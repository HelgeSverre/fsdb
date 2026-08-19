/// The scalar/aggregate function registry — every function call, including
/// built-ins like CONCAT, resolves through this one SQLite-style registry
/// via `registerScalar` rather than a hardcoded dispatch table.
module Fsdb.Functions

open System
open System.Globalization
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Fsdb.Value

/// A scalar function: its already-evaluated arguments in, one `Value` out.
type Scalar = Value list -> Value

/// How an extension function raises a *chosen* MySQL error code instead of
/// the generic 1105 the catch-all would give it — `raise (SqlError(1210,
/// "no such model"))` reaches the client as exactly that code/message.
/// Deliberately still an exception (not a `Result`-widening of `Scalar`):
/// builtins and the executor's evaluation pipeline stay untouched, and the
/// transaction-abort semantics of a throwing function stay identical to any
/// other failure (`QueryHandler.handle` clears `session.Tx` either way).
exception SqlError of code: int * message: string

/// What a rich extension function (`ScalarFunction`) sees about the query
/// it's executing inside. `Cancellation` is the killed-client token
/// (`Storage.queryCancellation`) — hosts hand it to `HttpClient` so a
/// network-calling function stops when its client vanishes. No async
/// executor: scalars block the connection thread for as long as they run.
type QueryContext =
    { Database: string option
      User: string
      Cancellation: System.Threading.CancellationToken }

/// The rich registration shape: a context-aware function plus the two
/// SQLite-style flags. Only `DirectOnly` is *enforced* (rejected inside
/// generated-column definitions — SQLite's DIRECTONLY rationale: a
/// network-calling UDF re-invoked by every later write is a footgun);
/// `Deterministic` is carried metadata for host-side caches.
/// ponytail: no planner constant-folding reads `Deterministic` — add that
/// only if repeated evaluation of a deterministic call ever shows up hot.
type ScalarFunction =
    { Name: string
      Fn: QueryContext -> Value list -> Value
      Deterministic: bool
      DirectOnly: bool }

module ScalarFunction =
    /// Deterministic, callable anywhere — the default shape.
    let create (name: string) (fn: QueryContext -> Value list -> Value) : ScalarFunction =
        { Name = name; Fn = fn; Deterministic = true; DirectOnly = false }

    /// The network-calling shape in one word: non-deterministic (results
    /// may differ per call) and direct-only (banned from generated-column
    /// definitions, where the engine — not the user's statement — would
    /// re-invoke it on every later write).
    let effectful (fn: ScalarFunction) : ScalarFunction =
        { fn with Deterministic = false; DirectOnly = true }

/// A host-registered read-only table, living in the reserved `fsdb` schema
/// (`SELECT * FROM fsdb.models`) — how an embedder exposes its own state
/// (a model registry, a metrics snapshot) to SQL without inserting rows.
/// `Rows` is pulled fresh once per statement that references the table; the
/// engine post-filters WHERE/JOIN over the full row list.
/// ponytail: no qual pushdown and no table-valued functions — add a
/// narrow-scan analogue only when a big virtual table hurts.
type VirtualTable =
    { Name: string
      Columns: Ast.ColumnDef list
      Rows: unit -> Value[] list }

module VirtualTable =
    /// The 11-field `ColumnDef` an embedder should never hand-build: every
    /// flag off, nullable, server-default collation/charset — the shape the
    /// `text`/`int`/... shorthands below all share.
    let private col (name: string) (ty: Ast.ColumnType) : Ast.ColumnDef =
        { Name = name
          Type = ty
          Nullable = true
          Default = None
          AutoIncrement = false
          PrimaryKey = false
          Unique = false
          OnUpdateCurrentTimestamp = false
          Generated = None
          Collation = None
          Charset = None }

    let text (name: string) : Ast.ColumnDef = col name Ast.TText
    let int (name: string) : Ast.ColumnDef = col name (Ast.TInt false)
    let bigint (name: string) : Ast.ColumnDef = col name (Ast.TBigInt false)
    let double (name: string) : Ast.ColumnDef = col name Ast.TDouble

    let create (name: string) (columns: Ast.ColumnDef list) (rows: unit -> Value[] list) : VirtualTable =
        { Name = name; Columns = columns; Rows = rows }

/// An aggregate function: the whole-row-set list of one already-evaluated,
/// already NULL-filtered `Value` per row, folded to one `Value` out —
/// `Executor.evalAggregate` owns evaluating the argument expression against
/// every row and dropping the NULLs (both need a `Store`/row context this
/// module doesn't have), so what lands here is always a nonempty plain
/// list. `COUNT(*)` doesn't evaluate any expression per row (`*` isn't a
/// valid `Expr` to evaluate) — `evalAggregate` special-cases it directly
/// rather than routing it through here.
type Aggregate = Value list -> Value

/// Case-insensitive by construction: every key is upper-invariant on the
/// way in, so `lookup`/`lookupAggregate` just normalize the same way rather
/// than needing a custom `IComparer`. A record (not a bare `Map` alias) so
/// `Aggregates` is a field here rather than a breaking change to what
/// `Registry` *is* everywhere it's named (`Executor.evalExpr`,
/// `QueryHandler.registryFor`, every test).
type Registry =
    { Scalars: Map<string, Scalar>
      Aggregates: Map<string, Aggregate>
      /// Rich (`QueryContext`-aware) registrations, kept separate from
      /// `Scalars` so builtins and plain `registerScalar` users never pay
      /// for the context plumbing — `QueryHandler.registryFor` collapses
      /// each entry to a plain `Scalar` by applying the per-statement
      /// context, and `Executor`'s DDL path reads the `DirectOnly` flag
      /// here to reject generated-column definitions.
      Extensions: Map<string, ScalarFunction> }

let empty: Registry =
    { Scalars = Map.empty
      Aggregates = Map.empty
      Extensions = Map.empty }

let registerScalar (name: string) (fn: Scalar) (registry: Registry) : Registry =
    { registry with Scalars = Map.add (name.ToUpperInvariant()) fn registry.Scalars }

let registerAggregate (name: string) (fn: Aggregate) (registry: Registry) : Registry =
    { registry with Aggregates = Map.add (name.ToUpperInvariant()) fn registry.Aggregates }

let registerExtension (fn: ScalarFunction) (registry: Registry) : Registry =
    { registry with Extensions = Map.add (fn.Name.ToUpperInvariant()) fn registry.Extensions }

let lookup (name: string) (registry: Registry) : Scalar option =
    Map.tryFind (name.ToUpperInvariant()) registry.Scalars

let lookupAggregate (name: string) (registry: Registry) : Aggregate option =
    Map.tryFind (name.ToUpperInvariant()) registry.Aggregates

// ---------------------------------------------------------------------------
// Built-ins, registered through the same `registerScalar` API user code
// gets — no special-casing for the ones that ship in the box.
// ---------------------------------------------------------------------------

let private concatFn (args: Value list) : Value =
    // MySQL: CONCAT returns NULL if any argument is NULL.
    if args |> List.exists (function VNull -> true | _ -> false) then
        VNull
    else
        args |> List.map (toText >> Option.defaultValue "") |> String.concat "" |> VString

let private textMap (f: string -> string) : Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] -> v |> toText |> Option.defaultValue "" |> f |> VString
    | _ -> VNull

/// True if any argument is NULL — the common case for multi-arg string/math
/// functions where MySQL's whole result is NULL if any input is.
let private anyNull (args: Value list) : bool =
    args |> List.exists (function VNull -> true | _ -> false)

/// `toText` defaulted to `""` — the common case once `anyNull` has already
/// ruled NULL out, so every call site isn't re-deriving the same default.
let private req (v: Value) : string = v |> toText |> Option.defaultValue ""

/// A value as the 64-bit pattern MySQL's bit-oriented functions (BIN, OCT,
/// CONV, HEX) read it as: those treat their argument as `BIGINT UNSIGNED`,
/// so `BIN(-1)` is 64 ones. `toDouble` can't be the route for the top half
/// of the domain — it saturates `int64` at 2^63-1 — so `VUInt`/`VInt` pass
/// through their exact bits and only inexact inputs go via `double`.
let private toUInt64 (v: Value) : uint64 =
    match v with
    | VUInt u -> u
    | VInt i -> uint64 i
    | _ ->
        let d = toDouble v

        if d >= 1.8446744073709552e19 then UInt64.MaxValue
        elif d < 0.0 then uint64 (int64 (max d -9.2233720368547758e18))
        else uint64 d

/// A numeric argument to an integer-domain builtin (BIT_COUNT, EXPORT_SET,
/// MAKEDATE, CONV's bases), rounded the way MySQL rounds it: DECIMAL half
/// away from zero, DOUBLE half to even. A *string* argument truncates
/// instead, so it passes through untouched and the caller's plain `int`
/// cast does the truncating (MySQL-verified: `BIT_COUNT(3.5)` = 1 — 3.5
/// rounds to 4 — where `BIT_COUNT('3.5')` = 2, truncating to 3).
///
/// Not folded into `toUInt64`: BIN/OCT/HEX are `CONV(N, 10, b)` in MySQL,
/// which reads its first argument as a *string*, so those truncate
/// (`BIN(2.5)` is '10', not '11').
let private roundNumeric (v: Value) : Value =
    match v with
    | VDecimal d -> VDecimal(Math.Round(d, MidpointRounding.AwayFromZero))
    | VDouble d -> VDouble(Math.Round(d, MidpointRounding.ToEven))
    | _ -> v

/// `LENGTH` counts UTF-8 bytes (MySQL's `LENGTH` is a byte length, not a
/// character count — that's `CHAR_LENGTH`). `VBytes`' own byte count is
/// used directly rather than round-tripping through `toText` (which decodes
/// raw bytes 1:1 as Latin-1 chars for display, and re-encoding *that* as
/// UTF-8 would inflate any byte ≥ 0x80 to two bytes).
let private lengthFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VBytes b ] -> VInt(int64 b.Length)
    | [ v ] -> v |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetByteCount |> int64 |> VInt
    | _ -> VNull

/// `CHAR_LENGTH` counts Unicode code points, not UTF-16 units — a surrogate
/// pair (an astral character) is one character, not two.
let private charLengthFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] ->
        let s = v |> toText |> Option.defaultValue ""
        let mutable n = 0
        let mutable i = 0

        while i < s.Length do
            i <- i + (if Char.IsHighSurrogate s.[i] && i + 1 < s.Length && Char.IsLowSurrogate s.[i + 1] then 2 else 1)
            n <- n + 1

        VInt(int64 n)
    | _ -> VNull

let private coalesceFn (args: Value list) : Value =
    args |> List.tryFind (function VNull -> false | _ -> true) |> Option.defaultValue VNull

let private ifNullFn: Scalar =
    function
    | [ VNull; b ] -> b
    | [ a; _ ] -> a
    | _ -> VNull

let private ifFn: Scalar =
    function
    | [ cond; a; b ] -> if truthy cond = Some true then a else b
    | _ -> VNull

let private absFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VInt i ] -> VInt(abs i)
    // Already non-negative and outside `int64`/`double`'s exact reach past
    // 2^63 — the generic `toDouble` arm below would answer 1.8446744073709552e19.
    | [ VUInt u ] -> VUInt u
    | [ VDouble d ] -> VDouble(abs d)
    | [ VDecimal d ] -> VDecimal(abs d)
    | [ v ] -> VDouble(abs (toDouble v))
    | _ -> VNull

/// `Math.Round` throws outside 0..15 (double) / 0..28 (decimal) digits, so
/// `ROUND(x, -n)` — MySQL rounds to the left of the decimal point, e.g.
/// `ROUND(123.456, -1) = 120` — needs its own scaling rather than passing a
/// negative digit count straight through. Used for digit counts outside the
/// BCL's supported range in either direction; a factor of 0/∞ (digits far
/// outside a double's meaningful exponent range) collapses to 0, matching
/// what rounding to a vastly-larger-than-the-value power of 10 means.
/// Approximate-value rounding is half-to-*even* (MySQL defers to the C
/// library here — `ROUND(2.5e0)` is 2, not 3, oracle-verified), unlike the
/// half-away-from-zero `roundDecimalAt` uses for exact values.
/// Past `Math.Round`'s 15-digit limit, scale up and divide back rather than
/// multiplying by the reciprocal: `5551 / 1e20` and `5551 * 1e-20` are
/// different doubles and MySQL's answer is the former.
let private roundDoubleAt (d: float) (digits: int) : float =
    if digits >= 0 && digits <= 15 then
        Math.Round(d, digits, MidpointRounding.ToEven)
    elif digits > 15 then
        let factor = Math.Pow(10.0, float digits)

        if Double.IsInfinity factor || Double.IsInfinity(d * factor) then
            d
        else
            Math.Round(d * factor, MidpointRounding.ToEven) / factor
    else
        let factor = Math.Pow(10.0, float -digits)
        if Double.IsInfinity factor || factor = 0.0 then 0.0
        else Math.Round(d / factor, MidpointRounding.ToEven) * factor

let private roundDecimalAt (d: decimal) (digits: int) : decimal =
    if digits >= 0 && digits <= 28 then
        Math.Round(d, digits, MidpointRounding.AwayFromZero)
    else
        try
            let factor = pown 10M -digits
            Math.Round(d / factor, MidpointRounding.AwayFromZero) * factor
        with :? OverflowException ->
            0M

/// ROUND(x) rounds to the nearest integer; ROUND(x, n) to `n` decimal
/// places (negative `n` rounds left of the point). Exact values (INT,
/// DECIMAL) round half away from zero; approximate ones (DOUBLE) round half
/// to even — MySQL's split, not one rule for both.
let private roundFn: Scalar =
    function
    | [ VNull ]
    | [ VNull; _ ] -> VNull
    | [ VInt i ] -> VInt i
    | [ VInt i; VInt digits ] -> if digits >= 0L then VInt i else VInt(int64 (roundDecimalAt (decimal i) (int digits)))
    // `BIGINT UNSIGNED` is exact and integral already, so a non-negative
    // digit count is the identity; rounding left of the point can leave the
    // unsigned domain (MySQL then raises 1690, which `Value.narrowUnsigned`
    // does for the arithmetic path — here the exact `DECIMAL` is the answer
    // MySQL gives for every in-domain case).
    | [ VUInt u ] -> VUInt u
    | [ VUInt u; VInt digits ] ->
        if digits >= 0L then
            VUInt u
        else
            Value.narrowUnsigned (roundDecimalAt (decimal u) (int digits))
    | [ VDecimal d ] -> VDecimal(Math.Round(d, MidpointRounding.AwayFromZero))
    | [ VDecimal d; VInt digits ] -> VDecimal(roundDecimalAt d (int digits))
    | [ VDouble d ] -> VDouble(Math.Round(d, MidpointRounding.ToEven))
    | [ VDouble d; VInt digits ] -> VDouble(roundDoubleAt d (int digits))
    | [ v ] -> VDouble(Math.Round(toDouble v, MidpointRounding.AwayFromZero))
    | [ v; VInt digits ] -> VDouble(roundDoubleAt (toDouble v) (int digits))
    | _ -> VNull

/// `MOD(a, b)` (and `%`, which desugars to this in `Parser`) — MySQL's
/// numeric-promotion rules, shared with `Value.add`/`sub`/`mul` rather than
/// re-deriving them here (a `DECIMAL` operand promotes to `DECIMAL`,
/// keeping its scale).
let private modFn: Scalar =
    function
    | [ a; b ] -> modulo a b
    | _ -> VNull

/// Drops the sub-second part of a `DateTime`. `DateTime.Now` carries 100 ns
/// ticks, but MySQL's `NOW()`/`CURRENT_TIMESTAMP` default to precision 0 (no
/// fraction); `Value.toText` renders any sub-second component, so the
/// current-time sources have to truncate at the source or they'd show
/// microseconds real MySQL never does.
let truncateToSecond (dt: DateTime) : DateTime =
    DateTime(dt.Ticks - dt.Ticks % TimeSpan.TicksPerSecond, dt.Kind)

/// Rounds a non-negative 100 ns tick count to `fsp` fractional-second digits
/// (fsp 0-6), MySQL's rounding for a value coerced into a `DATETIME(N)`/
/// `TIME(N)` column: half away from zero (`.5` rounds up, verified against
/// the oracle — `00:00:00.5`/`.01.5`/`.02.5` all round up, not banker's).
/// A tick is 10^-7 s, so one fsp digit is 10^(7-fsp) ticks; the rounding can
/// carry across seconds/minutes/days, which the plain tick arithmetic handles.
let roundTicksToFsp (fsp: int) (ticks: int64) : int64 =
    if fsp >= 7 then
        ticks
    else
        let unit = pown 10L (7 - fsp)
        let rem = ticks % unit
        ticks - rem + (if rem * 2L >= unit then unit else 0L)

/// `roundTicksToFsp` on a whole `DateTime` (see `Storage.coerceValue`'s
/// `DATETIME`/`TIMESTAMP` case).
let roundDateTimeToFsp (fsp: int) (dt: DateTime) : DateTime =
    DateTime(roundTicksToFsp fsp dt.Ticks, dt.Kind)

/// `NOW()`/`CURRENT_TIMESTAMP` (no arg, MySQL precision 0) truncate to whole
/// seconds; `NOW(N)`/`CURRENT_TIMESTAMP(N)` round the clock to N fractional
/// digits (`NOW(6)` → microseconds). N is clamped to 0-6 — MySQL raises 1426
/// on a larger fsp, but a scalar has no error channel, so this caps rather
/// than throwing a raw exception. `Executor.fspOfExpr` renders these at N
/// digits so an `NOW(3)` shows exactly three, not `toText`'s full six.
let private nowFn: Scalar =
    function
    | [ n ] when not (anyNull [ n ]) ->
        let fsp = toDouble n |> int |> max 0 |> min 6
        VDateTime(roundDateTimeToFsp fsp DateTime.Now)
    | _ -> VDateTime(truncateToSecond DateTime.Now)

// ---------------------------------------------------------------------------
// JSON. `VJson`/`VString` both hold raw JSON text (a JSON column coerces to
// `VString` today — `Storage.coerceValue`'s call, not this module's — so
// every function here reads through `tryParseJsonValue`, which treats the
// two the same rather than special-casing `VJson`). Parsed on demand with
// `System.Text.Json.Nodes.JsonNode`, whose object/array nodes are mutable in
// place, which is what makes JSON_SET/INSERT/REPLACE/REMOVE tractable
// without hand-rolling a second JSON tree type.
// ---------------------------------------------------------------------------

/// One step of a `$.a[2].b`-style path. The wildcards are the minimal
/// one-level forms, not a recursive descent operator — and they're two
/// distinct cases because MySQL keeps them apart (oracle-verified):
/// `$.*` fans out over an *object's* members only (`'[1,2]'` → no match)
/// and `$[*]` over an *array's* elements only (`'{"k":1}'` → no match) —
/// the distinction JSON_TABLE's pinned "object under `$[*]` expands to
/// zero rows" probe depends on.
type private JPath =
    | JKey of string
    | JIndex of int
    | JMemberWildcard
    | JElementWildcard

/// Parses MySQL's JSON path grammar: `$`, then any run of `.key`,
/// `."quoted key"`, `[n]`, `.*`, or `[*]`. Returns `None` on anything it
/// doesn't recognize rather than guessing.
let private parseJsonPathRaw (path: string) : JPath list option =
    if isNull path || path.Length = 0 || path.[0] <> '$' then
        None
    else
        let s = path.Substring(1)
        let segs = ResizeArray<JPath>()
        let mutable i = 0
        let mutable ok = true

        while ok && i < s.Length do
            match s.[i] with
            | '.' ->
                i <- i + 1

                if i < s.Length && s.[i] = '*' then
                    segs.Add JMemberWildcard
                    i <- i + 1
                elif i < s.Length && s.[i] = '"' then
                    let start = i + 1
                    let close = s.IndexOf('"', start)

                    if close < 0 then
                        ok <- false
                    else
                        segs.Add(JKey(s.Substring(start, close - start)))
                        i <- close + 1
                else
                    let start = i

                    while i < s.Length && (Char.IsLetterOrDigit s.[i] || s.[i] = '_') do
                        i <- i + 1

                    if i = start then ok <- false else segs.Add(JKey(s.Substring(start, i - start)))
            | '[' ->
                let close = s.IndexOf(']', i)

                if close < 0 then
                    ok <- false
                else
                    let inner = s.Substring(i + 1, close - i - 1).Trim()

                    if inner = "*" then
                        segs.Add JElementWildcard
                    else
                        match Int32.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                        | true, n -> segs.Add(JIndex n)
                        | false, _ -> ok <- false

                    i <- close + 1
            | _ -> ok <- false

        if ok then Some(List.ofSeq segs) else None

/// `parseJsonPathRaw`, memoized by path string: the path argument is almost
/// always a query literal, so the same string parses once per process rather
/// than once per row. Pure, so the cache never affects correctness — a rare
/// `GetOrAdd` race just re-parses harmlessly.
let private parseJsonPath : string -> JPath list option =
    let cache = System.Collections.Concurrent.ConcurrentDictionary<string, JPath list option>()
    // Path strings are attacker-controlled, so the cache is bounded: a flood
    // of distinct paths clears it rather than growing memory without limit.
    // Real workloads reuse a handful of literal paths and stay well under
    // the cap, keeping the memoization.
    let maxEntries = 4096

    fun path ->
        if cache.Count > maxEntries then cache.Clear()
        cache.GetOrAdd(path, parseJsonPathRaw)

/// MySQL's negative-counts-from-the-end array index (`$[-1]` is the last
/// element), bounds-checked against `a`'s actual length.
let private normIndex (a: JsonArray) (idx: int) : int option =
    let i = if idx < 0 then a.Count + idx else idx
    if i >= 0 && i < a.Count then Some i else None

/// Walks `node` along `segs`, returning every match (more than one only
/// when a wildcard segment fans out). A found JSON `null` is a valid
/// match — represented by a `null` `JsonNode` reference in the list — so
/// callers distinguish "found null" (`[null]`) from "not found" (`[]`).
let rec private navigateJson (node: JsonNode) (segs: JPath list) : JsonNode list =
    match segs with
    | [] -> [ node ]
    | JMemberWildcard :: rest ->
        match node with
        | :? JsonObject as o -> o |> Seq.collect (fun kv -> navigateJson kv.Value rest) |> List.ofSeq
        | _ -> []
    | JElementWildcard :: rest ->
        match node with
        | :? JsonArray as a -> a |> Seq.collect (fun v -> navigateJson v rest) |> List.ofSeq
        | _ -> []
    | JKey k :: rest ->
        match node with
        | :? JsonObject as o -> if o.ContainsKey k then navigateJson o.[k] rest else []
        | _ -> []
    | JIndex idx :: rest ->
        match node with
        | :? JsonArray as a ->
            match normIndex a idx with
            | Some i -> navigateJson a.[i] rest
            | None -> []
        // MySQL treats a non-array (including a scalar and a JSON null) as a
        // one-element array for indexing: `JSON_EXTRACT(CAST('{}' AS JSON),
        // '$[0]')` is `{}`, not NULL. Only index 0 (or -1, the same element
        // counted from the end) hits; anything else misses.
        | _ -> if idx = 0 || idx = -1 then navigateJson node rest else []

/// MySQL escapes a quote inside a JSON string as `\"` and leaves `<`, `>`,
/// `&` alone; `System.Text.Json`'s default encoder emits `\u0022`/`\u003C`
/// for the same characters, which is legal JSON but not MySQL's rendering.
let private jsonRenderOptions =
    JsonSerializerOptions(Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

/// Quotes a JSON string literal exactly as MySQL's JSON printer does,
/// oracle-verified: `"` and `\` backslash-escaped, `\b \f \n \r \t` in their
/// short forms, every other C0 control as `\u00xx`, and *everything else* —
/// DEL, `<`/`&`/`>`/`/`, and characters outside the BMP alike — emitted
/// literally. `System.Text.Json` cannot be configured into that last part:
/// even `UnsafeRelaxedJsonEscaping` (and `JavaScriptEncoder.Create
/// UnicodeRanges.All`) splits a supplementary-plane character such as an
/// emoji into an escaped UTF-16 surrogate pair, where MySQL emits the
/// character itself.
let internal jsonQuote (s: string) : string =
    let sb = Text.StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore

    for c in s do
        match c with
        | '"' -> sb.Append "\\\"" |> ignore
        | '\\' -> sb.Append "\\\\" |> ignore
        | '\b' -> sb.Append "\\b" |> ignore
        | '\f' -> sb.Append "\\f" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when c < ' ' -> sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        | c -> sb.Append c |> ignore

    sb.Append('"').ToString()

/// Renders a `JsonNode` the way MySQL's JSON printer does: a space after
/// every `:` and `,`, recursively, and object keys in MySQL's *stored*
/// order — shortest first, ties broken lexicographically — not the order
/// they were written in. (`JsonNode.ToJsonString()` alone is compact with no
/// spaces and keeps insertion order, which reads as JSON but not as
/// *MySQL's* JSON.)
let rec private formatJsonNode (node: JsonNode) : string =
    match node with
    | null -> "null"
    | :? JsonObject as o ->
        "{"
        + (o
           |> Seq.sortWith (fun a b ->
               match Operators.compare a.Key.Length b.Key.Length with
               | 0 -> String.CompareOrdinal(a.Key, b.Key)
               | other -> other)
           |> Seq.map (fun kv -> jsonQuote kv.Key + ": " + formatJsonNode kv.Value)
           |> String.concat ", ")
        + "}"
    | :? JsonArray as a -> "[" + (a |> Seq.map formatJsonNode |> String.concat ", ") + "]"
    | _ when node.GetValueKind() = JsonValueKind.String -> jsonQuote (node.GetValue<string>())
    | _ -> node.ToJsonString jsonRenderOptions

// JSON_TABLE's hooks into this module's private path machinery —
// `Executor.jsonTableRows` compiles later and can't see the private
// helpers directly, and re-implementing the path walker there would be a
// second grammar to keep in sync.

/// Parses a document's text as JSON. `Error` = malformed (the executor's
/// 3141) — distinct from a legal top-level `null`, which parses to a null
/// node inside `Ok`.
let jsonParseDocument (text: string) : Result<JsonNode, unit> =
    try
        Ok(JsonNode.Parse text)
    with _ ->
        Error()

/// Every node `path` matches under `root` (`None` = unparseable path); a
/// `null` element is a matched JSON `null`, not a miss.
let jsonPathNodes (root: JsonNode) (path: string) : JsonNode list option =
    parseJsonPath path |> Option.map (navigateJson root)

/// MySQL-style JSON text for a node (the `", "`-spaced printer above).
let jsonNodeText (node: JsonNode) : string = formatJsonNode node

/// Parses a `Value`'s text as a JSON document. `None` for NULL or text that
/// isn't valid JSON — every JSON function here has no error channel (`Scalar`
/// returns a plain `Value`, not a `Result`), so invalid JSON in becomes NULL
/// out rather than a raised error, unlike real MySQL's 3140.
let private tryParseJsonValue (v: Value) : JsonNode option =
    match v with
    | VNull -> None
    | _ ->
        match toText v with
        | None -> None
        | Some s ->
            try
                Some(JsonNode.Parse s)
            with _ ->
                None

/// The inverse of `tryParseJsonValue`: a scalar `Value` becomes the `JsonNode`
/// it denotes as a JSON value — numbers/strings/bool/null map directly,
/// `VJson`/pre-existing JSON text is parsed and spliced in as a subdocument
/// (falling back to a JSON string if it isn't valid JSON), and dates render
/// through their normal text form, same as MySQL storing them as JSON strings.
let private valueToJsonNode (v: Value) : JsonNode =
    match v with
    | VNull -> null
    | VInt i -> JsonValue.Create i
    | VUInt u -> JsonValue.Create u
    | VDouble d -> JsonValue.Create d
    | VDecimal d -> JsonValue.Create d
    | VJson j ->
        try
            JsonNode.Parse j
        with _ ->
            JsonValue.Create j
    | _ -> JsonValue.Create(v |> toText |> Option.defaultValue "")

let private boolToInt (b: bool) : Value = VInt(if b then 1L else 0L)

/// MySQL's JSON_CONTAINS: an object contains a candidate object if every
/// candidate key is present with a containing value; an array contains a
/// candidate array if every candidate element is contained *somewhere* in
/// the target array (order-independent), and contains a candidate scalar
/// the same way; otherwise it's scalar-vs-scalar equality by JSON kind.
let rec private jsonContains (target: JsonNode) (candidate: JsonNode) : bool =
    match target, candidate with
    | null, null -> true
    | null, _
    | _, null -> false
    | (:? JsonObject as t), (:? JsonObject as c) ->
        c |> Seq.forall (fun kv -> t.ContainsKey kv.Key && jsonContains t.[kv.Key] kv.Value)
    | (:? JsonArray as t), (:? JsonArray as c) -> c |> Seq.forall (fun cv -> t |> Seq.exists (fun tv -> jsonContains tv cv))
    | (:? JsonArray as t), _ -> t |> Seq.exists (fun tv -> jsonContains tv candidate)
    | _ ->
        try
            target.GetValueKind() = candidate.GetValueKind() && formatJsonNode target = formatJsonNode candidate
        with _ ->
            false

/// JSON_LENGTH: object/array count their members/elements; a scalar (and
/// JSON null) counts as length 1, matching MySQL.
let private jsonNodeLength (node: JsonNode) : int =
    match node with
    | :? JsonObject as o -> o.Count
    | :? JsonArray as a -> a.Count
    | _ -> 1

let private jsonExtractFn: Scalar =
    function
    | doc :: (_ :: _ as pathArgs) when not (anyNull (doc :: pathArgs)) ->
        match tryParseJsonValue doc with
        | None -> VNull
        | Some root ->
            let paths = pathArgs |> List.map (fun p -> toText p |> Option.bind parseJsonPath)

            if paths |> List.exists Option.isNone then
                VNull
            else
                let matches = paths |> List.collect (fun p -> navigateJson root p.Value)

                match matches, pathArgs with
                | [], _ -> VNull
                | [ single ], [ _ ] -> VJson(formatJsonNode single)
                | many, _ -> VJson("[" + (many |> List.map formatJsonNode |> String.concat ", ") + "]")
    | _ -> VNull

let private jsonUnquoteFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] ->
        match toText v with
        | None -> VNull
        | Some s ->
            try
                match JsonNode.Parse s with
                | null -> VString "null"
                | node when node.GetValueKind() = JsonValueKind.String -> VString(node.GetValue<string>())
                | node -> VString(formatJsonNode node)
            with _ ->
                // Not valid JSON text — MySQL would raise 3146; tolerated
                // here as a pass-through since `Scalar` has no error channel.
                VString s
    | _ -> VNull

let private jsonContainsFn: Scalar =
    function
    | [ t; c ] when not (anyNull [ t; c ]) ->
        match tryParseJsonValue t, tryParseJsonValue c with
        | Some tn, Some cn -> boolToInt (jsonContains tn cn)
        | _ -> VNull
    | [ t; c; p ] when not (anyNull [ t; c; p ]) ->
        match tryParseJsonValue t, toText p |> Option.bind parseJsonPath, tryParseJsonValue c with
        | Some tn, Some segs, Some cn ->
            match navigateJson tn segs with
            | [] -> VNull
            | matches -> boolToInt (matches |> List.exists (fun m -> jsonContains m cn))
        | _ -> VNull
    | _ -> VNull

let private jsonLengthFn: Scalar =
    function
    | [ doc ] when not (anyNull [ doc ]) -> tryParseJsonValue doc |> Option.map (jsonNodeLength >> int64 >> VInt) |> Option.defaultValue VNull
    | [ doc; p ] when not (anyNull [ doc; p ]) ->
        match tryParseJsonValue doc, toText p |> Option.bind parseJsonPath with
        | Some root, Some segs ->
            match navigateJson root segs with
            | [ m ] -> VInt(int64 (jsonNodeLength m))
            | _ -> VNull
        | _ -> VNull
    | _ -> VNull

/// JSON_DEPTH: a scalar (and an *empty* array/object) is depth 1; a
/// non-empty container is 1 + the deepest member. MySQL-verified:
/// `JSON_DEPTH('[]')` = 1, `JSON_DEPTH('[1,[2,[3]]]')` = 4.
let rec private jsonNodeDepth (node: JsonNode) : int =
    match node with
    | :? JsonObject as o when o.Count > 0 -> 1 + (o |> Seq.map (fun kv -> jsonNodeDepth kv.Value) |> Seq.max)
    | :? JsonArray as a when a.Count > 0 -> 1 + (a |> Seq.map jsonNodeDepth |> Seq.max)
    | _ -> 1

let private jsonDepthFn: Scalar =
    function
    | [ doc ] when not (anyNull [ doc ]) -> tryParseJsonValue doc |> Option.map (jsonNodeDepth >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private jsonValidFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] ->
        match toText v with
        | None -> VNull
        | Some s ->
            try
                JsonNode.Parse s |> ignore
                VInt 1L
            with _ ->
                VInt 0L
    | _ -> VNull

/// MySQL's JSON_TYPE names, collapsed to what a `JsonNode` can actually tell
/// apart: numbers only split INTEGER/DOUBLE by whether the source text has a
/// fraction or exponent, not MySQL's full INTEGER/UNSIGNED INTEGER/DECIMAL
/// split (this engine doesn't retain that provenance).
let private jsonTypeOf (node: JsonNode) : string =
    match node with
    | null -> "NULL"
    | :? JsonObject -> "OBJECT"
    | :? JsonArray -> "ARRAY"
    | _ ->
        match node.GetValueKind() with
        | JsonValueKind.String -> "STRING"
        | JsonValueKind.True
        | JsonValueKind.False -> "BOOLEAN"
        | JsonValueKind.Number ->
            let raw = node.ToJsonString()
            if raw.IndexOfAny [| '.'; 'e'; 'E' |] >= 0 then "DOUBLE" else "INTEGER"
        | _ -> "OPAQUE"

let private jsonTypeFn: Scalar =
    function
    | [ doc ] when not (anyNull [ doc ]) ->
        match tryParseJsonValue doc with
        | Some node -> VString(jsonTypeOf node)
        | None -> VNull
    | _ -> VNull

let private jsonKeysOf (node: JsonNode option) : Value =
    match node with
    | Some(:? JsonObject as o) -> VJson("[" + (o |> Seq.map (fun kv -> jsonQuote kv.Key) |> String.concat ", ") + "]")
    | _ -> VNull

let private jsonKeysFn: Scalar =
    function
    | [ doc ] when not (anyNull [ doc ]) -> jsonKeysOf (tryParseJsonValue doc)
    | [ doc; p ] when not (anyNull [ doc; p ]) ->
        match tryParseJsonValue doc, toText p |> Option.bind parseJsonPath with
        | Some root, Some segs ->
            match navigateJson root segs with
            | [ m ] -> jsonKeysOf (Some m)
            | _ -> VNull
        | _ -> VNull
    | _ -> VNull

/// JSON_SET/JSON_INSERT/JSON_REPLACE's shared write semantics: `Set` always
/// writes, `Insert` only writes if the target doesn't already exist,
/// `Replace` only writes if it does. ponytail: no auto-vivification of
/// missing intermediate containers (MySQL doesn't either — only the final
/// path leg may be created against an existing object, or one past the end
/// of an existing array), add it if a migration path needs deeper creation.
type private JsonWriteMode =
    | JSet
    | JInsert
    | JReplace

/// Walks to the container holding `segs`' final path segment and applies
/// `act` to it — the shared spine of `setJsonPath`/`removeJsonPath`, so a
/// fix to how either one traverses a *nested* path (as opposed to what it
/// does once it gets there) automatically applies to both.
let rec private atLeaf (root: JsonNode) (segs: JPath list) (act: JsonNode -> JPath -> unit) : unit =
    match segs with
    | [ leaf ] -> act root leaf
    | JKey k :: rest ->
        match root with
        | :? JsonObject as o when o.ContainsKey k && not (isNull o.[k]) -> atLeaf o.[k] rest act
        | _ -> ()
    | JIndex idx :: rest ->
        match root with
        | :? JsonArray as a ->
            match normIndex a idx with
            | Some i when not (isNull a.[i]) -> atLeaf a.[i] rest act
            | _ -> ()
        | _ -> ()
    | JMemberWildcard :: _
    | JElementWildcard :: _
    | [] -> ()

let private setJsonPath (root: JsonNode) (segs: JPath list) (value: JsonNode) (mode: JsonWriteMode) : unit =
    atLeaf root segs (fun parent leaf ->
        match parent, leaf with
        | (:? JsonObject as o), JKey k ->
            match mode, o.ContainsKey k with
            | JInsert, true
            | JReplace, false -> ()
            | _ -> o.[k] <- value
        | (:? JsonArray as a), JIndex idx ->
            match normIndex a idx with
            | Some i ->
                match mode with
                | JInsert -> ()
                | _ -> a.[i] <- value
            | None ->
                let i = if idx < 0 then a.Count + idx else idx

                if i = a.Count then
                    match mode with
                    | JReplace -> ()
                    | _ -> a.Add value
        | _ -> ())

/// Splits a flat `[path; value; path; value; ...]` arg list into pairs —
/// `List.chunkBySize 2` would work too but leaves an unavoidable
/// non-exhaustive match warning on its length-2 sublists.
let rec private pairsOf (args: Value list) : (Value * Value) list =
    match args with
    | p :: v :: rest -> (p, v) :: pairsOf rest
    | _ -> []

let private jsonWriteFn (mode: JsonWriteMode) : Scalar =
    function
    | doc :: rest when rest.Length >= 2 && rest.Length % 2 = 0 && not (anyNull (doc :: rest)) ->
        match tryParseJsonValue doc with
        | None -> VNull
        | Some root0 ->
            let mutable root = root0

            for path, v in pairsOf rest do
                match toText path |> Option.bind parseJsonPath with
                | Some [] ->
                    match mode with
                    | JInsert -> ()
                    | _ -> root <- valueToJsonNode v
                | Some segs -> setJsonPath root segs (valueToJsonNode v) mode
                | None -> ()

            VJson(formatJsonNode root)
    | _ -> VNull

let private removeJsonPath (root: JsonNode) (segs: JPath list) : unit =
    atLeaf root segs (fun parent leaf ->
        match parent, leaf with
        | (:? JsonObject as o), JKey k -> o.Remove k |> ignore
        | (:? JsonArray as a), JIndex idx ->
            match normIndex a idx with
            | Some i -> a.RemoveAt i
            | None -> ()
        | _ -> ())

let private jsonRemoveFn: Scalar =
    function
    | doc :: (_ :: _ as paths) when not (anyNull (doc :: paths)) ->
        match tryParseJsonValue doc with
        | None -> VNull
        | Some root ->
            for p in paths do
                match toText p |> Option.bind parseJsonPath with
                | Some segs -> removeJsonPath root segs
                | None -> ()

            VJson(formatJsonNode root)
    | _ -> VNull

let private jsonArrayFn: Scalar = fun args -> VJson(formatJsonNode (JsonArray(args |> List.map valueToJsonNode |> Array.ofList)))

let private jsonObjectFn: Scalar =
    fun args ->
        if args.Length % 2 <> 0 then
            VNull
        else
            let o = JsonObject()

            for k, v in pairsOf args do
                o.[req k] <- valueToJsonNode v

            VJson(formatJsonNode o)

/// `JSON_ARRAYAGG`'s fold. Not a `registerAggregate` entry: the executor's
/// generic aggregate path drops NULL rows before folding, but MySQL keeps
/// them as JSON `null` (`JSON_ARRAYAGG(x)` over `1, NULL` is `[1, null]`),
/// so `Executor.evalAggregate` calls this with the raw per-row values.
let jsonArrayAggregate (values: Value list) : Value =
    VJson(formatJsonNode (JsonArray(values |> List.map valueToJsonNode |> Array.ofList)))

/// `JSON_OBJECTAGG`'s fold — two arguments per row, which the executor's
/// generic single-argument aggregate path can't express either. A NULL key
/// is MySQL error 3158; a duplicate key keeps the last row's value.
let jsonObjectAggregate (pairs: (Value * Value) list) : Value =
    let o = JsonObject()

    for k, v in pairs do
        match k with
        | VNull -> raise (SqlError(3158, "JSON documents may not contain NULL member names."))
        | _ -> o.[req k] <- valueToJsonNode v

    VJson(formatJsonNode o)

/// Every JSON string leaf under `node`, paired with its path — the search
/// space for `JSON_SEARCH`.
let rec private collectJsonStrings (node: JsonNode) (path: string) : (string * string) list =
    match node with
    | null -> []
    | :? JsonObject as o -> o |> Seq.collect (fun kv -> collectJsonStrings kv.Value (path + "." + kv.Key)) |> List.ofSeq
    | :? JsonArray as a ->
        a |> Seq.indexed |> Seq.collect (fun (i, v) -> collectJsonStrings v (sprintf "%s[%d]" path i)) |> List.ofSeq
    | _ -> if node.GetValueKind() = JsonValueKind.String then [ path, node.GetValue<string>() ] else []

/// Minimal `JSON_SEARCH(doc, 'one'|'all', search_str)` — ponytail: no
/// `escape_char`/restricting `path` argument support, add them if a
/// migration's search actually needs escaped wildcards or a narrowed scope.
let private jsonSearchFn: Scalar =
    function
    | doc :: modeV :: searchV :: _ when not (anyNull [ doc; modeV; searchV ]) ->
        match tryParseJsonValue doc, toText searchV with
        | Some root, Some search ->
            let rx = Regex(likeToRegex search, RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
            let matches = collectJsonStrings root "$" |> List.filter (snd >> rx.IsMatch) |> List.map fst

            match matches, (toText modeV |> Option.defaultValue "one").ToUpperInvariant() with
            | [], _ -> VNull
            | ps, "ALL" -> VJson("[" + (ps |> List.map jsonQuote |> String.concat ", ") + "]")
            | p :: _, _ -> VJson(jsonQuote p)
        | _ -> VNull
    | _ -> VNull

/// `CONVERT(expr USING charset)` — MySQL's transcode form, desugared by the
/// parser into this scalar (second argument = the charset name). Same
/// write-time semantics as column charsets: latin1 is cp1252 (so `€`
/// survives, unencodables map to '?'), ascii keeps 7-bit and maps the rest
/// to '?', binary returns raw UTF-8 bytes; the common utf8mb4 aliases pass
/// text through unchanged.
let private convertFn: Scalar =
    function
    | [ VNull; _ ] -> VNull
    | [ v; VString charset ] ->
        let text = v |> toText |> Option.defaultValue ""

        match charset.ToLowerInvariant() with
        | "utf8mb4"
        | "utf8" -> VString text
        | "latin1" -> VString(Collation.Charset.transcodeLatin1 text)
        | "ascii" -> VString(Collation.Charset.transcodeAscii text)
        | "binary" -> VBytes(Text.Encoding.UTF8.GetBytes text)
        | _ -> VNull
    | _ -> VNull

// ---------------------------------------------------------------------------
// Dates. `NOW()`/`CURRENT_TIMESTAMP` already exist above; everything else
// MySQL's date/time surface needs for a Laravel app (timestamps, `Carbon`
// comparisons, `whereDate`, etc.) lives here.
// ---------------------------------------------------------------------------

let private dateTimeFormats =
    [| "yyyy-MM-dd HH:mm:ss"
       "yyyy-MM-dd"
       "yyyy-MM-ddTHH:mm:ss"
       "yyyy/MM/dd" |]

/// Parses any `Value` as a `DateTime` the way MySQL's implicit date cast
/// does: real date/datetime values pass through, everything else parses its
/// text (first against MySQL's own common formats, then .NET's general
/// parser as a fallback) — `None` rather than an error for anything that
/// doesn't look like a date.
let private asDateTime (v: Value) : DateTime option =
    match v with
    | VDateTime dt -> Some dt
    | VDate d -> Some(d.ToDateTime TimeOnly.MinValue)
    | VNull -> None
    | _ ->
        match toText v with
        | None -> None
        | Some s ->
            match DateTime.TryParseExact(s.Trim(), dateTimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, dt -> Some dt
            | false, _ ->
                match DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None) with
                | true, dt -> Some dt
                | false, _ -> None

let private asDateOnly (v: Value) : DateOnly option = asDateTime v |> Option.map DateOnly.FromDateTime

let private dateOnlyUnits = set [ "DAY"; "WEEK"; "MONTH"; "QUARTER"; "YEAR" ]

/// Adds an interval, returning `None` when the result falls outside
/// `DateTime`'s range — MySQL yields NULL for an out-of-range temporal
/// rather than erroring. `int amount` for the month/year units can itself
/// overflow, so those are bounds-checked before the conversion.
let private addInterval (dt: DateTime) (amount: float) (unit: string) : DateTime option =
    let monthsAmount (scale: int) =
        let scaled = amount * float scale
        if Double.IsNaN scaled || abs scaled > 1.0e8 then None else Some(int scaled)

    try
        match unit.ToUpperInvariant() with
        | "SECOND" -> Some(dt.AddSeconds amount)
        | "MINUTE" -> Some(dt.AddMinutes amount)
        | "HOUR" -> Some(dt.AddHours amount)
        | "DAY" -> Some(dt.AddDays amount)
        | "WEEK" -> Some(dt.AddDays(amount * 7.0))
        | "MONTH" -> monthsAmount 1 |> Option.map dt.AddMonths
        | "QUARTER" -> monthsAmount 3 |> Option.map dt.AddMonths
        | "YEAR" -> (if abs amount > 1.0e8 then None else Some(int amount)) |> Option.map dt.AddYears
        | "MICROSECOND" -> Some(dt.AddTicks(int64 (amount * 10.0)))
        // An unrecognized unit used to return `dt` unchanged, which is a
        // silent wrong answer; NULL is at least an honest refusal. Every
        // composite unit is normalized into this vocabulary before it gets
        // here (see `compositeIntervalUnits`).
        | _ -> None
    with :? ArgumentOutOfRangeException ->
        None

/// MySQL's composite `INTERVAL` units take a *string* of several components
/// with any punctuation between them ('2-3' YEAR_MONTH, '1:30' HOUR_MINUTE,
/// '1 2:3:4' DAY_SECOND), and a value with fewer components than the unit
/// names is read as its *rightmost* ones. Each entry is the per-component
/// multiplier list plus the simple unit the total is expressed in, so
/// `addInterval` never has to know these exist.
let private compositeIntervalUnits =
    dict
        [ "YEAR_MONTH", ([ 12.0; 1.0 ], "MONTH")
          "DAY_HOUR", ([ 86400.0; 3600.0 ], "SECOND")
          "DAY_MINUTE", ([ 86400.0; 3600.0; 60.0 ], "SECOND")
          "DAY_SECOND", ([ 86400.0; 3600.0; 60.0; 1.0 ], "SECOND")
          "HOUR_MINUTE", ([ 3600.0; 60.0 ], "SECOND")
          "HOUR_SECOND", ([ 3600.0; 60.0; 1.0 ], "SECOND")
          "MINUTE_SECOND", ([ 60.0; 1.0 ], "SECOND")
          "SECOND_MICROSECOND", ([ 1.0e6; 1.0 ], "MICROSECOND")
          "MINUTE_MICROSECOND", ([ 6.0e7; 1.0e6; 1.0 ], "MICROSECOND")
          "HOUR_MICROSECOND", ([ 3.6e9; 6.0e7; 1.0e6; 1.0 ], "MICROSECOND")
          "DAY_MICROSECOND", ([ 8.64e10; 3.6e9; 6.0e7; 1.0e6; 1.0 ], "MICROSECOND") ]

let private parseCompositeInterval (weights: float list) (simpleUnit: string) (text: string) : float option =
    let digitRuns = Regex.Matches(text, @"\d+") |> Seq.map (fun m -> m.Value) |> List.ofSeq

    // Right-align value against unit: too few components means the leftmost
    // ones were left out ('3:4' DAY_SECOND is 3 minutes 4 seconds), while too
    // many is simply malformed — oracle-pinned: '1 2:3:4' HOUR_MINUTE,
    // '1:2:3' HOUR_MINUTE and '1-2-3' YEAR_MONTH are all NULL in 8.4.11.
    if digitRuns.IsEmpty || digitRuns.Length > weights.Length then
        None
    else
        let keep = min digitRuns.Length weights.Length
        let w = weights |> List.skip (weights.Length - keep)

        let p =
            digitRuns
            |> List.skip (digitRuns.Length - keep)
            // The trailing microsecond component is a decimal *fraction*, not
            // a count: '1.5' SECOND_MICROSECOND is 1.5 s (1500000 µs), not
            // 1 s plus 5 µs — oracle-pinned, so it right-pads to six digits.
            |> List.mapi (fun i s ->
                if simpleUnit = "MICROSECOND" && i = keep - 1 then
                    float (s.PadRight(6, '0').Substring(0, 6))
                else
                    float s)

        let total = List.map2 (*) w p |> List.sum
        Some(if text.TrimStart().StartsWith "-" then -total else total)

/// `Parser.fs`'s `INTERVAL n UNIT` grammar desugars to
/// `FuncCall("INTERVAL", [n; Lit(VString UNIT)])` (no separate `Interval`
/// AST node), which `evalExpr` evaluates like any other function call
/// before `DATE_ADD`/`DATE_SUB` see it — so the marker string this scalar
/// returns is exactly `dateAddCore`'s 2-arg-form input. Still independently
/// testable by evaluating the registered functions with `Value` args
/// directly.
let private intervalMarker = "\x01INTERVAL\x01"

let private intervalFn: Scalar =
    function
    | [ amt; unit ] -> VString(intervalMarker + req amt + "\x01" + req unit)
    | _ -> VNull

/// Reads the `INTERVAL` encoding above, or tolerates a plain `"N UNIT"`
/// string (e.g. `DATE_ADD(d, '1 DAY')`) as a fallback shape.
let private tryParseIntervalArg (v: Value) : (float * string) option =
    match v with
    | VString s when s.StartsWith intervalMarker ->
        match s.Substring(intervalMarker.Length).Split('\x01') with
        | [| n; u |] ->
            match compositeIntervalUnits.TryGetValue(u.ToUpperInvariant()) with
            | true, (weights, simpleUnit) -> parseCompositeInterval weights simpleUnit n |> Option.map (fun total -> total, simpleUnit)
            | _ ->
                match Double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture) with
                | true, d -> Some(d, u)
                | false, _ -> None
        | _ -> None
    | VString s ->
        let m = Regex.Match(s.Trim(), @"^(-?\d+(?:\.\d+)?)\s+([A-Za-z]+)$")
        if m.Success then
            Some(Double.Parse(m.Groups.[1].Value, CultureInfo.InvariantCulture), m.Groups.[2].Value.ToUpperInvariant())
        else
            None
    | _ -> None

/// Whether `v` names a date with no time-of-day component — a real `VDate`,
/// or (MySQL doesn't type its string literals, so `DATE_ADD('2024-01-15',
/// INTERVAL 1 DAY)` is still expected to answer with a plain date, not a
/// midnight datetime) a `VString` that doesn't itself look like it carries
/// a time part.
let private looksDateOnly (v: Value) : bool =
    match v with
    | VDate _ -> true
    | VString s -> not (s.Contains ':')
    | _ -> false

let private applyDateInterval (sign: float) (dateV: Value) (dt: DateTime) (amount: float) (unit: string) : Value =
    match addInterval dt (sign * amount) unit with
    | None -> VNull // out of range — MySQL yields NULL
    | Some result ->
        if looksDateOnly dateV && dateOnlyUnits.Contains(unit.ToUpperInvariant()) then
            VDate(DateOnly.FromDateTime result)
        else
            VDateTime result

let private dateAddCore (sign: float) : Scalar =
    function
    | [ dateV; intervalV ] when not (anyNull [ dateV; intervalV ]) ->
        match asDateTime dateV, tryParseIntervalArg intervalV with
        | Some dt, Some(n, unit) -> applyDateInterval sign dateV dt n unit
        | _ -> VNull
    | [ dateV; amtV; VString unit ] when not (anyNull [ dateV; amtV ]) ->
        match asDateTime dateV with
        | Some dt -> applyDateInterval sign dateV dt (toDouble amtV) unit
        | None -> VNull
    | _ -> VNull

/// `TIMESTAMPADD(unit, n, expr)` — the same arithmetic `DATE_ADD(expr,
/// INTERVAL n unit)` does, with the arguments in the other order (the unit
/// arrives as argument one from `Parser.timestampFuncAtom`).
let private timestampAddFn: Scalar =
    function
    | [ u; n; d ] when not (anyNull [ u; n; d ]) ->
        dateAddCore 1.0 [ d; n; VString(toText u |> Option.defaultValue "") ]
    | _ -> VNull

/// `ADDDATE`/`SUBDATE` additionally accept a bare number as the second
/// argument (`ADDDATE(d, 3)` means 3 DAYs) where `DATE_ADD`/`DATE_SUB`
/// require the `INTERVAL` form — falls back to `dateAddCore` for the
/// `INTERVAL`/3-arg shapes so both spellings share one implementation.
let private addSubDateCore (sign: float) : Scalar =
    function
    | [ dateV; amtV ] when not (anyNull [ dateV; amtV ]) ->
        match asDateTime dateV with
        | None -> VNull
        | Some dt ->
            match tryParseIntervalArg amtV with
            | Some(n, unit) -> applyDateInterval sign dateV dt n unit
            | None -> applyDateInterval sign dateV dt (toDouble amtV) "DAY"
    | args -> dateAddCore sign args

/// Whether `v` is the encoded result of an `INTERVAL n unit` expression
/// (see `intervalFn` above) — the marker `Executor.evalExpr`'s `BinOp(Add,
/// ...)`/`BinOp(Sub, ...)` cases check before falling back to plain numeric
/// `Value.add`/`Value.sub`, since `datetime_expr +/- INTERVAL n unit` reaches
/// them as an ordinary binary operator, not a call to `DATE_ADD`/`DATE_SUB`.
let isIntervalValue (v: Value) : bool =
    match v with
    | VString s -> s.StartsWith intervalMarker
    | _ -> false

/// Real date/time arithmetic for that binary-operator form — same encoding
/// and unit handling as `dateAddCore`, just entered through `+`/`-` on an
/// `INTERVAL n unit` operand instead of a `DATE_ADD`/`DATE_SUB` call. `None`
/// when `dateV` isn't a recognizable date/time, so the caller can fall back
/// to `Value.add`/`Value.sub` and get MySQL's usual type-error/NULL there.
let tryDateIntervalBinOp (sign: float) (dateV: Value) (intervalV: Value) : Value option =
    match tryParseIntervalArg intervalV, asDateTime dateV with
    | Some(n, unit), Some dt -> Some(applyDateInterval sign dateV dt n unit)
    // A real `INTERVAL n unit` operand never degrades into numeric addition:
    // MySQL answers NULL when the left side isn't a date ('abc' + INTERVAL 1
    // DAY) or the interval value is malformed ('1:2:3' HOUR_MINUTE), whereas
    // `Value.add` would coerce the encoded marker string to a number and
    // return a silently wrong 2020.
    | _ when isIntervalValue intervalV -> Some VNull
    | _ -> None

let private dateDiffFn: Scalar =
    function
    | [ a; b ] when not (anyNull [ a; b ]) ->
        match asDateOnly a, asDateOnly b with
        | Some da, Some db -> VInt(int64 (da.DayNumber - db.DayNumber))
        | _ -> VNull
    | _ -> VNull

/// The common `DATE_FORMAT`/`FROM_UNIXTIME` specifiers — MySQL's `%x` table
/// has far more (week-numbering variants, locale names, ...); this is the
/// subset a Laravel app's `Carbon::format`-equivalent queries actually hit.
let private formatDate (dt: DateTime) (fmt: string) : string =
    let sb = StringBuilder()
    let mutable i = 0

    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            let piece =
                match fmt.[i + 1] with
                | 'Y' -> dt.Year.ToString("D4")
                | 'y' -> (dt.Year % 100).ToString("D2")
                | 'm' -> dt.Month.ToString("D2")
                | 'c' -> string dt.Month
                | 'd' -> dt.Day.ToString("D2")
                | 'e' -> string dt.Day
                | 'H' -> dt.Hour.ToString("D2")
                | 'h'
                | 'I' -> (let h = dt.Hour % 12 in (if h = 0 then 12 else h)).ToString("D2")
                | 'i' -> dt.Minute.ToString("D2")
                | 's'
                | 'S' -> dt.Second.ToString("D2")
                | 'p' -> if dt.Hour < 12 then "AM" else "PM"
                | 'W' -> dt.DayOfWeek.ToString()
                | 'a' -> (dt.DayOfWeek.ToString()).Substring(0, 3)
                | 'M' -> CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName dt.Month
                | 'b' -> CultureInfo.InvariantCulture.DateTimeFormat.GetAbbreviatedMonthName dt.Month
                | 'j' -> dt.DayOfYear.ToString("D3")
                | 'D' ->
                    let suffix =
                        match dt.Day with
                        | 1
                        | 21
                        | 31 -> "st"
                        | 2
                        | 22 -> "nd"
                        | 3
                        | 23 -> "rd"
                        | _ -> "th"

                    string dt.Day + suffix
                | 'w' -> string (int dt.DayOfWeek)
                | '%' -> "%"
                | other -> "%" + string other

            sb.Append(piece: string) |> ignore
            i <- i + 2
        else
            sb.Append fmt.[i] |> ignore
            i <- i + 1

    sb.ToString()

let private dateFormatFn: Scalar =
    function
    | [ d; f ] when not (anyNull [ d; f ]) ->
        match asDateTime d, toText f with
        | Some dt, Some fmt -> VString(formatDate dt fmt)
        | _ -> VNull
    | _ -> VNull

let private dateFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map VDate |> Option.defaultValue VNull
    | _ -> VNull

/// `TIMESTAMP(expr)` — coerces to a datetime the way MySQL does (a date
/// gains `00:00:00`); real MySQL also parses *two* string arguments as
/// `TIMESTAMP(expr1, expr2)` (= `expr1 + expr2` as a time), which nothing
/// here needs yet — add it if a migration calls that form.
let private timestampFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateTime v |> Option.map VDateTime |> Option.defaultValue VNull
    | _ -> VNull

/// No `TIME` case in `Value` (see the `VJson` comment on the same theme) —
/// ponytail: rendered as a plain `"HH:mm:ss"` string, add a `VTime` case if
/// a migration needs it to compare/sort as a real time value.
let private timeFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateTime v
        |> Option.map (fun dt -> VString(dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture)))
        |> Option.defaultValue VNull
    | _ -> VNull

let private datePartFn (f: DateTime -> int) : Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateTime v |> Option.map (f >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private dayNameFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateTime v |> Option.map (fun d -> VString(d.DayOfWeek.ToString())) |> Option.defaultValue VNull
    | _ -> VNull

let private monthNameFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateTime v
        |> Option.map (fun d -> VString(CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName d.Month))
        |> Option.defaultValue VNull
    | _ -> VNull

/// MySQL's 8 `WEEK(date, mode)` modes are 3 independent choices packed into
/// 3 bits: bit 0 picks Monday- (set) vs Sunday-first (unset) weeks; bit 1
/// picks a 1-53 range where the days before week 1 borrow the previous
/// year's last week (set) vs a 0-53 range where they're week 0 (unset);
/// bit 2 picks whether week 1 is the first week *containing* the year's
/// first Monday/Sunday (unset) or the ISO-style first week with 4+ days in
/// this year (set). MySQL's `week_mode()` XORs bit 2 when bit 0 is unset —
/// without that flip, Sunday-first and Monday-first modes wouldn't line up
/// with the docs' mode table (e.g. mode 0 uses the "contains" rule, mode 1
/// uses the "4+ days" rule, even though both have bit 2 unset in the raw
/// mode number).
let private weekModeBits (mode: int) : bool * bool * bool =
    let monday = mode &&& 1 <> 0
    let rangeFrom1 = mode &&& 2 <> 0
    let rawFirstWeekday = mode &&& 4 <> 0
    let firstWeekday = if monday then rawFirstWeekday else not rawFirstWeekday
    (monday, rangeFrom1, not firstWeekday)

/// The first day of week 1 of `year`, under the "contains the first
/// Mon/Sun" rule (`not rule4day`: the on-or-after occurrence of the week's
/// start weekday from Jan 1) or the ISO-style "4+ days in this year" rule
/// (`rule4day`: Jan 1's week counts as week 1 only if it has at least 4
/// days in `year`, i.e. its start weekday is within 3 days of Jan 1).
let private weekStart (year: int) (monday: bool) (rule4day: bool) : DateOnly =
    let jan1 = DateOnly(year, 1, 1)
    let raw = int jan1.DayOfWeek
    let wd = if monday then (raw + 6) % 7 else raw
    if rule4day then
        (if wd <= 3 then jan1.AddDays(-wd) else jan1.AddDays(7 - wd))
    else
        jan1.AddDays((7 - wd) % 7)

/// Days before `date`'s own-year week 1 belong either to week 0
/// (`not rangeFrom1`) or to the previous year's real last week
/// (`rangeFrom1`); a date on/after next year's week 1 start similarly
/// rolls forward to week 1 of next year, but only under `rangeFrom1` —
/// MySQL's 0-53 modes let a trailing week run all the way to 53 instead.
let private calcWeek (date: DateOnly) (monday: bool) (rule4day: bool) (rangeFrom1: bool) : int * int =
    let year = date.Year
    let w1 = weekStart year monday rule4day

    if date < w1 then
        if rangeFrom1 then
            let py = year - 1
            ((date.DayNumber - (weekStart py monday rule4day).DayNumber) / 7 + 1, py)
        else
            (0, year)
    else
        let weeknr = (date.DayNumber - w1.DayNumber) / 7 + 1

        if rangeFrom1 && date >= weekStart (year + 1) monday rule4day then
            (1, year + 1)
        else
            (weeknr, year)

let private weekOf (mode: int) (date: DateOnly) : int * int =
    let monday, rangeFrom1, rule4day = weekModeBits mode
    calcWeek date monday rule4day rangeFrom1

let private weekFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map (weekOf 0 >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | [ v; m ] when not (anyNull [ v; m ]) ->
        asDateOnly v |> Option.map (weekOf (int (toDouble m)) >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private weekdayFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateTime v |> Option.map (fun d -> VInt(int64 ((int d.DayOfWeek + 6) % 7))) |> Option.defaultValue VNull
    | _ -> VNull

let private weekOfYearFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map (weekOf 3 >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

/// `YEARWEEK` always uses the mode's day-of-week and week-1 rule but forces
/// the 1-53/rollover range (`rangeFrom1 = true`) regardless of the mode's
/// own range bit — a `(year, week)` pair has to be unambiguous, which a
/// week-0 or a trailing week-53-that's-really-next-year's-week-1 isn't.
let private yearWeekOf (mode: int) (date: DateOnly) : int =
    let monday, _, rule4day = weekModeBits mode
    let w, y = calcWeek date monday rule4day true
    y * 100 + w

let private yearWeekFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map (yearWeekOf 0 >> int64 >> VInt) |> Option.defaultValue VNull
    | [ v; m ] when not (anyNull [ v; m ]) ->
        asDateOnly v |> Option.map (yearWeekOf (int (toDouble m)) >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private curDateFn: Scalar = fun _ -> VDate(DateOnly.FromDateTime DateTime.Now)
let private curTimeFn: Scalar = fun _ -> VString(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture))

let private unixEpoch = DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Unspecified)

// The 0-arg form has to agree with `nowFn`/`curDateFn`/`curTimeFn`'s clock
// (`DateTime.Now`, local time) rather than UTC — this engine doesn't model
// timezones at all, so `UNIX_TIMESTAMP()` and `UNIX_TIMESTAMP(NOW())`
// (and `FROM_UNIXTIME(UNIX_TIMESTAMP())` vs. `NOW()`) would otherwise
// disagree by the host's UTC offset, and the disagreement would change
// with the host's timezone.
let private unixTimestampFn: Scalar =
    function
    | [] -> VInt(int64 (DateTime.Now - unixEpoch).TotalSeconds)
    | [ v ] when not (anyNull [ v ]) -> asDateTime v |> Option.map (fun dt -> VInt(int64 (dt - unixEpoch).TotalSeconds)) |> Option.defaultValue VNull
    | _ -> VNull

let private fromUnixSeconds (ts: Value) : DateTime option =
    let secs = toDouble ts
    if Double.IsNaN secs || abs secs > 3.2e11 then
        None // MySQL's FROM_UNIXTIME range tops out near year 3001; NULL past it
    else
        try Some(unixEpoch.AddSeconds secs) with :? ArgumentOutOfRangeException -> None

let private fromUnixTimeFn: Scalar =
    function
    | [ ts ] when not (anyNull [ ts ]) -> fromUnixSeconds ts |> Option.map VDateTime |> Option.defaultValue VNull
    | [ ts; f ] when not (anyNull [ ts; f ]) ->
        match toText f, fromUnixSeconds ts with
        | Some fmt, Some dt -> VString(formatDate dt fmt)
        | _ -> VNull
    | _ -> VNull

let private timestampDiffFn: Scalar =
    function
    | [ u; a; b ] when not (anyNull [ u; a; b ]) ->
        match toText u, asDateTime a, asDateTime b with
        | Some unit, Some da, Some db ->
            let span = db - da

            let result =
                match unit.ToUpperInvariant() with
                | "SECOND" -> span.TotalSeconds
                | "MINUTE" -> span.TotalMinutes
                | "HOUR" -> span.TotalHours
                | "DAY" -> span.TotalDays
                | "WEEK" -> span.TotalDays / 7.0
                // MONTH/YEAR's "whole units" rule (a day short of a full
                // month/year doesn't count) only makes sense computed on the
                // chronologically-ordered pair — computed directly on
                // `(da, db)` it always subtracted 1 regardless of which
                // side was earlier, overshooting by one whenever `db < da`
                // (a negative diff). Order the pair first, then reapply the
                // sign to the magnitude.
                | "MONTH" ->
                    let earlier, later, sign = if db < da then db, da, -1.0 else da, db, 1.0
                    sign * (float ((later.Year - earlier.Year) * 12 + later.Month - earlier.Month) - (if later.Day < earlier.Day then 1.0 else 0.0))
                | "QUARTER" ->
                    let earlier, later, sign = if db < da then db, da, -1.0 else da, db, 1.0
                    let months = float ((later.Year - earlier.Year) * 12 + later.Month - earlier.Month) - (if later.Day < earlier.Day then 1.0 else 0.0)
                    sign * Math.Truncate(months / 3.0)
                | "YEAR" ->
                    let earlier, later, sign = if db < da then db, da, -1.0 else da, db, 1.0
                    sign * (float (later.Year - earlier.Year) - (if (later.Month, later.Day) < (earlier.Month, earlier.Day) then 1.0 else 0.0))
                | _ -> span.TotalSeconds

            VInt(int64 (Math.Truncate result))
        | _ -> VNull
    | _ -> VNull

/// `EXTRACT(unit FROM expr)` — the unit rides in as argument one (see
/// `Parser.extractAtom`). A composite unit concatenates its components as
/// decimal digits, each lower one zero-padded to its own width (2, or 6 for
/// microseconds) and the highest one unpadded: oracle-pinned on
/// '2020-03-04 05:06:07.123456', `DAY_SECOND` is 4050607 and
/// `DAY_MICROSECOND` is 4050607123456. An unknown unit is NULL.
let private extractFn: Scalar =
    function
    | [ u; v ] when not (anyNull [ u; v ]) ->
        match toText u, asDateTime v with
        | Some unit, Some d ->
            // Components stitched together as `sum(part_i * 10^width_of_all_lower)`.
            let compose (parts: (int * int) list) =
                parts |> List.fold (fun acc (value, width) -> acc * pown 10L width + int64 value) 0L

            let micro = int ((d.Ticks % 10_000_000L) / 10L)

            match unit.ToUpperInvariant() with
            | "MICROSECOND" -> VInt(int64 micro)
            | "SECOND" -> VInt(int64 d.Second)
            | "MINUTE" -> VInt(int64 d.Minute)
            | "HOUR" -> VInt(int64 d.Hour)
            | "DAY" -> VInt(int64 d.Day)
            | "WEEK" -> weekFn [ v ]
            | "MONTH" -> VInt(int64 d.Month)
            | "QUARTER" -> VInt(int64 ((d.Month - 1) / 3 + 1))
            | "YEAR" -> VInt(int64 d.Year)
            | "SECOND_MICROSECOND" -> VInt(compose [ d.Second, 0; micro, 6 ])
            | "MINUTE_MICROSECOND" -> VInt(compose [ d.Minute, 0; d.Second, 2; micro, 6 ])
            | "MINUTE_SECOND" -> VInt(compose [ d.Minute, 0; d.Second, 2 ])
            | "HOUR_MICROSECOND" -> VInt(compose [ d.Hour, 0; d.Minute, 2; d.Second, 2; micro, 6 ])
            | "HOUR_SECOND" -> VInt(compose [ d.Hour, 0; d.Minute, 2; d.Second, 2 ])
            | "HOUR_MINUTE" -> VInt(compose [ d.Hour, 0; d.Minute, 2 ])
            | "DAY_MICROSECOND" -> VInt(compose [ d.Day, 0; d.Hour, 2; d.Minute, 2; d.Second, 2; micro, 6 ])
            | "DAY_SECOND" -> VInt(compose [ d.Day, 0; d.Hour, 2; d.Minute, 2; d.Second, 2 ])
            | "DAY_MINUTE" -> VInt(compose [ d.Day, 0; d.Hour, 2; d.Minute, 2 ])
            | "DAY_HOUR" -> VInt(compose [ d.Day, 0; d.Hour, 2 ])
            | "YEAR_MONTH" -> VInt(compose [ d.Year, 0; d.Month, 2 ])
            | _ -> VNull
        | _ -> VNull
    | _ -> VNull

let private lastDayFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateTime v
        |> Option.map (fun dt -> VDate(DateOnly(dt.Year, dt.Month, DateTime.DaysInMonth(dt.Year, dt.Month))))
        |> Option.defaultValue VNull
    | _ -> VNull

/// `MAKEDATE(year, dayofyear)`: January 1st of `year` plus `dayofyear - 1`
/// days, so day 366 of a non-leap year rolls into the next year
/// (MySQL-verified: `MAKEDATE(2024, 367)` = 2025-01-01). `dayofyear < 1`
/// and a year outside 0..9999 are NULL, and a two-digit year follows
/// MySQL's usual pivot (0..69 → 2000s, 70..99 → 1900s). Both arguments
/// round rather than truncate, which decides which side of that pivot a
/// fractional year lands on (`MAKEDATE(69.6, 1)` is 1970-01-01).
let private makeDateFn: Scalar =
    function
    | [ y; d ] when not (anyNull [ y; d ]) ->
        let year = int (toDouble (roundNumeric y))
        let day = int (toDouble (roundNumeric d))

        let year =
            if year >= 0 && year <= 69 then year + 2000
            elif year >= 70 && year <= 99 then year + 1900
            else year

        if day < 1 || year < 1 || year > 9999 then
            VNull
        else
            try
                VDate(DateOnly(year, 1, 1).AddDays(day - 1))
            with _ ->
                VNull
    | _ -> VNull

/// A `CONVERT_TZ` zone argument, as minutes east of UTC. Only MySQL's
/// numeric `[+-]HH:MM` form is understood, over MySQL's asymmetric
/// documented range '-13:59'..'+14:00' (anything outside is NULL).
/// ponytail: named zones (`UTC`, `America/New_York`) and `SYSTEM` return
/// NULL, which is exactly what a MySQL server with no `mysql.time_zone*`
/// rows loaded answers — the oracle this is pinned against. Route them
/// through `TimeZoneInfo` only alongside a real, loadable zone table, since
/// a half-loaded one would answer some zones and NULL others.
let private tzOffsetMinutes (spec: string) : int option =
    let s = spec.Trim()

    let sign, body =
        if s.StartsWith "+" then 1, s.Substring 1
        elif s.StartsWith "-" then -1, s.Substring 1
        else 0, s

    if sign = 0 then
        None
    else
        match body.Split ':' with
        | [| h; m |] ->
            match Int32.TryParse(h, NumberStyles.None, CultureInfo.InvariantCulture), Int32.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture) with
            | (true, hours), (true, minutes) when minutes < 60 ->
                let total = sign * (hours * 60 + minutes)
                if total >= -839 && total <= 840 then Some total else None
            | _ -> None
        | _ -> None

/// MySQL converts only when the argument, read in `from_tz`, lands inside
/// the TIMESTAMP window — 1970-01-01 00:00:01 UTC through 3001-01-18
/// 23:59:59 UTC (8.0.28 widened the old 2038 ceiling). Outside it the input
/// comes back *unchanged*, not NULL and not shifted, so 1970 and 9999
/// datetimes are fixed points. Only the source instant is tested; the
/// result may fall outside the window (`CONVERT_TZ('1970-01-01
/// 00:00:01','+00:00','-05:00')` = '1969-12-31 19:00:01').
let private convertTzFn: Scalar =
    let windowStart = DateTime(1970, 1, 1, 0, 0, 1)
    let windowEnd = DateTime(3001, 1, 18, 23, 59, 59)

    function
    | [ dt; f; t ] when not (anyNull [ dt; f; t ]) ->
        match asDateTime dt, tzOffsetMinutes (req f), tzOffsetMinutes (req t) with
        | Some d, Some fromMinutes, Some toMinutes ->
            let utc =
                try
                    Some(d.AddMinutes(float -fromMinutes))
                with _ ->
                    None

            match utc with
            | Some u when u >= windowStart && u <= windowEnd ->
                try
                    VDateTime(d.AddMinutes(float (toMinutes - fromMinutes)))
                with _ ->
                    VDateTime d
            | _ -> VDateTime d
        | _ -> VNull
    | _ -> VNull

/// The `STR_TO_DATE` mirror of `formatDate`'s specifier table, translated
/// to a .NET custom format string for `DateTime.TryParseExact`.
let private mysqlToNetFormat (fmt: string) : string =
    let sb = StringBuilder()
    let mutable i = 0

    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            let piece =
                match fmt.[i + 1] with
                | 'Y' -> "yyyy"
                | 'y' -> "yy"
                | 'm' -> "MM"
                | 'c' -> "M"
                | 'd' -> "dd"
                | 'e' -> "d"
                | 'H' -> "HH"
                | 'h' -> "hh"
                | 'i' -> "mm"
                | 's' -> "ss"
                | 'p' -> "tt"
                | 'M' -> "MMMM"
                | 'b' -> "MMM"
                | '%' -> "%"
                | other -> string other

            sb.Append(piece: string) |> ignore
            i <- i + 2
        else
            sb.Append fmt.[i] |> ignore
            i <- i + 1

    sb.ToString()

let private strToDateFn: Scalar =
    function
    | [ s; f ] when not (anyNull [ s; f ]) ->
        match toText s, toText f with
        | Some str, Some fmt ->
            let netFmt = mysqlToNetFormat fmt

            match DateTime.TryParseExact(str.Trim(), netFmt, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, dt when netFmt.Contains "H" || netFmt.Contains "h" -> VDateTime dt
            | true, dt -> VDate(DateOnly.FromDateTime dt)
            | false, _ -> VNull
        | _ -> VNull
    | _ -> VNull

// ---------------------------------------------------------------------------
// Strings.
// ---------------------------------------------------------------------------

/// Shared by `SUBSTRING`/`SUBSTRING_INDEX`-adjacent helpers: MySQL's 1-based,
/// negative-counts-from-the-end position rule, clamped into a valid 0-based
/// start offset.
let private resolveStart (len: int) (pos: int) : int option =
    if pos > 0 then Some(min (pos - 1) len)
    elif pos < 0 then
        // A negative position further back than the string is long has no
        // valid start (MySQL: SUBSTRING('hello', -10) = '').
        if len + pos < 0 then None else Some(len + pos)
    else None

let private substringFn: Scalar =
    function
    | [ s; posV ] when not (anyNull [ s; posV ]) ->
        let str = req s

        match resolveStart str.Length (int (toDouble posV)) with
        | None -> VString ""
        | Some start -> VString(str.Substring start)
    | [ s; posV; lenV ] when not (anyNull [ s; posV; lenV ]) ->
        let str = req s
        let takeLen = int (toDouble lenV)

        match resolveStart str.Length (int (toDouble posV)) with
        | None -> VString ""
        | Some start when takeLen <= 0 -> VString ""
        | Some start -> VString(str.Substring(start, min takeLen (str.Length - start)))
    | _ -> VNull

/// Character-by-character substring search using the engine's default
/// collation's `CharEquals`, so accent/case sensitivity follows the
/// collation (e.g. `_bin`/`_cs` don't fold, `_ai_ci` also ignores accents).
/// Not per-column collation-aware (that needs the column's collation
/// threaded through `Scalar`'s signature, which this engine doesn't do
/// yet).
let private collationIndexOf (str: string) (sub: string) (startIdx: int) : int =
    if sub = "" then
        startIdx
    else
        let charEquals = Collation.defaultCollation.CharEquals
        let maxStart = str.Length - sub.Length
        let mutable result = -1
        let mutable i = startIdx

        while result < 0 && i <= maxStart do
            let mutable matched = true
            let mutable j = 0

            while matched && j < sub.Length do
                if not (charEquals str.[i + j] sub.[j]) then matched <- false
                j <- j + 1

            if matched then result <- i
            i <- i + 1

        result

let private locateAt (str: string) (sub: string) (startIdx: int) : Value =
    if startIdx > str.Length || startIdx < 0 then
        VInt 0L
    else
        VInt(int64 (collationIndexOf str sub startIdx + 1))

let private locateFn: Scalar =
    function
    | [ sub; str ] when not (anyNull [ sub; str ]) -> locateAt (req str) (req sub) 0
    // A start position below 1 is invalid and yields 0, not a search from
    // the beginning: LOCATE('l', 'Hello', 0) = 0 in MySQL.
    | [ sub; str; posV ] when not (anyNull [ sub; str; posV ]) ->
        let pos = int (toDouble posV)
        if pos < 1 then VInt 0L else locateAt (req str) (req sub) (pos - 1)
    | _ -> VNull

let private instrFn: Scalar =
    function
    | [ str; sub ] when not (anyNull [ str; sub ]) -> locateAt (req str) (req sub) 0
    | _ -> VNull

let private replaceFn: Scalar =
    function
    | [ s; f; t ] when not (anyNull [ s; f; t ]) ->
        let str, frm = req s, req f
        if frm = "" then VString str else VString(str.Replace(frm, req t))
    | _ -> VNull

let private padFn (left: bool) : Scalar =
    function
    | [ s; lenV; p ] when not (anyNull [ s; lenV; p ]) ->
        let str, pad = req s, req p
        let targetLen = int (toDouble lenV)

        if targetLen < 0 then
            VNull
        elif targetLen = 0 then
            VString ""
        elif targetLen <= str.Length then
            VString(str.Substring(0, targetLen))
        elif pad = "" then
            VNull
        else
            let needed = targetLen - str.Length
            let padding = (String.replicate (needed / pad.Length + 1) pad).Substring(0, needed)
            VString(if left then padding + str else str + padding)
    | _ -> VNull

/// `LEFT`/`RIGHT` count *bytes* on a binary operand and characters on a
/// text one, same as MySQL. Without the `VBytes` case the bytes would go
/// through `req`'s text view first, and any non-ASCII byte comes back out
/// as its multi-byte UTF-8 encoding — `LEFT(x, 4)` over binary data then
/// returns six bytes of mangled prefix.
let private leftFn: Scalar =
    function
    | [ VBytes b; n ] when not (anyNull [ n ]) -> VBytes(Array.truncate (max 0 (int (toDouble n))) b)
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let str = req s
        VString(str.Substring(0, max 0 (min str.Length (int (toDouble n)))))
    | _ -> VNull

let private rightFn: Scalar =
    function
    | [ VBytes b; n ] when not (anyNull [ n ]) ->
        let k = max 0 (min b.Length (int (toDouble n)))
        VBytes(b.[b.Length - k ..])
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let str = req s
        let k = max 0 (min str.Length (int (toDouble n)))
        VString(str.Substring(str.Length - k))
    | _ -> VNull

/// The server's advertised `max_allowed_packet` — `REPEAT`/`SPACE` return
/// NULL rather than allocate past it for a runaway count, and MySQL NULLs
/// them only past the *actual* max_allowed_packet, so this must match what
/// `Session.defaultVariables` reports. That is
/// `Packet.maxAccumulatedPacketSize` (64 MiB), but Functions.fs compiles
/// before Packet.fs, so the value is duplicated here; keep the two in sync.
let private maxAllowedPacket = 64 * 1024 * 1024

let private repeatFn: Scalar =
    function
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let k = int (toDouble n)
        let str = req s
        if k <= 0 then VString ""
        elif int64 k * int64 str.Length > int64 maxAllowedPacket then VNull
        else VString(String.replicate k str)
    | _ -> VNull

let private spaceFn: Scalar =
    function
    | [ n ] when not (anyNull [ n ]) ->
        let k = max 0 (int (toDouble n))
        if k > maxAllowedPacket then VNull else VString(String(' ', k))
    | _ -> VNull

/// The first byte of the string's UTF-8 encoding, not the first UTF-16 code
/// unit — `ASCII('é')` is `195` (0xC3, the lead byte of é's 2-byte UTF-8
/// encoding) in MySQL, not é's UTF-16 value 233.
let private asciiFn: Scalar =
    function
    | [ VBytes b ] -> VInt(if b.Length = 0 then 0L else int64 b.[0])
    | [ s ] when not (anyNull [ s ]) ->
        let str = req s
        VInt(if str = "" then 0L else int64 (Text.Encoding.UTF8.GetBytes(str).[0]))
    | _ -> VNull

/// Minimal `CHAR(n1, n2, ...)`: builds a string from Unicode code points
/// rather than MySQL's charset-aware byte assembly. Per MySQL, NULL
/// arguments are skipped rather than nulling the whole result.
let private charFn: Scalar =
    fun args ->
        args
        |> List.choose (function
            | VNull -> None
            | v -> Some(char (int (toDouble v))))
        |> Array.ofList
        |> String
        |> VString

let private hexFn: Scalar =
    function
    | [ VInt i ] -> VString(i.ToString "X")
    | [ VUInt u ] -> VString(u.ToString "X")
    | [ VString s ] -> VString(Text.Encoding.UTF8.GetBytes s |> Array.map (fun b -> b.ToString "X2") |> String.concat "")
    | [ VBytes b ] -> VString(b |> Array.map (fun x -> x.ToString "X2") |> String.concat "")
    | [ v ] when not (anyNull [ v ]) -> VString((int64 (toDouble v)).ToString "X")
    | _ -> VNull

let private unhexFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        let s = req v

        if s.Length % 2 <> 0 || not (s |> Seq.forall Uri.IsHexDigit) then
            VNull
        else
            [| for i in 0 .. 2 .. s.Length - 1 -> Convert.ToByte(s.Substring(i, 2), 16) |]
            |> VBytes
    | _ -> VNull

let private md5Fn: Scalar = textMap (fun s -> Convert.ToHexString(MD5.HashData(Text.Encoding.UTF8.GetBytes s)).ToLowerInvariant())
let private sha1Fn: Scalar = textMap (fun s -> Convert.ToHexString(SHA1.HashData(Text.Encoding.UTF8.GetBytes s)).ToLowerInvariant())

/// SHA-224 (FIPS 180-4): SHA-256's compression function with different
/// initial hash values, output truncated to 224 bits. The BCL has no
/// `SHA224` type, so the compression loop lives here.
let private sha224 (data: byte[]) : byte[] =
    let k =
        [| 0x428a2f98u; 0x71374491u; 0xb5c0fbcfu; 0xe9b5dba5u; 0x3956c25bu; 0x59f111f1u; 0x923f82a4u; 0xab1c5ed5u
           0xd807aa98u; 0x12835b01u; 0x243185beu; 0x550c7dc3u; 0x72be5d74u; 0x80deb1feu; 0x9bdc06a7u; 0xc19bf174u
           0xe49b69c1u; 0xefbe4786u; 0x0fc19dc6u; 0x240ca1ccu; 0x2de92c6fu; 0x4a7484aau; 0x5cb0a9dcu; 0x76f988dau
           0x983e5152u; 0xa831c66du; 0xb00327c8u; 0xbf597fc7u; 0xc6e00bf3u; 0xd5a79147u; 0x06ca6351u; 0x14292967u
           0x27b70a85u; 0x2e1b2138u; 0x4d2c6dfcu; 0x53380d13u; 0x650a7354u; 0x766a0abbu; 0x81c2c92eu; 0x92722c85u
           0xa2bfe8a1u; 0xa81a664bu; 0xc24b8b70u; 0xc76c51a3u; 0xd192e819u; 0xd6990624u; 0xf40e3585u; 0x106aa070u
           0x19a4c116u; 0x1e376c08u; 0x2748774cu; 0x34b0bcb5u; 0x391c0cb3u; 0x4ed8aa4au; 0x5b9cca4fu; 0x682e6ff3u
           0x748f82eeu; 0x78a5636fu; 0x84c87814u; 0x8cc70208u; 0x90befffau; 0xa4506cebu; 0xbef9a3f7u; 0xc67178f2u |]

    let h = [| 0xc1059ed8u; 0x367cd507u; 0x3070dd17u; 0xf70e5939u; 0xffc00b31u; 0x68581511u; 0x64f98fa7u; 0xbefa4fa4u |]

    // Pad to a 64-byte multiple: 0x80, zeros, then the bit length big-endian.
    let padded = Array.zeroCreate ((data.Length + 8) / 64 * 64 + 64)
    Array.blit data 0 padded 0 data.Length
    padded.[data.Length] <- 0x80uy
    let bitLen = uint64 data.Length * 8UL

    for i in 0..7 do
        padded.[padded.Length - 1 - i] <- byte (bitLen >>> (8 * i))

    let rotr (x: uint32) n = (x >>> n) ||| (x <<< (32 - n))
    let w = Array.zeroCreate<uint32> 64

    for block in 0 .. padded.Length / 64 - 1 do
        for t in 0..15 do
            let o = block * 64 + t * 4
            w.[t] <- (uint32 padded.[o] <<< 24) ||| (uint32 padded.[o + 1] <<< 16) ||| (uint32 padded.[o + 2] <<< 8) ||| uint32 padded.[o + 3]

        for t in 16..63 do
            let s0 = rotr w.[t - 15] 7 ^^^ rotr w.[t - 15] 18 ^^^ (w.[t - 15] >>> 3)
            let s1 = rotr w.[t - 2] 17 ^^^ rotr w.[t - 2] 19 ^^^ (w.[t - 2] >>> 10)
            w.[t] <- w.[t - 16] + s0 + w.[t - 7] + s1

        let mutable a, b, c, d = h.[0], h.[1], h.[2], h.[3]
        let mutable e, f, g, hh = h.[4], h.[5], h.[6], h.[7]

        for t in 0..63 do
            let t1 = hh + (rotr e 6 ^^^ rotr e 11 ^^^ rotr e 25) + ((e &&& f) ^^^ (~~~e &&& g)) + k.[t] + w.[t]
            let t2 = (rotr a 2 ^^^ rotr a 13 ^^^ rotr a 22) + ((a &&& b) ^^^ (a &&& c) ^^^ (b &&& c))
            hh <- g
            g <- f
            f <- e
            e <- d + t1
            d <- c
            c <- b
            b <- a
            a <- t1 + t2

        h.[0] <- h.[0] + a
        h.[1] <- h.[1] + b
        h.[2] <- h.[2] + c
        h.[3] <- h.[3] + d
        h.[4] <- h.[4] + e
        h.[5] <- h.[5] + f
        h.[6] <- h.[6] + g
        h.[7] <- h.[7] + hh

    // 224 bits = the first 7 of the 8 state words.
    [| for i in 0..6 do
           for s in [ 24; 16; 8; 0 ] -> byte (h.[i] >>> s) |]

let private sha2Fn: Scalar =
    function
    | [ s; lenV ] when not (anyNull [ s; lenV ]) ->
        let bytes = Text.Encoding.UTF8.GetBytes(req s)

        let hash =
            match int (toDouble lenV) with
            | 0
            | 256 -> Some(SHA256.HashData bytes)
            | 224 -> Some(sha224 bytes)
            | 384 -> Some(SHA384.HashData bytes)
            | 512 -> Some(SHA512.HashData bytes)
            | _ -> None

        hash |> Option.map (fun h -> VString(Convert.ToHexString(h).ToLowerInvariant())) |> Option.defaultValue VNull
    | _ -> VNull

let private formatFn: Scalar =
    function
    | [ n; d ] when not (anyNull [ n; d ]) -> VString((toDouble n).ToString("N" + string (max 0 (int (toDouble d))), CultureInfo.InvariantCulture))
    | _ -> VNull

let private substringIndexFn: Scalar =
    function
    | [ s; d; c ] when not (anyNull [ s; d; c ]) ->
        let str, delim = req s, req d
        let count = int (toDouble c)

        if delim = "" || count = 0 then
            VString ""
        else
            let parts = str.Split([| delim |], StringSplitOptions.None)

            if count > 0 then
                VString(String.Join(delim, parts |> Array.truncate count))
            else
                let take = min (-count) parts.Length
                VString(String.Join(delim, parts |> Array.skip (parts.Length - take)))
    | _ -> VNull

let private concatWsFn: Scalar =
    function
    | sep :: rest when not (anyNull [ sep ]) ->
        rest |> List.choose (function VNull -> None | v -> toText v) |> String.concat (req sep) |> VString
    | _ -> VNull

let private eltFn: Scalar =
    function
    | n :: rest when not (anyNull [ n ]) ->
        let idx = int (toDouble n)
        if idx >= 1 && idx <= rest.Length then rest.[idx - 1] else VNull
    | _ -> VNull

/// `FIELD(NULL, ...)` is one of MySQL's documented NULL exceptions: it
/// returns `0`, not `NULL`, since NULL never equals anything (including
/// another NULL) so it simply never matches.
let private fieldFn: Scalar =
    function
    | target :: rest ->
        match rest |> List.tryFindIndex (fun v -> Value.equals target v = Some true) with
        | Some i -> VInt(int64 (i + 1))
        | None -> VInt 0L
    | _ -> VNull

/// `EXPORT_SET(bits, on, off [, separator [, number_of_bits]])`: one token
/// per bit of `bits`, **low bit first**. `number_of_bits` defaults to 64 and
/// is read as unsigned then capped at 64, so a negative count means 64 too
/// (MySQL-verified: `EXPORT_SET(5,'Y','n',',',-1)` yields all 64 tokens).
/// Both numeric arguments round rather than truncate
/// (`EXPORT_SET(5.7,'Y','N',',',4)` exports 6's bits).
let private exportSetFn: Scalar =
    fun args ->
        match args with
        | bits :: on :: off :: rest when not (anyNull (bits :: on :: off :: rest)) && rest.Length <= 2 ->
            let separator = rest |> List.tryItem 0 |> Option.map req |> Option.defaultValue ","

            let count =
                match rest |> List.tryItem 1 with
                | Some n -> toUInt64 (roundNumeric n) |> min 64UL |> int
                | None -> 64

            let value = toUInt64 (roundNumeric bits)

            [ 0 .. count - 1 ]
            |> List.map (fun i -> if (value >>> i) &&& 1UL = 1UL then req on else req off)
            |> String.concat separator
            |> VString
        | _ -> VNull

/// `BIT_COUNT`: set bits in the argument's `BIGINT UNSIGNED` pattern, so
/// `BIT_COUNT(-1)` is 64. A fractional argument rounds before the count
/// (`BIT_COUNT(3.5)` counts 4's bits, giving 1).
let private bitCountFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (Numerics.BitOperations.PopCount(toUInt64 (roundNumeric v))))
    | _ -> VNull

let private findInSetFn: Scalar =
    function
    | [ s; list ] when not (anyNull [ s; list ]) ->
        let target = req s

        match (req list).Split(',') |> Array.tryFindIndex (fun x -> Collation.defaultCollation.Equals x target) with
        | Some i -> VInt(int64 (i + 1))
        | None -> VInt 0L
    | _ -> VNull

let private quoteFn: Scalar =
    function
    | [ VNull ] -> VString "NULL"
    | [ v ] ->
        let sb = StringBuilder()
        sb.Append '\'' |> ignore

        for c in req v do
            match c with
            | '\'' -> sb.Append "\\'" |> ignore
            | '\\' -> sb.Append "\\\\" |> ignore
            | '\000' -> sb.Append "\\0" |> ignore
            | '\n' -> sb.Append "\\n" |> ignore
            | '\r' -> sb.Append "\\r" |> ignore
            | '\026' -> sb.Append "\\Z" |> ignore
            | c -> sb.Append c |> ignore

        sb.Append '\'' |> ignore
        VString(sb.ToString())
    | _ -> VNull

let private strcmpFn: Scalar =
    function
    | [ a; b ] when not (anyNull [ a; b ]) -> VInt(int64 (sign (Value.compare a b)))
    | _ -> VNull

// ---------------------------------------------------------------------------
// REGEXP_LIKE/REPLACE/SUBSTR/INSTR. Same timeout guard as `Executor.regexpOp`
// (the `REGEXP`/`RLIKE` operator) against catastrophic backtracking, but
// unlike that operator these can't thread a real MySQL error back to the
// client — `Scalar` is a total `Value list -> Value`, no error channel —
// so a malformed pattern degrades to NULL, the same tolerate-and-return-NULL
// treatment this file already gives malformed JSON input (see
// `jsonExtractFn`'s neighbors) rather than the operator's 1139.
// ---------------------------------------------------------------------------

let private regexpTimeout = TimeSpan.FromSeconds 5.0

/// MySQL's default is case-insensitive matching; `match_type` characters
/// flip options on top of that default, applied left to right so the last
/// conflicting flag (e.g. "ci") wins rather than erroring on conflicts.
/// `u` (Unix-only line endings) has no .NET equivalent and is ignored.
let private regexOptsOfMatchType (matchType: string option) : RegexOptions =
    let mutable opts = RegexOptions.IgnoreCase

    for c in matchType |> Option.defaultValue "" do
        match c with
        | 'c' -> opts <- opts &&& ~~~RegexOptions.IgnoreCase
        | 'i' -> opts <- opts ||| RegexOptions.IgnoreCase
        | 'm' -> opts <- opts ||| RegexOptions.Multiline
        | 'n' -> opts <- opts ||| RegexOptions.Singleline
        | _ -> ()

    opts

let private tryRegex (pattern: string) (opts: RegexOptions) : Regex option =
    try
        Some(Regex(pattern, opts, regexpTimeout))
    with :? ArgumentException ->
        None

/// The `occurrence`-th match (1-based) at or after 1-based `pos` — MySQL's
/// pos/occurrence pair, common to all four REGEXP_* functions. `pos`
/// outside `[1, text.Length + 1]` or a non-positive `occurrence` has no
/// match, same as an occurrence beyond how many matches actually exist.
let private nthMatch (rx: Regex) (text: string) (pos: int) (occurrence: int) : Match option =
    if pos < 1 || pos > text.Length + 1 || occurrence < 1 then
        None
    else
        // Walk matches lazily via `NextMatch` and stop at the requested
        // occurrence — materializing the whole MatchCollection would let a
        // `.`-style pattern over a large string build millions of Match
        // objects for what is usually occurrence 1.
        let mutable m = rx.Match(text, pos - 1)
        let mutable remaining = occurrence - 1

        while m.Success && remaining > 0 do
            m <- m.NextMatch()
            remaining <- remaining - 1

        if m.Success then Some m else None

let private matchTypeArg (args: Value list) (idx: int) : string option =
    args |> List.tryItem idx |> Option.filter (fun v -> v <> VNull) |> Option.map req

let private intArgOr (dflt: int) (args: Value list) (idx: int) : int =
    args |> List.tryItem idx |> Option.filter (fun v -> v <> VNull) |> Option.map (toDouble >> int) |> Option.defaultValue dflt

let private regexpLikeFn: Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        match tryRegex (req p) (regexOptsOfMatchType (matchTypeArg rest 0)) with
        | Some rx -> if rx.IsMatch(req e) then VInt 1L else VInt 0L
        | None -> VNull
    | _ -> VNull

let private regexpInstrFn: Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        let text = req e
        let pos = intArgOr 1 rest 0
        let occurrence = intArgOr 1 rest 1
        let returnEnd = intArgOr 0 rest 2 <> 0

        match tryRegex (req p) (regexOptsOfMatchType (matchTypeArg rest 3)) with
        | Some rx ->
            match nthMatch rx text pos occurrence with
            | Some m -> VInt(int64 ((if returnEnd then m.Index + m.Length else m.Index) + 1))
            | None -> VInt 0L
        | None -> VNull
    | _ -> VNull

let private regexpSubstrFn: Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        let text = req e
        let pos = intArgOr 1 rest 0
        let occurrence = intArgOr 1 rest 1

        match tryRegex (req p) (regexOptsOfMatchType (matchTypeArg rest 2)) with
        | Some rx ->
            match nthMatch rx text pos occurrence with
            | Some m -> VString m.Value
            | None -> VNull
        | None -> VNull
    | _ -> VNull

/// MySQL (ICU) replacement text uses `$N` for backreferences, the same
/// syntax .NET's `Regex.Replace` understands, so `$N` passes through
/// untouched. `\N` is a literal digit and `\\` a literal backslash in MySQL,
/// not a backreference escape, so both are translated away before .NET sees
/// them (oracle-verified: MySQL 8.4). A `$` NOT followed by a digit is
/// rejected with MySQL's 3887 — leaving it for .NET would enable its
/// non-MySQL `$``/`$'`/`$&`/`$_` substitution tokens (and `$`` amplifies
/// output to O(n²)); MySQL itself errors on all of them.
let private toDotNetReplacement (repl: string) : string =
    if Regex.IsMatch(repl, @"\$(?!\d)") then
        raise (SqlError(3887, "A capture group has an invalid name."))

    Regex.Replace(repl, @"\\\\|\\(\d)", fun m -> if m.Value = "\\\\" then "\\" else m.Groups.[1].Value)

/// `occurrence = 0` (the default) replaces every match; a positive
/// `occurrence` replaces only that one match, leaving the rest of the
/// string untouched either way.
let private regexpReplaceFn: Scalar =
    function
    | e :: p :: r :: rest when not (anyNull [ e; p; r ]) ->
        let text = req e
        let repl = toDotNetReplacement (req r)
        let pos = intArgOr 1 rest 0
        let occurrence = intArgOr 0 rest 1

        match tryRegex (req p) (regexOptsOfMatchType (matchTypeArg rest 2)) with
        | Some rx ->
            if pos < 1 || pos > text.Length + 1 then
                VNull
            else
                let head = text.Substring(0, pos - 1)
                let tail = text.Substring(pos - 1)

                let replaced =
                    if occurrence <= 0 then
                        rx.Replace(tail, repl)
                    else
                        let mutable n = 0

                        rx.Replace(
                            tail,
                            (fun m ->
                                n <- n + 1
                                if n = occurrence then m.Result repl else m.Value)
                        )

                VString(head + replaced)
        | None -> VNull
    | _ -> VNull

// ---------------------------------------------------------------------------
// Math/misc.
// ---------------------------------------------------------------------------

/// FLOOR/CEILING keep the argument's *type family*, they don't collapse it:
/// MySQL answers a DECIMAL argument with a scale-0 DECIMAL, not a BIGINT, so
/// `FLOOR(exact_value)` comes back on the wire as NEWDECIMAL. Going through
/// `toDouble` for a decimal would also lose digits past 2^53.
let private ceilFn: Scalar =
    function
    | [ VInt i ] -> VInt i
    | [ VUInt u ] -> VUInt u
    | [ VDecimal d ] -> VDecimal(Math.Ceiling d)
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (Math.Ceiling(toDouble v)))
    | _ -> VNull

let private floorFn: Scalar =
    function
    | [ VInt i ] -> VInt i
    | [ VUInt u ] -> VUInt u
    | [ VDecimal d ] -> VDecimal(Math.Floor d)
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (Math.Floor(toDouble v)))
    | _ -> VNull

let private powFn: Scalar =
    function
    | [ a; b ] when not (anyNull [ a; b ]) -> VDouble(Math.Pow(toDouble a, toDouble b))
    | _ -> VNull

let private sqrtFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        let d = toDouble v
        if d < 0.0 then VNull else VDouble(Math.Sqrt d)
    | _ -> VNull

let private signFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (sign (toDouble v)))
    | _ -> VNull

/// `TRUNCATE(d, n)` on an exact value keeps scale `n` even when the kept
/// digits are zeros — MySQL answers `68632858.00`, and .NET's `decimal`
/// arithmetic alone would hand back a scale-0 `68632858`. Re-parsing a
/// fixed-point rendering pads the scale back on.
let private truncateFn: Scalar =
    function
    // Exact and integral: only a negative digit count changes anything, and
    // it can only shrink the value, so it stays in the unsigned domain.
    | [ VUInt u; d ] when not (anyNull [ d ]) ->
        let digits = int (toDouble d)

        if digits >= 0 then VUInt u
        // 10^20 already exceeds the whole domain, so anything beyond that
        // truncates to 0 without asking `decimal` for an overflowing factor.
        elif digits <= -20 then VUInt 0UL
        else
            let factor = pown 10M -digits
            Value.narrowUnsigned (Math.Truncate(decimal u / factor) * factor)
    | [ VDecimal dec; d ] when not (anyNull [ d ]) ->
        let digits = int (toDouble d)
        let factor = decimal (Math.Pow(10.0, float digits))
        let truncated = Math.Truncate(dec * factor) / factor

        if digits <= 0 then
            VDecimal truncated
        else
            let scale = min digits 28
            VDecimal(Decimal.Parse(truncated.ToString("F" + string scale, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture))
    | [ v; d ] when not (anyNull [ v; d ]) ->
        let factor = Math.Pow(10.0, float (int (toDouble d)))
        VDouble(Math.Truncate(toDouble v * factor) / factor)
    | _ -> VNull

let private random = Random()
let private randFn: Scalar = fun _ -> VDouble(random.NextDouble())

let private greatestFn: Scalar =
    function
    | args when anyNull args || List.isEmpty args -> VNull
    | args -> args |> List.reduce (fun a b -> if Value.compare a b >= 0 then a else b)

let private leastFn: Scalar =
    function
    | args when anyNull args || List.isEmpty args -> VNull
    | args -> args |> List.reduce (fun a b -> if Value.compare a b <= 0 then a else b)

/// `NULLIF(a, b)`: NULL when `a = b`; otherwise `a` — including when the
/// comparison itself is unknown (either side NULL), since MySQL's `<>`
/// there is unknown, not true.
let private nullIfFn: Scalar =
    function
    | [ a; b ] -> if Value.equals a b = Some true then VNull else a
    | _ -> VNull

let private isNullFn: Scalar =
    function
    | [ VNull ] -> VInt 1L
    | [ _ ] -> VInt 0L
    | _ -> VNull

let private baseDigits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"

/// CONV/BIN/OCT work in MySQL's 64-bit *unsigned* domain: a negative input
/// wraps to its two's-complement pattern (`CONV('-1', 10, 16)` is
/// FFFFFFFFFFFFFFFF, `BIN(-1)` is 64 ones), and 18446744073709551615 has to
/// survive rather than saturate `int64`.
///
///
/// Parsing stops at the first character that isn't a digit in `b` and keeps
/// what it already read, rather than rejecting the whole string:
/// MySQL-verified, `CONV('12abc', 10, 10)` is `12` and `CONV('xyz', 16, 10)`
/// is `0`, not NULL.
///
/// A magnitude past 2^64-1 saturates rather than wrapping, and MySQL's two
/// string-to-integer parsers disagree about the sign of the saturated
/// value: positive always saturates to 2^64-1, negative to 0 — but only
/// when the number's most significant digit is a decimal one. A letter
/// there saturates to 2^64-1 as well (MySQL-verified:
/// `CONV('-1FFFFFFFFFFFFFFFF',16,10)` is 0 where
/// `CONV('-FFFFFFFFFFFFFFFF0',16,10)` is 18446744073709551615).
let private parseInBase (s: string) (b: int) : uint64 option =
    let s = s.Trim().ToUpperInvariant()
    let neg, s = if s.StartsWith "-" then true, s.Substring 1 else false, s

    if b < 2 || b > 36 then
        None
    else
        let mutable acc = 0UL
        let mutable stop = false
        let mutable overflow = false
        let mutable leadDigit = -1

        for c in s do
            let d = baseDigits.IndexOf c

            if stop || d < 0 || d >= b then
                stop <- true
            elif acc > (UInt64.MaxValue - uint64 d) / uint64 b then
                overflow <- true
            else
                if leadDigit < 0 && d > 0 then
                    leadDigit <- d

                acc <- acc * uint64 b + uint64 d

        Some(
            if overflow then
                if neg && leadDigit >= 0 && leadDigit <= 9 then 0UL else UInt64.MaxValue
            elif neg then
                0UL - acc
            else
                acc
        )

let private toBase (n: uint64) (b: int) : string =
    if n = 0UL then
        "0"
    else
        let mutable v = n
        let sb = StringBuilder()

        while v > 0UL do
            sb.Insert(0, baseDigits.[int (v % uint64 b)]) |> ignore
            v <- v / uint64 b

        sb.ToString()

/// A negative `to_base` asks for *signed* output — the 64-bit pattern is
/// reread as `int64` and printed with a leading `-` (MySQL-verified:
/// `CONV(-1, 10, -16)` is `-1`, where `CONV(-1, 10, 16)` is
/// `FFFFFFFFFFFFFFFF`). A negative `from_base` only means the input is
/// signed, which `parseInBase` already handles, so its magnitude is what
/// selects the digit set.
let private convFn: Scalar =
    function
    | [ n; f; t ] when not (anyNull [ n; f; t ]) ->
        let text = req n
        let fromBase = int (toDouble (roundNumeric f))
        let toBaseArg = int (toDouble (roundNumeric t))
        // Widened before `abs`: `abs Int32.MinValue` throws, and MySQL
        // answers NULL for that base rather than erroring.
        let magnitude = abs (int64 toBaseArg)
        let fromMagnitude = abs (int64 fromBase)

        if text = "" || magnitude < 2L || magnitude > 36L || fromMagnitude < 2L || fromMagnitude > 36L then
            VNull
        else
            let magnitude = int magnitude

            match parseInBase text (int fromMagnitude) with
            | None -> VNull
            | Some v when toBaseArg > 0 -> VString(toBase v magnitude)
            | Some v ->
                let signed = int64 v

                if signed < 0L then
                    // `-Int64.MinValue` overflows; its magnitude is exactly
                    // the same bit pattern read as unsigned.
                    VString("-" + toBase (0UL - v) magnitude)
                else
                    VString(toBase v magnitude)
    | _ -> VNull

let private binFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VString(toBase (toUInt64 v) 2)
    | _ -> VNull

let private octFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VString(toBase (toUInt64 v) 8)
    | _ -> VNull

/// The zlib/IEEE-802.3 CRC-32 MySQL's `CRC32()` implements — the standard
/// bit-reflected table-driven form, not a shortcut worth swapping for a
/// library dependency.
let private crc32Table =
    Array.init 256 (fun i ->
        let mutable c = uint32 i

        for _ in 0..7 do
            c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1

        c)

let private crc32 (bytes: byte[]) : uint32 =
    let mutable crc = 0xFFFFFFFFu
    for b in bytes do
        crc <- crc32Table.[int ((crc ^^^ uint32 b) &&& 0xFFu)] ^^^ (crc >>> 8)
    crc ^^^ 0xFFFFFFFFu

let private crc32Fn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (crc32 (Text.Encoding.UTF8.GetBytes(req v))))
    | _ -> VNull

let private uuidFn: Scalar = fun _ -> VString(Guid.NewGuid().ToString())

/// Accepts the three textual UUID spellings `UUID_TO_BIN`/`IS_UUID` do:
/// dashed (`8-4-4-4-12`), undashed (32 hex digits), either optionally
/// wrapped in `{}` — and returns the 32 hex digits in field order with no
/// separators, or `None` if `s` isn't one of those three shapes.
let private normalizeUuidHex (s: string) : string option =
    let inner =
        let t = s.Trim()
        if t.StartsWith "{" && t.EndsWith "}" then t.Substring(1, t.Length - 2) else t

    let dashed =
        Regex.Match(inner, @"^([0-9A-Fa-f]{8})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{4})-([0-9A-Fa-f]{12})$")

    if dashed.Success then
        Some(dashed.Groups |> Seq.cast<Group> |> Seq.skip 1 |> Seq.map (fun g -> g.Value) |> String.concat "")
    elif inner.Length = 32 && inner |> Seq.forall Uri.IsHexDigit then
        Some inner
    else
        None

/// Swapped byte order moves the time-high and time-mid fields ahead of
/// time-low (`time_hi | time_mid | time_low | clock_seq | node` instead of
/// the RFC 4122 field order) so a UUIDv1's mostly-incrementing time-low
/// isn't the top bits of an indexed binary column — verified byte-for-byte
/// against `UUID_TO_BIN`'s own oracle output, not derived from the RFC.
let private swapUuidBytes (standard: byte[]) : byte[] =
    Array.concat [ standard.[6..7]; standard.[4..5]; standard.[0..3]; standard.[8..15] ]

let private unswapUuidBytes (swapped: byte[]) : byte[] =
    Array.concat [ swapped.[4..7]; swapped.[2..3]; swapped.[0..1]; swapped.[8..15] ]

let private uuidToBinFn: Scalar =
    let bytesOf (hex32: string) (swap: bool) : byte[] =
        let standard = [| for i in 0 .. 2 .. 30 -> Convert.ToByte(hex32.Substring(i, 2), 16) |]
        if swap then swapUuidBytes standard else standard

    function
    | [ v ] when not (anyNull [ v ]) -> normalizeUuidHex (req v) |> Option.map (fun h -> VBytes(bytesOf h false)) |> Option.defaultValue VNull
    | [ v; s ] when not (anyNull [ v; s ]) ->
        normalizeUuidHex (req v) |> Option.map (fun h -> VBytes(bytesOf h (toDouble s <> 0.0))) |> Option.defaultValue VNull
    | _ -> VNull

let private binToUuidFn: Scalar =
    let format (b: byte[]) : Value =
        if b.Length <> 16 then
            VNull
        else
            let hex = b |> Array.map (fun x -> x.ToString "x2") |> String.concat ""

            VString(
                sprintf
                    "%s-%s-%s-%s-%s"
                    (hex.Substring(0, 8))
                    (hex.Substring(8, 4))
                    (hex.Substring(12, 4))
                    (hex.Substring(16, 4))
                    (hex.Substring(20, 12))
            )

    function
    | [ v ] when not (anyNull [ v ]) -> format (Text.Encoding.Latin1.GetBytes(req v))
    | [ v; s ] when not (anyNull [ v; s ]) ->
        let raw = Text.Encoding.Latin1.GetBytes(req v)

        if raw.Length <> 16 then
            VNull
        else
            format (if toDouble s <> 0.0 then unswapUuidBytes raw else raw)
    | _ -> VNull

let private isUuidFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> if (normalizeUuidHex (req v)).IsSome then VInt 1L else VInt 0L
    | _ -> VNull

let private inetAtonFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        match IPAddress.TryParse(req v) with
        | true, ip when ip.AddressFamily = AddressFamily.InterNetwork ->
            let b = ip.GetAddressBytes()
            VInt(int64 ((uint32 b.[0] <<< 24) ||| (uint32 b.[1] <<< 16) ||| (uint32 b.[2] <<< 8) ||| uint32 b.[3]))
        | _ -> VNull
    | _ -> VNull

let private inetNtoaFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        let n = uint32 (toDouble v)
        VString(sprintf "%d.%d.%d.%d" ((n >>> 24) &&& 0xFFu) ((n >>> 16) &&& 0xFFu) ((n >>> 8) &&& 0xFFu) (n &&& 0xFFu))
    | _ -> VNull

// ---------------------------------------------------------------------------
// Aggregates: COUNT/SUM/AVG/MIN/MAX. Each `Aggregate` here only ever sees a
// nonempty, already NULL-filtered `Value list` — `Executor.evalAggregate`
// handles the empty-list-is-NULL case (and COUNT(*)'s row-counting, which
// isn't a fold over evaluated values at all) before calling in.
// ---------------------------------------------------------------------------

let private countAgg: Aggregate = fun vs -> VInt(int64 (List.length vs))

/// MySQL promotes SUM over exact integer inputs to DECIMAL rather than
/// preserving the integer runtime type. Besides avoiding BIGINT overflow,
/// this is observable in the resultset's column-definition packet: drivers
/// see NEWDECIMAL for SUM(bigint), not LONGLONG. Accumulate exact numeric
/// inputs as decimal from the first value so two valid BIGINT operands can
/// produce a result larger than BIGINT without overflowing along the way.
let private sumAgg: Aggregate =
    fun values ->
        if
            values
            |> List.forall (function
                | VInt _
                | VUInt _
                | VDecimal _ -> true
                | _ -> false)
        then
            values
            |> List.fold
                (fun total value ->
                    match value with
                    | VInt integer -> total + decimal integer
                    | VUInt unsigned -> total + decimal unsigned
                    | VDecimal number -> total + number
                    | _ -> total)
                0M
            |> VDecimal
        else
            List.reduce Value.add values

/// AVG shares SUM's exact-decimal accumulation rather than reducing with
/// `Value.add`: MySQL's AVG over exact inputs is a DECIMAL too, and summing
/// `BIGINT UNSIGNED` rows through unsigned arithmetic would leave the
/// unsigned domain (error 1690) on the way to a perfectly ordinary average.
let private avgAgg: Aggregate = fun vs -> Value.div (sumAgg vs) (VInt(int64 (List.length vs)))
let private minAgg: Aggregate = List.reduce (fun a b -> if Value.compare a b <= 0 then a else b)
let private maxAgg: Aggregate = List.reduce (fun a b -> if Value.compare a b >= 0 then a else b)

/// Population variance divides by `n`, sample variance by `n - 1` — the
/// latter is undefined (MySQL: NULL, not an error) for the single-row
/// group `Executor.evalAggregate` still calls this with (it only
/// short-circuits truly *empty* groups to NULL before reaching here).
let private variance (sample: bool) (values: Value list) : float option =
    let xs = values |> List.map toDouble
    let n = float xs.Length

    if sample && n < 2.0 then
        None
    else
        let mean = List.sum xs / n
        let sumSquares = xs |> List.sumBy (fun x -> (x - mean) ** 2.0)
        Some(sumSquares / (if sample then n - 1.0 else n))

let private stddevPopAgg: Aggregate = fun vs -> variance false vs |> Option.get |> sqrt |> VDouble
let private stddevSampAgg: Aggregate = fun vs -> variance true vs |> Option.map (sqrt >> VDouble) |> Option.defaultValue VNull
let private varPopAgg: Aggregate = fun vs -> variance false vs |> Option.get |> VDouble
let private varSampAgg: Aggregate = fun vs -> variance true vs |> Option.map VDouble |> Option.defaultValue VNull

/// `BIT_AND`/`BIT_OR`/`BIT_XOR` over a truly empty group have MySQL
/// identities of all-ones/all-zero/all-zero respectively. ponytail:
/// `Executor.evalAggregate` short-circuits an empty (post-NULL-filter)
/// group to NULL before any `Aggregate` ever runs, so those identities
/// can't surface through this registry entry as it stands; giving
/// `evalAggregate` a registry-driven empty-group identity (instead of the
/// hardcoded NULL) would let this fold in like any other case.
let private bitAndAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (&&&))
let private bitOrAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (|||))
let private bitXorAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (^^^))

// ---------------------------------------------------------------------------
// MySQL 9 VECTOR. A vector is a `VBytes` of little-endian 4-byte floats —
// no new `Value` case, so storage/persistence/wire all carry it as the
// binary string real pre-9 clients see anyway. ponytail: only these
// functions interpret the bytes — no whitelist polices which expressions a
// vector flows through; anything byte-shaped is allowed until it reaches a
// function that must decode it.
// ---------------------------------------------------------------------------

/// MySQL 9's own refusal shape for a value that can't become (or isn't) a
/// vector, reused for every malformed input below.
let private vectorError (detail: string) : 'a =
    raise (SqlError(6138, sprintf "Data cannot be converted to vector: %s" detail))

let private vectorOfBytes (b: byte[]) : float32[] =
    if b.Length = 0 || b.Length % 4 <> 0 then
        vectorError (sprintf "%d bytes is not a whole number of 4-byte floats" b.Length)

    Array.init (b.Length / 4) (fun i -> BitConverter.ToSingle(b, i * 4))

let private bytesOfVector (fs: float32[]) : byte[] =
    let bytes = Array.zeroCreate (fs.Length * 4)

    fs
    |> Array.iteri (fun i f -> BitConverter.TryWriteBytes(Span(bytes, i * 4, 4), f) |> ignore)

    bytes

/// `STRING_TO_VECTOR('[1.05, -17.8]')` / `TO_VECTOR` — MySQL rejects the
/// empty vector, non-numeric elements, and more than 16383 dimensions.
let private stringToVectorFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VBytes b ] -> VBytes b // already a vector — MySQL passes it through
    | [ v ] ->
        let s = (toText v |> Option.defaultValue "").Trim()

        if not (s.StartsWith "[" && s.EndsWith "]" && s.Length >= 2) then
            vectorError (sprintf "'%s'" s)

        let inner = s.Substring(1, s.Length - 2).Trim()

        if inner = "" then vectorError (sprintf "'%s'" s)

        // Count separators before splitting so a literal with millions of
        // commas is rejected without first allocating the whole split array.
        let commaCount = inner |> Seq.sumBy (fun c -> if c = ',' then 1 else 0)

        if commaCount + 1 > 16383 then vectorError (sprintf "%d dimensions exceeds the maximum 16383" (commaCount + 1))

        let parts = inner.Split ','

        if parts.Length > 16383 then vectorError (sprintf "%d dimensions exceeds the maximum 16383" parts.Length)

        parts
        |> Array.map (fun p ->
            match Single.TryParse(p.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, f when Single.IsFinite f -> f
            | _ -> vectorError (sprintf "'%s'" s))
        |> bytesOfVector
        |> VBytes
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'string_to_vector'"))

/// `VECTOR_TO_STRING` / `FROM_VECTOR` — pinned to MySQL 9's exact scientific
/// rendering: 5 fractional digits, lowercase `e`, always-signed two-digit
/// exponent, comma-separated with no spaces (`[1.05000e+00,-1.78000e+01]`).
let private vectorToStringFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VBytes b ] ->
        vectorOfBytes b
        |> Array.map (fun f -> f.ToString("0.00000e+00", CultureInfo.InvariantCulture))
        |> String.concat ","
        |> sprintf "[%s]"
        |> VString
    | [ v ] -> vectorError (sprintf "'%s'" (toText v |> Option.defaultValue ""))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'vector_to_string'"))

let private vectorDimFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ VBytes b ] -> VInt(int64 (vectorOfBytes b).Length)
    | [ v ] -> vectorError (sprintf "'%s'" (toText v |> Option.defaultValue ""))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'vector_dim'"))

/// `DISTANCE(v1, v2, 'COSINE'|'EUCLIDEAN'|'DOT')` / `VECTOR_DISTANCE` —
/// HeatWave-only in real MySQL, so purely additive here. Brute force over
/// the decoded floats; 1210 (ER_WRONG_ARGUMENTS) for a dimension mismatch
/// or an unknown metric, the generic real-MySQL code since the HeatWave
/// numbers can't be oracle-verified. COSINE is `1 - cos(a,b)`, EUCLIDEAN is
/// the L2 norm of the difference, DOT is the raw dot product.
let private distanceFn: Scalar =
    function
    | args when anyNull args -> VNull
    | [ VBytes b1; VBytes b2; metric ] ->
        let v1 = vectorOfBytes b1
        let v2 = vectorOfBytes b2

        if v1.Length <> v2.Length then
            raise (SqlError(1210, sprintf "Incorrect arguments to DISTANCE: vectors have different dimensions %d and %d" v1.Length v2.Length))

        let dot = Array.fold2 (fun acc (a: float32) (b: float32) -> acc + float a * float b) 0.0 v1 v2

        match (toText metric |> Option.defaultValue "").ToUpperInvariant() with
        | "EUCLIDEAN" -> VDouble(sqrt (Array.fold2 (fun acc (a: float32) (b: float32) -> acc + (float a - float b) ** 2.0) 0.0 v1 v2))
        | "DOT" -> VDouble dot
        | "COSINE" ->
            let norm (v: float32[]) = sqrt (v |> Array.sumBy (fun x -> float x * float x))
            let n1, n2 = norm v1, norm v2

            if n1 = 0.0 || n2 = 0.0 then
                raise (SqlError(1210, "Incorrect arguments to DISTANCE: cosine distance is undefined for a zero vector"))

            VDouble(1.0 - dot / (n1 * n2))
        | other -> raise (SqlError(1210, sprintf "Incorrect arguments to DISTANCE: unknown metric '%s'" other))
    | [ _; _; _ ] -> raise (SqlError(1210, "Incorrect arguments to DISTANCE: arguments must be vectors"))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'distance'"))

let builtins: Registry =
    empty
    |> registerScalar "NOW" nowFn
    |> registerScalar "CURRENT_TIMESTAMP" nowFn
    |> registerScalar "CONCAT" concatFn
    |> registerScalar "UPPER" (textMap (fun s -> s.ToUpperInvariant()))
    |> registerScalar "LOWER" (textMap (fun s -> s.ToLowerInvariant()))
    |> registerScalar "LENGTH" lengthFn
    |> registerScalar "CHAR_LENGTH" charLengthFn
    |> registerScalar "COALESCE" coalesceFn
    |> registerScalar "IFNULL" ifNullFn
    |> registerScalar "IF" ifFn
    |> registerScalar "ABS" absFn
    |> registerScalar "ROUND" roundFn
    |> registerScalar "MOD" modFn
    // JSON
    |> registerScalar "JSON_EXTRACT" jsonExtractFn
    |> registerScalar "JSON_UNQUOTE" jsonUnquoteFn
    |> registerScalar "JSON_CONTAINS" jsonContainsFn
    |> registerScalar "JSON_SET" (jsonWriteFn JSet)
    |> registerScalar "JSON_INSERT" (jsonWriteFn JInsert)
    |> registerScalar "JSON_REPLACE" (jsonWriteFn JReplace)
    |> registerScalar "JSON_REMOVE" jsonRemoveFn
    |> registerScalar "JSON_ARRAY" jsonArrayFn
    |> registerScalar "JSON_OBJECT" jsonObjectFn
    |> registerScalar "JSON_LENGTH" jsonLengthFn
    |> registerScalar "JSON_DEPTH" jsonDepthFn
    |> registerScalar "JSON_VALID" jsonValidFn
    |> registerScalar "JSON_TYPE" jsonTypeFn
    |> registerScalar "JSON_KEYS" jsonKeysFn
    |> registerScalar "JSON_SEARCH" jsonSearchFn
    // Dates
    |> registerScalar "DATE_ADD" (dateAddCore 1.0)
    |> registerScalar "TIMESTAMPADD" timestampAddFn
    |> registerScalar "ADDDATE" (addSubDateCore 1.0)
    |> registerScalar "DATE_SUB" (dateAddCore -1.0)
    |> registerScalar "SUBDATE" (addSubDateCore -1.0)
    |> registerScalar "INTERVAL" intervalFn
    |> registerScalar "DATEDIFF" dateDiffFn
    |> registerScalar "DATE_FORMAT" dateFormatFn
    |> registerScalar "CONVERT" convertFn
    |> registerScalar "DATE" dateFn
    |> registerScalar "TIME" timeFn
    |> registerScalar "TIMESTAMP" timestampFn
    |> registerScalar "YEAR" (datePartFn (fun d -> d.Year))
    |> registerScalar "MONTH" (datePartFn (fun d -> d.Month))
    |> registerScalar "DAY" (datePartFn (fun d -> d.Day))
    |> registerScalar "DAYOFMONTH" (datePartFn (fun d -> d.Day))
    |> registerScalar "HOUR" (datePartFn (fun d -> d.Hour))
    |> registerScalar "MINUTE" (datePartFn (fun d -> d.Minute))
    |> registerScalar "SECOND" (datePartFn (fun d -> d.Second))
    |> registerScalar "DAYOFWEEK" (datePartFn (fun d -> int d.DayOfWeek + 1))
    |> registerScalar "DAYOFYEAR" (datePartFn (fun d -> d.DayOfYear))
    |> registerScalar "DAYNAME" dayNameFn
    |> registerScalar "MONTHNAME" monthNameFn
    |> registerScalar "WEEK" weekFn
    |> registerScalar "WEEKDAY" weekdayFn
    |> registerScalar "WEEKOFYEAR" weekOfYearFn
    |> registerScalar "YEARWEEK" yearWeekFn
    |> registerScalar "QUARTER" (datePartFn (fun d -> (d.Month - 1) / 3 + 1))
    |> registerScalar "CURDATE" curDateFn
    |> registerScalar "CURRENT_DATE" curDateFn
    |> registerScalar "CURTIME" curTimeFn
    |> registerScalar "UNIX_TIMESTAMP" unixTimestampFn
    |> registerScalar "FROM_UNIXTIME" fromUnixTimeFn
    |> registerScalar "TIMESTAMPDIFF" timestampDiffFn
    |> registerScalar "EXTRACT" extractFn
    |> registerScalar "LAST_DAY" lastDayFn
    |> registerScalar "MAKEDATE" makeDateFn
    |> registerScalar "CONVERT_TZ" convertTzFn
    |> registerScalar "STR_TO_DATE" strToDateFn
    // Strings
    |> registerScalar "SUBSTRING" substringFn
    |> registerScalar "SUBSTR" substringFn
    |> registerScalar "LOCATE" locateFn
    |> registerScalar "INSTR" instrFn
    |> registerScalar "POSITION" locateFn
    |> registerScalar "REPLACE" replaceFn
    |> registerScalar "TRIM" (textMap (fun s -> s.Trim()))
    |> registerScalar "LTRIM" (textMap (fun s -> s.TrimStart()))
    |> registerScalar "RTRIM" (textMap (fun s -> s.TrimEnd()))
    |> registerScalar "LPAD" (padFn true)
    |> registerScalar "RPAD" (padFn false)
    |> registerScalar "LEFT" leftFn
    |> registerScalar "RIGHT" rightFn
    |> registerScalar "REVERSE" (textMap (fun s -> String(Array.rev (s.ToCharArray()))))
    |> registerScalar "REPEAT" repeatFn
    |> registerScalar "SPACE" spaceFn
    |> registerScalar "ASCII" asciiFn
    |> registerScalar "CHAR" charFn
    |> registerScalar "HEX" hexFn
    |> registerScalar "UNHEX" unhexFn
    |> registerScalar "MD5" md5Fn
    |> registerScalar "SHA1" sha1Fn
    |> registerScalar "SHA2" sha2Fn
    |> registerScalar "FORMAT" formatFn
    |> registerScalar "SUBSTRING_INDEX" substringIndexFn
    |> registerScalar "CONCAT_WS" concatWsFn
    |> registerScalar "ELT" eltFn
    |> registerScalar "EXPORT_SET" exportSetFn
    |> registerScalar "FIELD" fieldFn
    |> registerScalar "FIND_IN_SET" findInSetFn
    |> registerScalar "QUOTE" quoteFn
    |> registerScalar "STRCMP" strcmpFn
    |> registerScalar "REGEXP_LIKE" regexpLikeFn
    |> registerScalar "REGEXP_REPLACE" regexpReplaceFn
    |> registerScalar "REGEXP_SUBSTR" regexpSubstrFn
    |> registerScalar "REGEXP_INSTR" regexpInstrFn
    // Math/misc
    |> registerScalar "CEIL" ceilFn
    |> registerScalar "CEILING" ceilFn
    |> registerScalar "FLOOR" floorFn
    |> registerScalar "POW" powFn
    |> registerScalar "POWER" powFn
    |> registerScalar "SQRT" sqrtFn
    |> registerScalar "SIGN" signFn
    |> registerScalar "TRUNCATE" truncateFn
    |> registerScalar "RAND" randFn
    |> registerScalar "GREATEST" greatestFn
    |> registerScalar "LEAST" leastFn
    |> registerScalar "NULLIF" nullIfFn
    |> registerScalar "ISNULL" isNullFn
    |> registerScalar "CONV" convFn
    |> registerScalar "BIN" binFn
    |> registerScalar "BIT_COUNT" bitCountFn
    |> registerScalar "OCT" octFn
    |> registerScalar "CRC32" crc32Fn
    |> registerScalar "UUID" uuidFn
    |> registerScalar "UUID_TO_BIN" uuidToBinFn
    |> registerScalar "BIN_TO_UUID" binToUuidFn
    |> registerScalar "IS_UUID" isUuidFn
    |> registerScalar "INET_ATON" inetAtonFn
    |> registerScalar "INET_NTOA" inetNtoaFn
    // MySQL 9 VECTOR
    |> registerScalar "STRING_TO_VECTOR" stringToVectorFn
    |> registerScalar "TO_VECTOR" stringToVectorFn
    |> registerScalar "VECTOR_TO_STRING" vectorToStringFn
    |> registerScalar "FROM_VECTOR" vectorToStringFn
    |> registerScalar "VECTOR_DIM" vectorDimFn
    |> registerScalar "DISTANCE" distanceFn
    |> registerScalar "VECTOR_DISTANCE" distanceFn
    |> registerAggregate "COUNT" countAgg
    |> registerAggregate "SUM" sumAgg
    |> registerAggregate "AVG" avgAgg
    |> registerAggregate "MIN" minAgg
    |> registerAggregate "MAX" maxAgg
    |> registerAggregate "STDDEV_POP" stddevPopAgg
    |> registerAggregate "STDDEV" stddevPopAgg
    |> registerAggregate "STD" stddevPopAgg
    |> registerAggregate "STDDEV_SAMP" stddevSampAgg
    |> registerAggregate "VAR_POP" varPopAgg
    |> registerAggregate "VARIANCE" varPopAgg
    |> registerAggregate "VAR_SAMP" varSampAgg
    |> registerAggregate "BIT_AND" bitAndAgg
    |> registerAggregate "BIT_OR" bitOrAgg
    |> registerAggregate "BIT_XOR" bitXorAgg
