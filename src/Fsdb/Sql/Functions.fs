/// Scalar and aggregate function registries used by every SQL function call.
module Fsdb.Functions

open System
open System.Globalization
open System.IO
open System.IO.Compression
open System.Net
open System.Net.Sockets
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open NJsonSchema
open NJsonSchema.Validation
open Fsdb.Value
open Fsdb.Temporal

/// A scalar function: its already-evaluated arguments in, one `Value` out.
type Scalar = Value list -> Value

/// An extension-function failure with an explicit MySQL error code.
exception SqlError of code: int * message: string

/// Statement context supplied to registered scalar functions.
/// `Cancellation` is signalled when the client disconnects or is killed.
type QueryContext =
    { Database: string option
      User: string
      Cancellation: System.Threading.CancellationToken }

/// A context-aware scalar function and its execution constraints.
/// Direct-only functions are rejected from stored expressions;
/// deterministic metadata is available to host-side caches.
type ScalarFunction =
    { Name: string
      Fn: QueryContext -> Value list -> Value
      Deterministic: bool
      DirectOnly: bool
      Signature: ScalarSignature option }

and ScalarSignature =
    { Parameters: Ast.ColumnType list
      Result: Ast.ColumnType }

module ScalarFunction =
    /// Deterministic, callable anywhere — the default shape.
    let create (name: string) (fn: QueryContext -> Value list -> Value) : ScalarFunction =
        { Name = name
          Fn = fn
          Deterministic = true
          DirectOnly = false
          Signature = None }

    /// Declares the SQL parameter and result types exposed during prepare.
    let withSignature (parameters: Ast.ColumnType list) (result: Ast.ColumnType) (fn: ScalarFunction) : ScalarFunction =
        { fn with Signature = Some { Parameters = parameters; Result = result } }

    /// Marks a function non-deterministic and unavailable to stored expressions.
    let effectful (fn: ScalarFunction) : ScalarFunction =
        { fn with Deterministic = false; DirectOnly = true }

/// A host-provided read-only table in the reserved `fsdb` schema.
/// `Rows` is evaluated once per referencing statement before SQL filtering.
type VirtualTable =
    { Name: string
      Columns: Ast.ColumnDef list
      Rows: unit -> Value[] list }

module VirtualTable =
    /// Creates a nullable column with server-default charset and collation.
    let private col (name: string) (ty: Ast.ColumnType) : Ast.ColumnDef =
        { Name = name
          Type = ty
          NumericDisplay = None
          Nullable = true
          Default = None
          AutoIncrement = false
          PrimaryKey = false
          Unique = false
          OnUpdateCurrentTimestamp = false
          Generated = None
          Comment = ""
          Collation = None
          Charset = None
          Srid = None }

    let text (name: string) : Ast.ColumnDef = col name Ast.TText
    let int (name: string) : Ast.ColumnDef = col name (Ast.TInt false)
    let bigint (name: string) : Ast.ColumnDef = col name (Ast.TBigInt false)
    let double (name: string) : Ast.ColumnDef = col name (Ast.TDouble false)

    let create (name: string) (columns: Ast.ColumnDef list) (rows: unit -> Value[] list) : VirtualTable =
        { Name = name; Columns = columns; Rows = rows }

/// An aggregate over already-evaluated, non-NULL argument values.
/// `COUNT(*)` is handled directly by the executor.
type Aggregate = Value list -> Value

/// Result collation lives beside invocation semantics so adding a builtin
/// cannot require a second function-name list in the executor.
type ResultCollation =
    | InheritArgument of index: int
    | CombineArguments of includeArgument: (int -> bool)
    | FixedCollation of name: string * coercibility: int

/// Case-insensitive scalar, aggregate, and extension registrations.
type Registry =
    { Scalars: Map<string, Scalar>
      ScalarMetadata: Map<string, ColumnMetadata>
      ScalarParameters: Map<string, ColumnMetadata list>
      TextArguments: Map<string, int -> bool>
      ByteArguments: Map<string, int -> bool>
      ResultCollations: Map<string, ResultCollation>
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
      ScalarMetadata = Map.empty
      ScalarParameters = Map.empty
      TextArguments = Map.empty
      ByteArguments = Map.empty
      ResultCollations = Map.empty
      Aggregates = Map.empty
      Extensions = Map.empty }

let registerScalar (name: string) (fn: Scalar) (registry: Registry) : Registry =
    let name = name.ToUpperInvariant()

    { registry with
        Scalars = Map.add name fn registry.Scalars
        ScalarMetadata = Map.remove name registry.ScalarMetadata
        ScalarParameters = Map.remove name registry.ScalarParameters
        TextArguments = Map.remove name registry.TextArguments
        ByteArguments = Map.remove name registry.ByteArguments
        ResultCollations = Map.remove name registry.ResultCollations }

let registerScalarWithMetadata (name: string) metadata (fn: Scalar) (registry: Registry) : Registry =
    let name = name.ToUpperInvariant()

    { registry with
        Scalars = Map.add name fn registry.Scalars
        ScalarMetadata = Map.add name metadata registry.ScalarMetadata
        ScalarParameters = Map.remove name registry.ScalarParameters
        TextArguments = Map.remove name registry.TextArguments
        ByteArguments = Map.remove name registry.ByteArguments
        ResultCollations = Map.remove name registry.ResultCollations }

let internal registerScalarWithSignature
    (name: string)
    (parameters: ColumnMetadata list)
    (result: ColumnMetadata)
    (fn: Scalar)
    (registry: Registry)
    : Registry =
    let name = name.ToUpperInvariant()
    let registry = registerScalarWithMetadata name result fn registry
    { registry with ScalarParameters = Map.add name parameters registry.ScalarParameters }

let internal registerTextScalar (name: string) (textArgument: int -> bool) (fn: Scalar) (registry: Registry) : Registry =
    let name = name.ToUpperInvariant()
    let registry = registerScalar name fn registry
    { registry with TextArguments = Map.add name textArgument registry.TextArguments }

let private registerByteArguments (name: string) (byteArgument: int -> bool) (registry: Registry) =
    { registry with ByteArguments = Map.add (name.ToUpperInvariant()) byteArgument registry.ByteArguments }

let private registerByteScalar name byteArgument fn registry =
    registry
    |> registerScalar name fn
    |> registerByteArguments name byteArgument

let private registerByteTextScalar name byteArgument fn registry =
    registry
    |> registerTextScalar name byteArgument fn
    |> registerByteArguments name byteArgument

let private registerResultCollation (name: string) (policy: ResultCollation) (registry: Registry) =
    { registry with
        ResultCollations = Map.add (name.ToUpperInvariant()) policy registry.ResultCollations }

let internal registerStringScalar name textArgument resultCollation fn registry =
    registry
    |> registerTextScalar name textArgument fn
    |> registerResultCollation name resultCollation

let private registerByteStringScalar name byteArgument resultCollation fn registry =
    registry
    |> registerStringScalar name byteArgument resultCollation fn
    |> registerByteArguments name byteArgument

let private registerScalarResult name resultCollation fn registry =
    registry
    |> registerScalar name fn
    |> registerResultCollation name resultCollation

let private binaryResult = FixedCollation("binary", 4)
let private jsonResult = FixedCollation("utf8mb4_bin", 2)
let private jsonTextResult = FixedCollation("utf8mb4_bin", 4)

let registerAggregate (name: string) (fn: Aggregate) (registry: Registry) : Registry =
    { registry with Aggregates = Map.add (name.ToUpperInvariant()) fn registry.Aggregates }

let registerExtension (fn: ScalarFunction) (registry: Registry) : Registry =
    { registry with Extensions = Map.add (fn.Name.ToUpperInvariant()) fn registry.Extensions }

let lookup (name: string) (registry: Registry) : Scalar option =
    Map.tryFind (name.ToUpperInvariant()) registry.Scalars

let lookupScalarMetadata (name: string) (registry: Registry) : ColumnMetadata option =
    Map.tryFind (name.ToUpperInvariant()) registry.ScalarMetadata

let internal lookupScalarParameters (name: string) (registry: Registry) : ColumnMetadata list option =
    Map.tryFind (name.ToUpperInvariant()) registry.ScalarParameters

let internal isTextArgument (name: string) index (registry: Registry) =
    registry.TextArguments
    |> Map.tryFind (name.ToUpperInvariant())
    |> Option.exists (fun predicate -> predicate index)

let internal isByteArgument (name: string) index (registry: Registry) =
    registry.ByteArguments
    |> Map.tryFind (name.ToUpperInvariant())
    |> Option.exists (fun predicate -> predicate index)

let internal lookupResultCollation (name: string) (registry: Registry) =
    registry.ResultCollations |> Map.tryFind (name.ToUpperInvariant())

let lookupAggregate (name: string) (registry: Registry) : Aggregate option =
    Map.tryFind (name.ToUpperInvariant()) registry.Aggregates

let private stringBytes (value: Value) =
    tryRawBytes value |> Option.defaultWith (fun () -> Text.Encoding.UTF8.GetBytes(toText value |> Option.defaultValue ""))

let private hasRawBytes values = values |> List.exists (tryRawBytes >> Option.isSome)

let private binaryText value = stringBytes value |> Text.Encoding.Latin1.GetString

let private binaryValue (text: string) = text |> Text.Encoding.Latin1.GetBytes |> VBytes

let private concatFn (args: Value list) : Value =
    // MySQL: CONCAT returns NULL if any argument is NULL.
    if args |> List.exists (function VNull -> true | _ -> false) then
        VNull
    elif hasRawBytes args then
        args |> List.toArray |> Array.collect stringBytes |> VBytes
    else
        args |> List.map (toText >> Option.defaultValue "") |> String.concat "" |> VString

let private trimRaw trimLeading trimTrailing (bytes: byte[]) =
    let mutable first = 0
    let mutable last = bytes.Length - 1

    if trimLeading then
        while first <= last && bytes.[first] = 0x20uy do
            first <- first + 1

    if trimTrailing then
        while last >= first && bytes.[last] = 0x20uy do
            last <- last - 1

    if first > last then VBytes [||] else VBytes(bytes.[first..last])

let private textMap rawMap (f: string -> string) : Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match tryRawBytes value with
        | Some bytes -> rawMap bytes
        | None -> value |> toText |> Option.defaultValue "" |> f |> VString
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
    | VBit(_, value) -> value
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
    | [ value ] ->
        match tryRawBytes value with
        | Some bytes -> VInt(int64 bytes.Length)
        | None -> value |> toText |> Option.defaultValue "" |> Text.Encoding.UTF8.GetByteCount |> int64 |> VInt
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

let private bitLengthFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match tryRawBytes value with
        | Some bytes -> VInt(int64 bytes.Length * 8L)
        | None -> VInt(int64 (Text.Encoding.UTF8.GetByteCount(req value)) * 8L)
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
    | [ a; b ] when a <> VNull && isArithmeticZero b ->
        match Diagnostics.divisionByZero () with
        | Ok() -> VNull
        | Error(code, message) -> raise (Diagnostics.EvaluationError(code, message))
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

// JSON_TABLE shares this path implementation with scalar JSON functions so
// path parsing cannot diverge between the two call sites.

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
let private jsonEqual (left: JsonNode) (right: JsonNode) : bool =
    match left, right with
    | null, null -> true
    | null, _
    | _, null -> false
    | _ ->
        try
            left.GetValueKind() = right.GetValueKind() && formatJsonNode left = formatJsonNode right
        with _ ->
            false

let rec private jsonContains (target: JsonNode) (candidate: JsonNode) : bool =
    match target, candidate with
    | null, null -> true
    | null, _
    | _, null -> false
    | (:? JsonObject as t), (:? JsonObject as c) ->
        c |> Seq.forall (fun kv -> t.ContainsKey kv.Key && jsonContains t.[kv.Key] kv.Value)
    | (:? JsonArray as t), (:? JsonArray as c) -> c |> Seq.forall (fun cv -> t |> Seq.exists (fun tv -> jsonContains tv cv))
    | (:? JsonArray as t), _ -> t |> Seq.exists (fun tv -> jsonContains tv candidate)
    | _ -> jsonEqual target candidate

/// JSON_LENGTH: object/array count their members/elements; a scalar (and
/// JSON null) counts as length 1, matching MySQL.
let private jsonNodeLength (node: JsonNode) : int =
    match node with
    | :? JsonObject as o -> o.Count
    | :? JsonArray as a -> a.Count
    | _ -> 1

let private tryParseJsonPaths pathArguments =
    let rec loop parsed =
        function
        | [] -> Some(List.rev parsed)
        | path :: rest ->
            match toText path |> Option.bind parseJsonPath with
            | Some segments -> loop (segments :: parsed) rest
            | None -> None

    loop [] pathArguments

let private tryExtractJsonNodes document pathArguments =
    match tryParseJsonValue document, tryParseJsonPaths pathArguments with
    | Some root, Some paths -> paths |> List.collect (navigateJson root) |> Some
    | _ -> None

let private jsonExtractFn: Scalar =
    function
    | doc :: (_ :: _ as pathArgs) when not (anyNull (doc :: pathArgs)) ->
        match tryExtractJsonNodes doc pathArgs with
        | None -> VNull
        | Some matches ->
            match matches, pathArgs with
            | [], _ -> VNull
            | [ single ], [ _ ] -> VJson(formatJsonNode single)
            | many, _ -> VJson("[" + (many |> List.map formatJsonNode |> String.concat ", ") + "]")
    | _ -> VNull

let private jsonValueFn: Scalar =
    function
    | [ document; path ] when not (anyNull [ document; path ]) ->
        match tryParseJsonValue document, toText path |> Option.bind parseJsonPath with
        | Some root, Some segments ->
            match navigateJson root segments with
            | [ null ] -> VNull
            | [ node ] when node.GetValueKind() = JsonValueKind.String -> VString(node.GetValue<string>())
            | [ node ] -> VString(formatJsonNode node)
            | _ -> VNull
        | _ -> VNull
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

let internal jsonExtractUnquotedFn: Scalar =
    function
    | doc :: (_ :: _ as pathArgs) when not (anyNull (doc :: pathArgs)) ->
        match tryExtractJsonNodes doc pathArgs with
        | None -> VNull
        | Some matches ->
            match matches, pathArgs with
            | [], _ -> VNull
            | [ null ], [ _ ] -> VString "null"
            | [ node ], [ _ ] when node.GetValueKind() = JsonValueKind.String -> VString(node.GetValue<string>())
            | [ node ], [ _ ] -> VString(formatJsonNode node)
            | many, _ -> VString("[" + (many |> List.map formatJsonNode |> String.concat ", ") + "]")
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

let private jsonMemberOfFn: Scalar =
    function
    | [ value; document ] when not (anyNull [ value; document ]) ->
        match tryParseJsonValue document with
        | Some(:? JsonArray as array) ->
            let candidate = valueToJsonNode value
            array |> Seq.exists (fun element -> jsonEqual element candidate) |> boolToInt
        | _ -> VNull
    | _ -> VNull

let private jsonContainsPathFn: Scalar =
    function
    | document :: mode :: (_ :: _ as paths) when not (anyNull (document :: mode :: paths)) ->
        match tryParseJsonValue document with
        | Some root ->
            let matches path =
                toText path
                |> Option.bind parseJsonPath
                |> Option.exists (navigateJson root >> List.isEmpty >> not)

            match (req mode).ToLowerInvariant() with
            | "one" -> paths |> List.exists matches |> boolToInt
            | "all" -> paths |> List.forall matches |> boolToInt
            | _ -> VNull
        | None -> VNull
    | _ -> VNull

let private jsonOverlaps (left: JsonNode) (right: JsonNode) : bool =
    match left, right with
    | (:? JsonArray as a), (:? JsonArray as b) -> a |> Seq.exists (fun x -> b |> Seq.exists (jsonEqual x))
    | (:? JsonObject as a), (:? JsonObject as b) ->
        a |> Seq.exists (fun pair -> b.ContainsKey pair.Key && jsonEqual pair.Value b.[pair.Key])
    | (:? JsonArray as array), scalar
    | scalar, (:? JsonArray as array) -> array |> Seq.exists (jsonEqual scalar)
    | _ -> jsonEqual left right

let private jsonOverlapsFn: Scalar =
    function
    | [ left; right ] when not (anyNull [ left; right ]) ->
        match tryParseJsonValue left, tryParseJsonValue right with
        | Some a, Some b -> jsonOverlaps a b |> boolToInt
        | _ -> VNull
    | _ -> VNull

let private jsonQuoteFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> value |> req |> jsonQuote |> VString
    | _ -> VNull

let private jsonPrettyFn: Scalar =
    let options = JsonSerializerOptions(WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

    function
    | [ document ] when not (anyNull [ document ]) ->
        tryParseJsonValue document
        |> Option.map (fun node ->
            if isNull node then
                "null"
            else
                JsonNode.Parse(formatJsonNode node).ToJsonString options)
        |> Option.map VString
        |> Option.defaultValue VNull
    | _ -> VNull

let private cloneJson (node: JsonNode) =
    if isNull node then null else node.DeepClone()

let rec private mergeJsonPatch (target: JsonNode) (patch: JsonNode) : JsonNode =
    match patch with
    | :? JsonObject as patchObject ->
        let result =
            match target with
            | :? JsonObject as targetObject -> targetObject.DeepClone() :?> JsonObject
            | _ -> JsonObject()

        for pair in patchObject do
            if isNull pair.Value then
                result.Remove pair.Key |> ignore
            else
                let previous = if result.ContainsKey pair.Key then result.[pair.Key] else null
                result.[pair.Key] <- mergeJsonPatch previous pair.Value

        result
    | _ -> cloneJson patch

let rec private mergeJsonPreserve (left: JsonNode) (right: JsonNode) : JsonNode =
    match left, right with
    | (:? JsonObject as a), (:? JsonObject as b) ->
        let result = a.DeepClone() :?> JsonObject

        for pair in b do
            if result.ContainsKey pair.Key then
                result.[pair.Key] <- mergeJsonPreserve result.[pair.Key] pair.Value
            else
                result.[pair.Key] <- cloneJson pair.Value

        result
    | (:? JsonArray as a), (:? JsonArray as b) ->
        JsonArray(Seq.append a b |> Seq.map cloneJson |> Array.ofSeq)
    | (:? JsonArray as array), value ->
        JsonArray(Seq.append array [ value ] |> Seq.map cloneJson |> Array.ofSeq)
    | value, (:? JsonArray as array) ->
        JsonArray(Seq.append [ value ] array |> Seq.map cloneJson |> Array.ofSeq)
    | _ -> JsonArray([| cloneJson left; cloneJson right |])

let private jsonMergeFn (merge: JsonNode -> JsonNode -> JsonNode) : Scalar =
    function
    | first :: (_ :: _ as rest) when not (anyNull (first :: rest)) ->
        let documents = first :: rest |> List.map tryParseJsonValue

        if documents |> List.exists Option.isNone then
            VNull
        else
            documents |> List.choose id |> List.reduce merge |> formatJsonNode |> VJson
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

let private jsonSchemaError functionName argument position =
    SqlError(3141, sprintf "Invalid JSON text in argument %d to function %s: \"Invalid value.\" at position %d." argument functionName position)

let private jsonSchemaObjectError functionName =
    SqlError(3853, sprintf "Invalid JSON type in argument 1 to function %s; an object is required." functionName)

let private maxJsonSchemaInputLength = 1_000_000
let private maxJsonSchemaDepth = 64
let private maxJsonSchemaPatternLength = 16_384
let private maxJsonSchemaRegexMatches = 1_024
let private jsonSchemaRegexTimeout = TimeSpan.FromMilliseconds 50.0
let private jsonSchemaRegexValidationTimeout = TimeSpan.FromMilliseconds 200.0

let private jsonSchemaLimitError =
    SqlError(1235, "This version of MySQL doesn't yet support JSON Schema inputs beyond its resource limits")

let private jsonSchemaRegexLimitError =
    SqlError(1235, "This version of MySQL doesn't yet support JSON Schema regular expressions beyond its resource limits")

let private parseJsonSchemaArgument functionName argument (value: Value) : JsonNode =
    match toText value with
    | None -> raise (jsonSchemaError functionName argument 0)
    | Some text ->
        if text.Length > maxJsonSchemaInputLength then
            raise jsonSchemaLimitError

        try
            JsonNode.Parse(text, JsonNodeOptions(), JsonDocumentOptions(MaxDepth = maxJsonSchemaDepth))
        with :? JsonException as error ->
            let position = if error.BytePositionInLine.HasValue then int error.BytePositionInLine.Value else 0
            raise (jsonSchemaError functionName argument position)

let private escapeJsonPointer (segment: string) =
    segment.Replace("~", "~0").Replace("/", "~1")

let rec private normalizeJsonSchema (node: JsonNode) : unit =
    match node with
    | :? JsonObject as obj ->
        match obj["$ref"] with
        | :? JsonValue as value ->
            match value.TryGetValue<string>() with
            | true, reference when not (reference.StartsWith "#") ->
                raise (SqlError(1235, "This version of MySQL doesn't yet support 'references in JSON Schema'"))
            | _ -> ()
        | _ -> ()

        obj |> Seq.iter (fun entry -> normalizeJsonSchema entry.Value)
    | :? JsonArray as array -> array |> Seq.iter normalizeJsonSchema
    | _ -> ()

let private tryJsonString (node: JsonNode) =
    match node with
    | :? JsonValue as value ->
        match value.TryGetValue<string>() with
        | true, text -> Some text
        | _ -> None
    | _ -> None

let private tryJsonObject (node: JsonNode) =
    match node with
    | :? JsonObject as obj -> Some obj
    | _ -> None

let private pointerSegment (segment: string) : string =
    segment.Replace("~1", "/").Replace("~0", "~")

let private pointerAppend location segment =
    location + "/" + escapeJsonPointer segment

let private sameJsonDocumentLocation (left: string) (right: string) =
    left.TrimEnd('/') = right.TrimEnd('/')

let private tryResolveJsonPointer (root: JsonObject) (reference: string) =
    let tryArrayIndex (array: JsonArray) (segment: string) =
        match Int32.TryParse segment with
        | true, index when index >= 0 && index < array.Count -> array[index] |> Option.ofObj
        | _ -> None

    match reference with
    | "#" -> Some(root :> JsonNode)
    | _ when reference.StartsWith "#/" ->
        reference.Substring(2).Split('/')
        |> Array.map pointerSegment
        |> Array.fold
            (fun current segment ->
                current
                |> Option.bind (fun node ->
                    match node with
                    | :? JsonObject as obj -> obj[segment] |> Option.ofObj
                    | :? JsonArray as array -> tryArrayIndex array segment
                    | _ -> None))
            (Some(root :> JsonNode))
    | _ -> None

let private exceedsJsonSchemaReferenceDepth (root: JsonObject) =
    let visiting = System.Collections.Generic.HashSet<JsonNode>(HashIdentity.Reference)

    let rec visit remainingDepth (node: JsonNode) =
        if remainingDepth < 0 then
            true
        elif isNull node then
            false
        elif not (visiting.Add node) then
            true
        else
            let recursive =
                match node with
                | :? JsonObject as obj ->
                    match obj["$ref"] |> tryJsonString with
                    | Some reference when reference.StartsWith "#" ->
                        tryResolveJsonPointer root reference |> Option.exists (visit (remainingDepth - 1))
                    | _ -> obj |> Seq.exists (fun property -> property.Key <> "$ref" && visit (remainingDepth - 1) property.Value)
                | :? JsonArray as array -> array |> Seq.exists (visit (remainingDepth - 1))
                | _ -> false

            visiting.Remove node |> ignore
            recursive

    visit maxJsonSchemaDepth root

type private JsonSchemaRegexBudget =
    { Started: System.Diagnostics.Stopwatch
      mutable Attempts: int }

let private tryMatchJsonSchemaPattern (budget: JsonSchemaRegexBudget) (pattern: string) (input: string) =
    let remaining = jsonSchemaRegexValidationTimeout - budget.Started.Elapsed

    if remaining <= TimeSpan.Zero then
        raise jsonSchemaRegexLimitError

    budget.Attempts <- budget.Attempts + 1

    if budget.Attempts > maxJsonSchemaRegexMatches then
        raise jsonSchemaRegexLimitError

    if pattern.Length > maxJsonSchemaPatternLength then
        raise jsonSchemaRegexLimitError

    try
        Some(Regex.IsMatch(input, pattern, RegexOptions.ECMAScript ||| RegexOptions.CultureInvariant, min jsonSchemaRegexTimeout remaining))
    with
    | :? ArgumentException -> None
    | :? RegexMatchTimeoutException -> raise jsonSchemaRegexLimitError

let rec private stripJsonSchemaRegularExpressions (node: JsonNode) : JsonNode =
    match node with
    | null -> null
    | :? JsonObject as obj ->
        let result = JsonObject()

        obj
        |> Seq.iter (fun property ->
            if property.Key <> "pattern" && property.Key <> "patternProperties" then
                result[property.Key] <- stripJsonSchemaRegularExpressions property.Value)

        result :> JsonNode
    | :? JsonArray as array ->
        let result = JsonArray()
        array |> Seq.iter (stripJsonSchemaRegularExpressions >> result.Add)
        result :> JsonNode
    | _ -> node.DeepClone()

let rec private containsJsonSchemaRegularExpressions (node: JsonNode) =
    match node with
    | :? JsonObject as obj ->
        obj.ContainsKey "pattern"
        || obj.ContainsKey "patternProperties"
        || (obj |> Seq.exists (fun property -> containsJsonSchemaRegularExpressions property.Value))
    | :? JsonArray as array -> array |> Seq.exists containsJsonSchemaRegularExpressions
    | _ -> false

let private compileJsonSchema (functionName: string) (text: string) : JsonSchema =
    try
        JsonSchema.FromJsonAsync(text).GetAwaiter().GetResult()
    with
    | :? Newtonsoft.Json.JsonException
    | :? ArgumentException -> raise (jsonSchemaObjectError functionName)

let private jsonSchemaValidatorSettings =
    JsonSchemaValidatorSettings(FormatValidators = [||])

let private schemaLocation (schema: JsonObject) (documentLocation: string) : string =
    let segments =
        documentLocation.TrimStart '#'
        |> fun path -> path.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun segment -> segment.Replace("~1", "/").Replace("~0", "~"))

    let rec find (location: string) (current: JsonNode) (remaining: string list) : string =
        match current, remaining with
        | :? JsonObject as obj, segment :: rest ->
            match obj["properties"] with
            | :? JsonObject as properties when properties.ContainsKey segment ->
                find (location + "/properties/" + escapeJsonPointer segment) properties[segment] rest
            | _ ->
                match obj["items"] with
                | :? JsonObject as items -> find (location + "/items") items rest
                | :? JsonArray as items ->
                    match Int32.TryParse segment with
                    | true, index when index >= 0 && index < items.Count -> find (location + "/items/" + string index) items[index] rest
                    | _ -> location
                | _ -> location
        | _ -> location

    find "#" schema (List.ofArray segments)

let private schemaKeyword =
    function
    | ValidationErrorKind.PropertyRequired -> "required"
    | ValidationErrorKind.StringExpected
    | ValidationErrorKind.NumberExpected
    | ValidationErrorKind.IntegerExpected
    | ValidationErrorKind.BooleanExpected
    | ValidationErrorKind.ObjectExpected
    | ValidationErrorKind.ArrayExpected
    | ValidationErrorKind.NullExpected
    | ValidationErrorKind.NoTypeValidates -> "type"
    | ValidationErrorKind.PatternMismatch -> "pattern"
    | ValidationErrorKind.StringTooShort -> "minLength"
    | ValidationErrorKind.StringTooLong -> "maxLength"
    | ValidationErrorKind.NumberTooSmall -> "minimum"
    | ValidationErrorKind.NumberTooBig
    | ValidationErrorKind.IntegerTooBig -> "maximum"
    | ValidationErrorKind.TooFewItems -> "minItems"
    | ValidationErrorKind.TooManyItems -> "maxItems"
    | ValidationErrorKind.ItemsNotUnique -> "uniqueItems"
    | ValidationErrorKind.NumberNotMultipleOf
    | ValidationErrorKind.IntegerNotMultipleOf -> "multipleOf"
    | ValidationErrorKind.NotInEnumeration -> "enum"
    | ValidationErrorKind.NotAnyOf -> "anyOf"
    | ValidationErrorKind.NotAllOf -> "allOf"
    | ValidationErrorKind.NotOneOf -> "oneOf"
    | ValidationErrorKind.ExcludedSchemaValidates -> "not"
    | ValidationErrorKind.NoAdditionalPropertiesAllowed
    | ValidationErrorKind.AdditionalPropertiesNotValid -> "additionalProperties"
    | ValidationErrorKind.TooFewProperties -> "minProperties"
    | ValidationErrorKind.TooManyProperties -> "maxProperties"
    | ValidationErrorKind.ArrayItemNotValid -> "items"
    | ValidationErrorKind.AdditionalItemNotValid
    | ValidationErrorKind.TooManyItemsInTuple -> "additionalItems"
    | other -> string other

type private JsonSchemaFailure =
    { SchemaLocation: string
      DocumentLocation: string
      Keyword: string }

let private neverValidJsonSchema () =
    let schema = JsonObject()
    schema["not"] <- JsonObject()
    schema :> JsonNode

let private addJsonSchemaConstraint (schema: JsonObject) (constraintSchema: JsonNode) =
    match schema["allOf"] with
    | null ->
        let constraints = JsonArray()
        constraints.Add constraintSchema
        schema["allOf"] <- constraints
    | :? JsonArray as constraints -> constraints.Add constraintSchema
    | _ -> ()

let private addPatternPropertyConstraint (schema: JsonObject) (propertyName: string) (constraintSchema: JsonNode) =
    let properties =
        match schema["properties"] with
        | :? JsonObject as properties -> properties
        | null ->
            let properties = JsonObject()
            schema["properties"] <- properties
            properties
        | _ -> JsonObject()

    match properties[propertyName] with
    | null -> properties[propertyName] <- constraintSchema
    | existing ->
        let combined = JsonObject()
        let constraints = JsonArray()
        constraints.Add(existing.DeepClone())
        constraints.Add constraintSchema
        combined["allOf"] <- constraints
        properties[propertyName] <- combined

let private isJsonSchemaPropertyPatternMatch (budget: JsonSchemaRegexBudget) (pattern: string) (propertyName: string) =
    tryMatchJsonSchemaPattern budget pattern propertyName |> Option.defaultValue false

let rec private rewriteJsonSchemaRegularExpressions
    (root: JsonObject)
    (cleanRoot: JsonObject)
    (source: JsonNode)
    (target: JsonNode)
    (document: JsonNode)
    schemaLocation
    documentLocation
    remainingDepth
    (seenReferences: Set<string>)
    (regexBudget: JsonSchemaRegexBudget)
    (patternFailures: ResizeArray<JsonSchemaFailure>)
    (patternPropertyMatches: ResizeArray<JsonSchemaFailure>)
    =
    if remainingDepth < 0 then
        raise jsonSchemaLimitError

    match source, target with
    | (:? JsonObject as sourceSchema), (:? JsonObject as targetSchema) ->
        let sourceSchema, targetSchema, seenReferences =
            match sourceSchema["$ref"] |> tryJsonString with
            | Some reference when reference.StartsWith "#" ->
                if seenReferences.Contains reference then
                    raise jsonSchemaRegexLimitError

                match tryResolveJsonPointer root reference |> Option.bind tryJsonObject, tryResolveJsonPointer cleanRoot reference |> Option.bind tryJsonObject with
                | Some sourceReference, Some targetReference -> sourceReference, targetReference, seenReferences.Add reference
                | _ -> raise jsonSchemaLimitError
            | _ -> sourceSchema, targetSchema, seenReferences

        match sourceSchema["pattern"] |> tryJsonString, document |> tryJsonString with
        | Some pattern, Some value ->
            match tryMatchJsonSchemaPattern regexBudget pattern value with
            | Some false ->
                patternFailures.Add
                    { SchemaLocation = schemaLocation
                      DocumentLocation = documentLocation
                      Keyword = "pattern" }

                addJsonSchemaConstraint targetSchema (neverValidJsonSchema ())
            | _ -> ()
        | _ -> ()

        match document |> tryJsonObject with
        | Some documentObject ->
            let sourceProperties = sourceSchema["properties"] |> tryJsonObject
            let targetProperties = targetSchema["properties"] |> tryJsonObject

            match sourceProperties, targetProperties with
            | Some sourceProperties, Some targetProperties ->
                sourceProperties
                |> Seq.iter (fun property ->
                    match documentObject[property.Key] |> Option.ofObj, targetProperties[property.Key] |> Option.ofObj with
                    | Some value, Some targetProperty ->
                        rewriteJsonSchemaRegularExpressions
                            root
                            cleanRoot
                            property.Value
                            targetProperty
                            value
                            (pointerAppend (pointerAppend schemaLocation "properties") property.Key)
                            (pointerAppend documentLocation property.Key)
                            (remainingDepth - 1)
                            seenReferences
                            regexBudget
                            patternFailures
                            patternPropertyMatches
                    | _ -> ())
            | _ -> ()

            match sourceSchema["patternProperties"] |> tryJsonObject with
            | Some patternProperties ->
                patternProperties
                |> Seq.iter (fun patternProperty ->
                    documentObject
                    |> Seq.iter (fun property ->
                        if isJsonSchemaPropertyPatternMatch regexBudget patternProperty.Key property.Key then
                            let constraintSchema = stripJsonSchemaRegularExpressions patternProperty.Value

                            rewriteJsonSchemaRegularExpressions
                                root
                                cleanRoot
                                patternProperty.Value
                                constraintSchema
                                property.Value
                                (pointerAppend (pointerAppend schemaLocation "patternProperties") patternProperty.Key)
                                (pointerAppend documentLocation property.Key)
                                (remainingDepth - 1)
                                seenReferences
                                regexBudget
                                patternFailures
                                patternPropertyMatches

                            addPatternPropertyConstraint targetSchema property.Key constraintSchema

                            patternPropertyMatches.Add
                                { SchemaLocation = schemaLocation
                                  DocumentLocation = pointerAppend documentLocation property.Key
                                  Keyword = "patternProperties" }))
            | _ -> ()

            match sourceSchema["additionalProperties"] |> tryJsonObject, targetSchema["additionalProperties"] |> tryJsonObject with
            | Some sourceAdditionalProperties, Some targetAdditionalProperties ->
                documentObject
                |> Seq.iter (fun property ->
                    let isNamedProperty = sourceProperties |> Option.exists (fun properties -> properties.ContainsKey property.Key)

                    let matchesPatternProperty =
                        match sourceSchema["patternProperties"] |> tryJsonObject with
                        | Some patternProperties ->
                            patternProperties
                            |> Seq.exists (fun patternProperty -> isJsonSchemaPropertyPatternMatch regexBudget patternProperty.Key property.Key)
                        | None -> false

                    if not isNamedProperty && not matchesPatternProperty then
                        rewriteJsonSchemaRegularExpressions
                            root
                            cleanRoot
                            sourceAdditionalProperties
                            targetAdditionalProperties
                            property.Value
                            (pointerAppend schemaLocation "additionalProperties")
                            (pointerAppend documentLocation property.Key)
                            (remainingDepth - 1)
                            seenReferences
                            regexBudget
                            patternFailures
                            patternPropertyMatches)
            | _ -> ()
        | None -> ()

        match document with
        | :? JsonArray as documentArray ->
            match sourceSchema["items"], targetSchema["items"] with
            | (:? JsonObject as sourceItems), (:? JsonObject as targetItems) ->
                documentArray
                |> Seq.mapi (fun index value -> index, value)
                |> Seq.iter (fun (index, value) ->
                    rewriteJsonSchemaRegularExpressions
                        root
                        cleanRoot
                        sourceItems
                        targetItems
                        value
                        (pointerAppend schemaLocation "items")
                        (pointerAppend documentLocation (string index))
                        (remainingDepth - 1)
                        seenReferences
                        regexBudget
                        patternFailures
                        patternPropertyMatches)
            | (:? JsonArray as sourceItems), (:? JsonArray as targetItems) ->
                documentArray
                |> Seq.mapi (fun index value -> index, value)
                |> Seq.iter (fun (index, value) ->
                    if index < sourceItems.Count && index < targetItems.Count then
                        rewriteJsonSchemaRegularExpressions
                            root
                            cleanRoot
                            sourceItems[index]
                            targetItems[index]
                            value
                            (pointerAppend (pointerAppend schemaLocation "items") (string index))
                            (pointerAppend documentLocation (string index))
                            (remainingDepth - 1)
                            seenReferences
                            regexBudget
                            patternFailures
                            patternPropertyMatches)
            | _ -> ()
        | _ -> ()

        [ "allOf"; "anyOf"; "oneOf" ]
        |> List.iter (fun keyword ->
            match sourceSchema[keyword], targetSchema[keyword] with
            | (:? JsonArray as sourceSchemas), (:? JsonArray as targetSchemas) ->
                sourceSchemas
                |> Seq.mapi (fun index schema -> index, schema)
                |> Seq.iter (fun (index, schema) ->
                    if index < targetSchemas.Count then
                        rewriteJsonSchemaRegularExpressions
                            root
                            cleanRoot
                            schema
                            targetSchemas[index]
                            document
                            (pointerAppend (pointerAppend schemaLocation keyword) (string index))
                            documentLocation
                            (remainingDepth - 1)
                            seenReferences
                            regexBudget
                            patternFailures
                            patternPropertyMatches)
            | _ -> ())

        match sourceSchema["not"], targetSchema["not"] with
        | (:? JsonObject as sourceNot), (:? JsonObject as targetNot) ->
            rewriteJsonSchemaRegularExpressions
                root
                cleanRoot
                sourceNot
                targetNot
                document
                (pointerAppend schemaLocation "not")
                documentLocation
                (remainingDepth - 1)
                seenReferences
                regexBudget
                patternFailures
                patternPropertyMatches
        | _ -> ()
    | _ -> ()

let rec private dependencyFailure (schema: JsonNode) (document: JsonNode) schemaLocation documentLocation =
    match schema, document with
    | (:? JsonObject as schema), (:? JsonObject as document) ->
        let missingDependency =
            match schema["dependencies"] with
            | :? JsonObject as dependencies ->
                dependencies
                |> Seq.tryPick (fun dependency ->
                    if document.ContainsKey dependency.Key then
                        match dependency.Value with
                        | :? JsonArray as required ->
                            required
                            |> Seq.tryPick (fun value ->
                                match value with
                                | :? JsonValue as name ->
                                    match name.TryGetValue<string>() with
                                    | true, name when not (document.ContainsKey name) ->
                                        Some
                                            { SchemaLocation = schemaLocation
                                              DocumentLocation = documentLocation
                                              Keyword = "dependencies" }
                                    | _ -> None
                                | _ -> None)
                        | _ -> None
                    else
                        None)
            | _ -> None

        match missingDependency with
        | Some failure -> Some failure
        | None ->
            match schema["properties"] with
            | :? JsonObject as properties ->
                properties
                |> Seq.tryPick (fun property ->
                    match document[property.Key] with
                    | null -> None
                    | value ->
                        dependencyFailure
                            property.Value
                            value
                            (schemaLocation + "/properties/" + escapeJsonPointer property.Key)
                            (documentLocation + "/" + escapeJsonPointer property.Key))
            | _ -> None
    | (:? JsonObject as schema), (:? JsonArray as document) ->
        match schema["items"] with
        | :? JsonObject as items ->
            document
            |> Seq.mapi (fun index value ->
                dependencyFailure items value (schemaLocation + "/items") (documentLocation + "/" + string index))
            |> Seq.tryPick id
        | _ -> None
    | _ -> None

let private jsonSchemaValidation functionName schemaValue documentValue =
    match schemaValue, documentValue with
    | VNull, _
    | _, VNull -> None
    | _ ->
        let schemaNode = parseJsonSchemaArgument functionName 1 schemaValue
        let documentNode = parseJsonSchemaArgument functionName 2 documentValue

        match schemaNode with
        | :? JsonObject as schema ->
            normalizeJsonSchema schema

            if exceedsJsonSchemaReferenceDepth schema then
                raise (SqlError(3157, "The JSON document exceeds the maximum depth."))

            let cleanSchema = stripJsonSchemaRegularExpressions schema :?> JsonObject
            let patternFailures = ResizeArray<JsonSchemaFailure>()
            let patternPropertyMatches = ResizeArray<JsonSchemaFailure>()

            if containsJsonSchemaRegularExpressions schema then
                let regexBudget = { Started = System.Diagnostics.Stopwatch.StartNew(); Attempts = 0 }

                rewriteJsonSchemaRegularExpressions
                    schema
                    cleanSchema
                    schema
                    cleanSchema
                    documentNode
                    "#"
                    "#"
                    maxJsonSchemaDepth
                    Set.empty
                    regexBudget
                    patternFailures
                    patternPropertyMatches

            let schemaText = cleanSchema.ToJsonString jsonRenderOptions
            let documentText = documentNode |> Option.ofObj |> Option.map (fun node -> node.ToJsonString jsonRenderOptions) |> Option.defaultValue "null"
            let errors = compileJsonSchema functionName schemaText |> fun compiled -> compiled.Validate(documentText, jsonSchemaValidatorSettings)
            let libraryFailure =
                errors
                |> Seq.tryHead
                |> Option.map (fun error ->
                    let documentLocation =
                        if error.Kind = ValidationErrorKind.PropertyRequired then
                            let separator = error.Path.LastIndexOf '/'
                            if separator < 1 then "#" else error.Path.Substring(0, separator)
                        else
                            error.Path

                    { SchemaLocation = schemaLocation schema documentLocation
                      DocumentLocation = documentLocation
                      Keyword = schemaKeyword error.Kind })

            let regularExpressionFailure =
                match libraryFailure with
                | Some failure ->
                    patternPropertyMatches
                    |> Seq.tryFind (fun candidate ->
                        sameJsonDocumentLocation failure.DocumentLocation candidate.DocumentLocation
                        || failure.DocumentLocation.StartsWith(candidate.DocumentLocation + "/"))
                    |> Option.orElseWith (fun () ->
                        patternFailures
                        |> Seq.tryFind (fun candidate -> sameJsonDocumentLocation failure.DocumentLocation candidate.DocumentLocation))
                | None -> None

            Some(schema, dependencyFailure schema documentNode "#" "#" |> Option.orElse regularExpressionFailure |> Option.orElse libraryFailure)
        | _ -> raise (jsonSchemaObjectError functionName)

let private jsonSchemaValidFn: Scalar =
    function
    | [ schema; document ] ->
        match jsonSchemaValidation "json_schema_valid" schema document with
        | None -> VNull
        | Some(_, None) -> VInt 1L
        | Some(_, Some _) -> VInt 0L
    | _ -> VNull

let private jsonSchemaValidationReportFn: Scalar =
    function
    | [ schema; document ] ->
        match jsonSchemaValidation "json_schema_validation_report" schema document with
        | None -> VNull
        | Some(_, None) -> VJson "{\"valid\": true}"
        | Some(_, Some failure) ->
            let reason = sprintf "The JSON document location '%s' failed requirement '%s' at JSON Schema location '%s'" failure.DocumentLocation failure.Keyword failure.SchemaLocation

            VJson(
                "{\"valid\": false, \"reason\": "
                + jsonQuote reason
                + ", \"schema-location\": "
                + jsonQuote failure.SchemaLocation
                + ", \"document-location\": "
                + jsonQuote failure.DocumentLocation
                + ", \"schema-failed-keyword\": "
                + jsonQuote failure.Keyword
                + "}"
            )
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
/// `Replace` only writes if it does. As in MySQL, missing intermediate
/// containers are not created automatically; only the final path leg may be
/// created against an existing object or one past the end of an array.
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

let private hasPathValuePairs (values: Value list) =
    not values.IsEmpty && values.Length % 2 = 0

let private appendedJson (current: JsonNode) (value: JsonNode) : JsonNode =
    match current with
    | :? JsonArray as array ->
        let result = array.DeepClone() :?> JsonArray
        result.Add(cloneJson value)
        result
    | _ -> JsonArray([| cloneJson current; cloneJson value |])

let private appendJsonPath (root: JsonNode) (segments: JPath list) (value: JsonNode) : JsonNode =
    match segments with
    | [] -> appendedJson root value
    | _ ->
        atLeaf root segments (fun parent leaf ->
            match parent, leaf with
            | (:? JsonObject as jsonObject), JKey key when jsonObject.ContainsKey key ->
                jsonObject.[key] <- appendedJson jsonObject.[key] value
            | (:? JsonArray as array), JIndex index ->
                match normIndex array index with
                | Some i -> array.[i] <- appendedJson array.[i] value
                | None -> ()
            | _ -> ())

        root

let private jsonArrayAppendFn: Scalar =
    function
    | document :: rest when hasPathValuePairs rest && not (anyNull (document :: rest)) ->
        match tryParseJsonValue document with
        | Some rootNode ->
            let mutable root = rootNode

            for path, value in pairsOf rest do
                match toText path |> Option.bind parseJsonPath with
                | Some segments -> root <- appendJsonPath root segments (valueToJsonNode value)
                | None -> ()

            VJson(formatJsonNode root)
        | None -> VNull
    | _ -> VNull

let private jsonArrayInsertFn: Scalar =
    function
    | document :: rest when hasPathValuePairs rest && not (anyNull (document :: rest)) ->
        match tryParseJsonValue document with
        | Some root ->
            for path, value in pairsOf rest do
                match toText path |> Option.bind parseJsonPath with
                | Some segments ->
                    atLeaf root segments (fun parent leaf ->
                        match parent, leaf with
                        | (:? JsonArray as array), JIndex index ->
                            let position = if index < 0 then array.Count + index else index

                            if position >= 0 then
                                array.Insert(min position array.Count, valueToJsonNode value)
                        | _ -> ())
                | None -> ()

            VJson(formatJsonNode root)
        | None -> VNull
    | _ -> VNull

let private variableLengthSize (value: int) =
    let mutable remaining = value
    let mutable bytes = 1

    while remaining >= 128 do
        remaining <- remaining >>> 7
        bytes <- bytes + 1

    bytes

let rec private jsonBinaryPayloadSize (inlineWidth: int) (node: JsonNode) : int64 =
    if isNull node then
        if inlineWidth >= 1 then 0L else 1L
    else
        match node.GetValueKind() with
        | JsonValueKind.True
        | JsonValueKind.False -> if inlineWidth >= 1 then 0L else 1L
        | JsonValueKind.String ->
            let length = Text.Encoding.UTF8.GetByteCount(node.GetValue<string>())
            int64 (variableLengthSize length + length)
        | JsonValueKind.Number ->
            let text = node.ToJsonString(jsonRenderOptions)

            match Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, number when number >= int64 Int16.MinValue && number <= int64 Int16.MaxValue ->
                if inlineWidth >= 2 then 0L else 2L
            | true, number when inlineWidth >= 4 && number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue -> 0L
            | true, number when number >= int64 Int32.MinValue && number <= int64 Int32.MaxValue -> 4L
            | true, _ -> 8L
            | _ -> 8L
        | JsonValueKind.Array -> jsonArrayBinarySize (node :?> JsonArray)
        | JsonValueKind.Object -> jsonObjectBinarySize (node :?> JsonObject)
        | _ -> if inlineWidth >= 1 then 0L else 1L

and private jsonArrayBinarySize (array: JsonArray) : int64 =
    let size header entry inlineWidth =
        int64 header
        + int64 (array.Count * entry)
        + (array |> Seq.sumBy (jsonBinaryPayloadSize inlineWidth))

    let small = size 4 3 2
    if array.Count <= 65535 && small <= 65535L then small else size 8 5 4

and private jsonObjectBinarySize (jsonObject: JsonObject) : int64 =
    let keyBytes = jsonObject |> Seq.sumBy (fun pair -> Text.Encoding.UTF8.GetByteCount pair.Key) |> int64

    let size header keyEntry valueEntry inlineWidth =
        int64 header
        + int64 (jsonObject.Count * (keyEntry + valueEntry))
        + keyBytes
        + (jsonObject |> Seq.sumBy (fun pair -> jsonBinaryPayloadSize inlineWidth pair.Value))

    let small = size 4 4 3 2
    if jsonObject.Count <= 65535 && small <= 65535L then small else size 8 6 5 4

let private jsonStorageSizeFn: Scalar =
    function
    | [ document ] when not (anyNull [ document ]) ->
        tryParseJsonValue document
        |> Option.map (fun node -> VInt(1L + jsonBinaryPayloadSize 0 node))
        |> Option.defaultValue VNull
    | _ -> VNull

let private jsonStorageFreeFn: Scalar =
    function
    | [ document ] when not (anyNull [ document ]) ->
        tryParseJsonValue document |> Option.map (fun _ -> VInt 0L) |> Option.defaultValue VNull
    | _ -> VNull

let private jsonWriteFn (mode: JsonWriteMode) : Scalar =
    function
    | doc :: rest when hasPathValuePairs rest && not (anyNull (doc :: rest)) ->
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

let private jsonSearchFn: Scalar =
    function
    | doc :: modeV :: searchV :: optional when not (anyNull [ doc; modeV; searchV ]) ->
        match tryParseJsonValue doc, toText searchV with
        | Some root, Some search ->
            let escape =
                optional
                |> List.tryHead
                |> Option.bind toText
                |> Option.bind (fun text -> if text.Length <= 1 then Some(if text = "" then '\u0000' else text.[0]) else None)
                |> Option.defaultValue '\\'

            let paths =
                match optional with
                | _escape :: (_ :: _ as pathValues) ->
                    pathValues
                    |> List.map (fun value ->
                        toText value
                        |> Option.bind (fun path -> parseJsonPath path |> Option.map (fun segments -> path, segments)))
                | _ -> [ Some("$", []) ]

            let options = RegexOptions.IgnoreCase ||| RegexOptions.Singleline ||| RegexOptions.NonBacktracking
            let rx = Regex(likeToRegexWith escape search, options, Limits.regexpMatchTimeout)

            let rec pathPrefixMatches expected actual =
                match expected, actual with
                | [], _ -> true
                | JKey left :: expectedRest, JKey right :: actualRest when left = right -> pathPrefixMatches expectedRest actualRest
                | JIndex left :: expectedRest, JIndex right :: actualRest when left = right -> pathPrefixMatches expectedRest actualRest
                | JMemberWildcard :: expectedRest, JKey _ :: actualRest -> pathPrefixMatches expectedRest actualRest
                | JElementWildcard :: expectedRest, JIndex _ :: actualRest -> pathPrefixMatches expectedRest actualRest
                | _ -> false

            let matches =
                if paths |> List.exists Option.isNone then
                    []
                else
                    let restrictions = paths |> List.choose id |> List.map snd

                    collectJsonStrings root "$"
                    |> List.filter (fun (path, _) ->
                        parseJsonPath path
                        |> Option.exists (fun actual -> restrictions |> List.exists (fun expected -> pathPrefixMatches expected actual)))
                    |> List.filter (snd >> rx.IsMatch)
                    |> List.map fst
                    |> List.distinct

            match matches, (toText modeV |> Option.defaultValue "one").ToUpperInvariant() with
            | [], _ -> VNull
            | [ path ], "ALL" -> VJson(jsonQuote path)
            | paths, "ALL" -> VJson("[" + (paths |> List.map jsonQuote |> String.concat ", ") + "]")
            | p :: _, _ -> VJson(jsonQuote p)
        | _ -> VNull
    | _ -> VNull

/// Raw binary operands bypass collation weighting and retain their bytes.
let weightString (collation: Collation.Collation) (value: Value) : Value =
    match value with
    | VNull -> VNull
    | value ->
        match tryRawBytes value with
        | Some bytes -> VBytes bytes
        | None -> collation.WeightOf(req value) |> VBytes

let weightStringChar (collation: Collation.Collation) (length: int) (value: Value) : Value =
    match value, tryRawBytes value with
    | VNull, _ -> VNull
    | _, Some bytes -> VBytes(Array.truncate length bytes)
    | _ ->
        let text = req value
        let result = StringBuilder()
        let mutable count = 0

        text.EnumerateRunes()
        |> Seq.truncate length
        |> Seq.iter (fun rune ->
            result.Append(rune.ToString()) |> ignore
            count <- count + 1)

        while count < length do
            result.Append ' ' |> ignore
            count <- count + 1

        weightString collation (VString(result.ToString()))

let weightStringBinaryWith (encodeText: string -> byte[]) (length: int) (value: Value) : Value =
    if length > Limits.maxAllowedPacket then
        raise (SqlError(1153, "Result of WEIGHT_STRING() exceeds max_allowed_packet"))

    let bytes =
        match value, tryRawBytes value with
        | VNull, _ -> None
        | _, Some bytes -> Some bytes
        | _ -> Some(encodeText (req value))

    match bytes with
    | None -> VNull
    | Some bytes ->
        let result = Array.zeroCreate length
        Array.Copy(bytes, result, min bytes.Length length)
        VBytes result

let weightStringBinary (length: int) (value: Value) : Value =
    weightStringBinaryWith Text.Encoding.UTF8.GetBytes length value

let private weightStringFn: Scalar =
    function
    | [ value ] -> weightString Collation.defaultCollation value
    | _ -> VNull

let private convertFn: Scalar =
    function
    | [ VNull; _ ] -> VNull
    | [ v; VString charset ] ->
        let text = v |> toText |> Option.defaultValue ""

        match Charset.canonicalName charset with
        | "binary" -> VBytes(Text.Encoding.UTF8.GetBytes text)
        | charset when Charset.tryFind charset |> Option.isSome -> VString(Charset.transcodeText charset text)
        | _ -> VNull
    | _ -> VNull

// Date and time functions.

let private dateTimeFormats =
    [| "yyyy-MM-dd HH:mm:ss"
       "yyyy-MM-dd"
       "yyyyMMdd"
       "yyMMdd"
       "yyyy-MM-ddTHH:mm:ss"
       "yyyy/MM/dd" |]

/// Parses any `Value` as a `DateTime` the way MySQL's implicit date cast
/// does: real date/datetime values pass through, everything else parses its
/// text (first against MySQL's own common formats, then .NET's general
/// parser as a fallback) — `None` rather than an error for anything that
/// doesn't look like a date.
let tryDateTimeValue (v: Value) : DateTime option =
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

let private asDateOnly (v: Value) : DateOnly option = tryDateTimeValue v |> Option.map DateOnly.FromDateTime

let private dateOnlyUnits = set [ "DAY"; "WEEK"; "MONTH"; "QUARTER"; "YEAR" ]

/// Adds an interval, returning `None` when the result falls outside
/// `DateTime`'s range — MySQL yields NULL for an out-of-range temporal
/// rather than erroring. `int amount` for the month/year units can itself
/// overflow, so those are bounds-checked before the conversion.
let tryAddInterval (dt: DateTime) (amount: float) (unit: string) : DateTime option =
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
        // Composite units are normalized into this vocabulary before use.
        | _ -> None
    with :? ArgumentOutOfRangeException ->
        None

/// MySQL's composite `INTERVAL` units take a *string* of several components
/// with any punctuation between them ('2-3' YEAR_MONTH, '1:30' HOUR_MINUTE,
/// '1 2:3:4' DAY_SECOND), and a value with fewer components than the unit
/// names is read as its *rightmost* ones. Each entry is the per-component
/// multiplier list plus the simple unit the total is expressed in, so
/// `tryAddInterval` never has to know these exist.
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
let tryIntervalParts (amount: string) (unit: string) : (float * string) option =
    match compositeIntervalUnits.TryGetValue(unit.ToUpperInvariant()) with
    | true, (weights, simpleUnit) ->
        parseCompositeInterval weights simpleUnit amount
        |> Option.map (fun total -> total, simpleUnit)
    | _ ->
        match Double.TryParse(amount, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value -> Some(value, unit)
        | false, _ -> None

let tryIntervalArgument (v: Value) : (float * string) option =
    match v with
    | VString s when s.StartsWith intervalMarker ->
        match s.Substring(intervalMarker.Length).Split('\x01') with
        | [| n; u |] ->
            tryIntervalParts n u
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
    match tryAddInterval dt (sign * amount) unit with
    | None -> VNull // out of range — MySQL yields NULL
    | Some result ->
        if looksDateOnly dateV && dateOnlyUnits.Contains(unit.ToUpperInvariant()) then
            VDate(DateOnly.FromDateTime result)
        else
            VDateTime result

let private dateAddCore (sign: float) : Scalar =
    function
    | [ dateV; intervalV ] when not (anyNull [ dateV; intervalV ]) ->
        match tryDateTimeValue dateV, tryIntervalArgument intervalV with
        | Some dt, Some(n, unit) -> applyDateInterval sign dateV dt n unit
        | _ -> VNull
    | [ dateV; amtV; VString unit ] when not (anyNull [ dateV; amtV ]) ->
        match tryDateTimeValue dateV with
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
        match tryDateTimeValue dateV with
        | None -> VNull
        | Some dt ->
            match tryIntervalArgument amtV with
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
    match tryIntervalArgument intervalV, tryDateTimeValue dateV with
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

let private calcDaysInYear (year: uint32) =
    if year &&& 3u = 0u && (year % 100u <> 0u || (year % 400u = 0u && year <> 0u)) then 366u else 365u

let private calcDayNumber (year: uint32) (month: uint32) (day: uint32) : int64 =
    if year = 0u && month = 0u then
        0L
    else
        let mutable y = int64 year
        let mutable days = 365L * y + 31L * (int64 month - 1L) + int64 day

        if month <= 2u then
            y <- y - 1L
        else
            days <- days - (int64 month * 4L + 23L) / 10L

        let centuryCorrection = ((y / 100L + 1L) * 3L) / 4L
        days + y / 4L - centuryCorrection

let private calcWeekday dayNumber sundayFirst =
    int ((dayNumber + 5L + if sundayFirst then 1L else 0L) % 7L)

let private wrapUInt32 (value: int64) = uint32 (value &&& 0xffffffffL)

let private calcWeek (behaviour: int) (yearValue: int) (month: int) (day: int) : uint32 * uint32 =
    let mondayFirst = behaviour &&& 1 <> 0
    let mutable weekYear = behaviour &&& 2 <> 0
    let firstWeekday = behaviour &&& 4 <> 0
    let dayNumber = calcDayNumber (uint32 yearValue) (uint32 month) (uint32 day)
    let mutable firstDayNumber = calcDayNumber (uint32 yearValue) 1u 1u
    let mutable weekday = calcWeekday firstDayNumber (not mondayFirst)
    let mutable year = uint32 yearValue

    let earlyJanuary = month = 1 && day <= 7 - weekday

    if earlyJanuary && not weekYear && ((firstWeekday && weekday <> 0) || (not firstWeekday && weekday >= 4)) then
        0u, year
    else
        if earlyJanuary then
            weekYear <- true
            year <- year - 1u
            let daysInYear = calcDaysInYear year
            firstDayNumber <- firstDayNumber - int64 daysInYear
            weekday <- (weekday + 53 * 7 - int daysInYear) % 7

        let start =
            if (firstWeekday && weekday <> 0) || (not firstWeekday && weekday >= 4) then
                firstDayNumber + int64 (7 - weekday)
            else
                firstDayNumber - int64 weekday

        let days = wrapUInt32 (dayNumber - start)

        if weekYear && days >= 52u * 7u then
            weekday <- (weekday + int (calcDaysInYear year)) % 7

            if (not firstWeekday && weekday < 4) || (firstWeekday && weekday = 0) then
                1u, year + 1u
            else
                days / 7u + 1u, year
        else
            days / 7u + 1u, year

let private weekBehaviour mode =
    let value = mode &&& 7
    if value &&& 1 = 0 then value ^^^ 4 else value

let private weekOf (mode: int) (date: DateOnly) : int * int =
    let week, year = calcWeek (weekBehaviour mode) date.Year date.Month date.Day
    int week, int year

let private yearWeekOf (mode: int) (date: DateOnly) : int =
    let week, year = calcWeek (weekBehaviour mode ||| 2) date.Year date.Month date.Day
    int year * 100 + int week

type private DateFormatParts =
    { Year: int
      Month: int
      Day: int
      Hour: int
      Minute: int
      Second: int
      Microsecond: int }

let private partsOfDateTime (value: DateTime) =
    { Year = value.Year
      Month = value.Month
      Day = value.Day
      Hour = value.Hour
      Minute = value.Minute
      Second = value.Second
      Microsecond = int (value.Ticks % TimeSpan.TicksPerSecond / 10L) }

let private dateFormatParts =
    function
    | VZeroDate date ->
        let year, month, day = zeroDateParts date

        Some
            { Year = year
              Month = month
              Day = day
              Hour = 0
              Minute = 0
              Second = 0
              Microsecond = 0 }
    | VZeroDateTime dateTime ->
        let date, hour, minute, second, microsecond = zeroDateTimeParts dateTime
        let year, month, day = zeroDateParts date

        Some
            { Year = year
              Month = month
              Day = day
              Hour = hour
              Minute = minute
              Second = second
              Microsecond = microsecond }
    | VTime value ->
        try
            Some(partsOfDateTime (DateTime.Today.AddTicks(timeTicks value)))
        with :? ArgumentOutOfRangeException ->
            None
    | value -> tryDateTimeValue value |> Option.map partsOfDateTime

let private zeroPadded width (value: int64) =
    if value < 0L then
        "-" + (-value).ToString().PadLeft(max 0 (width - 1), '0')
    else
        value.ToString().PadLeft(width, '0')

let private ordinal day =
    let suffix =
        if day >= 10 && day <= 19 then
            "th"
        else
            match day % 10 with
            | 1 -> "st"
            | 2 -> "nd"
            | 3 -> "rd"
            | _ -> "th"

    string day + suffix

/// Shared `DATE_FORMAT` and `FROM_UNIXTIME` rendering.
let private formatDate (locale: TemporalLocale.Names) (parts: DateFormatParts) (fmt: string) : string option =
    let sb = StringBuilder()
    let mutable i = 0
    let mutable valid = true
    let hour12 = (parts.Hour % 24 + 11) % 12 + 1
    let week behaviour = calcWeek behaviour parts.Year parts.Month parts.Day
    let dayNumber = calcDayNumber (uint32 parts.Year) (uint32 parts.Month) (uint32 parts.Day)
    let weekday = calcWeekday dayNumber true

    let monthName abbreviated =
        if parts.Month = 0 then
            valid <- false
            ""
        elif abbreviated then
            locale.AbbreviatedMonths.[parts.Month - 1]
        else
            locale.Months.[parts.Month - 1]

    let weekdayName abbreviated =
        if parts.Year = 0 && parts.Month = 0 then
            valid <- false
            ""
        else
            let index = (weekday + 6) % 7
            if abbreviated then locale.AbbreviatedDays.[index] else locale.Days.[index]

    while i < fmt.Length do
        if fmt.[i] = '%' && i + 1 < fmt.Length then
            let piece =
                match fmt.[i + 1] with
                | 'Y' -> zeroPadded 4 parts.Year
                | 'y' -> zeroPadded 2 (int64 (parts.Year % 100))
                | 'm' -> zeroPadded 2 parts.Month
                | 'c' -> string parts.Month
                | 'd' -> zeroPadded 2 parts.Day
                | 'e' -> string parts.Day
                | 'f' -> zeroPadded 6 parts.Microsecond
                | 'H' -> zeroPadded 2 parts.Hour
                | 'h'
                | 'I' -> zeroPadded 2 hour12
                | 'i' -> zeroPadded 2 parts.Minute
                | 'j' -> zeroPadded 3 (dayNumber - calcDayNumber (uint32 parts.Year) 1u 1u + 1L)
                | 'k' -> string parts.Hour
                | 'l' -> string hour12
                | 'M' -> monthName false
                | 'b' -> monthName true
                | 'p' -> if parts.Hour % 24 < 12 then "AM" else "PM"
                | 'r' -> sprintf "%02d:%02d:%02d %s" hour12 parts.Minute parts.Second (if parts.Hour % 24 < 12 then "AM" else "PM")
                | 's'
                | 'S' -> zeroPadded 2 parts.Second
                | 'T' -> sprintf "%02d:%02d:%02d" parts.Hour parts.Minute parts.Second
                | 'U' -> week 4 |> fst |> int64 |> zeroPadded 2
                | 'u' -> week 1 |> fst |> int64 |> zeroPadded 2
                | 'V' -> week 6 |> fst |> int64 |> zeroPadded 2
                | 'v' -> week 3 |> fst |> int64 |> zeroPadded 2
                | 'W' -> weekdayName false
                | 'a' -> weekdayName true
                | 'w' -> string weekday
                | 'X' -> week 6 |> snd |> int64 |> zeroPadded 4
                | 'x' -> week 3 |> snd |> int64 |> zeroPadded 4
                | 'D' -> ordinal parts.Day
                | '%' -> "%"
                | other -> string other

            sb.Append(piece: string) |> ignore
            i <- i + 2
        else
            sb.Append fmt.[i] |> ignore
            i <- i + 1

    if fmt <> "" && valid then Some(sb.ToString()) else None

let internal tryTimeLocale (locale: string) =
    TemporalLocale.tryFind locale

let internal defaultTimeLocale = TemporalLocale.tryFind "en_US" |> Option.get

let internal dateFormatFn (locale: TemporalLocale.Names) : Scalar =
    function
    | [ d; f ] when not (anyNull [ d; f ]) ->
        match dateFormatParts d, toText f with
        | Some parts, Some fmt ->
            formatDate locale parts fmt
            |> Option.map VString
            |> Option.defaultValue VNull
        | _ -> VNull
    | [ _; _ ] -> VNull
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'DATE_FORMAT'"))

let private dateFn: Scalar =
    function
    | [ VZeroDate date ] -> VZeroDate date
    | [ VZeroDateTime dateTime ] -> VZeroDate(zeroDateOfDateTime dateTime)
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map VDate |> Option.defaultValue VNull
    | _ -> VNull

let private timeFn: Scalar =
    function
    | [ VTime value ] -> VTime value
    | [ VZeroDateTime value ] ->
        let _, hour, minute, second, micros = zeroDateTimeParts value
        VTime(timeValueOrClamp ((int64 hour * 3600L + int64 minute * 60L + int64 second) * TimeSpan.TicksPerSecond + int64 micros * 10L))
    | [ v ] when not (anyNull [ v ]) ->
        match toText v |> Option.bind tryParseTimeInputTicks with
        | Some ticks -> VTime(timeValueOrClamp (roundTimeTicksToFsp 6 ticks))
        | None -> tryDateTimeValue v |> Option.map (fun value -> VTime(timeValueOrClamp value.TimeOfDay.Ticks)) |> Option.defaultValue VNull
    | _ -> VNull

let private datePartFn (f: DateTime -> int) : Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> tryDateTimeValue v |> Option.map (f >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private zeroAwareDatePart (fromZero: ZeroDate -> int) (fromDateTime: DateTime -> int) : Scalar =
    function
    | [ VZeroDate date ] -> VInt(int64 (fromZero date))
    | [ VZeroDateTime dateTime ] -> VInt(int64 (fromZero (zeroDateOfDateTime dateTime)))
    | [ value ] when not (anyNull [ value ]) -> tryDateTimeValue value |> Option.map (fromDateTime >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private timeParts (value: TimeValue) =
    let ticks = abs (timeTicks value)
    let totalSeconds = ticks / TimeSpan.TicksPerSecond
    let hour = int (totalSeconds / 3600L)
    let minute = int (totalSeconds % 3600L / 60L)
    let second = int (totalSeconds % 60L)
    let microseconds = int (ticks % TimeSpan.TicksPerSecond / 10L)
    (hour, minute, second, microseconds)

let private timeHour value =
    let hour, _, _, _ = timeParts value
    hour

let private timeMinute value =
    let _, minute, _, _ = timeParts value
    minute

let private timeSecond value =
    let _, _, second, _ = timeParts value
    second

let private timeMicroseconds value =
    let _, _, _, microseconds = timeParts value
    microseconds

let private zeroAwareTimePart (fromZero: ZeroDateTime -> int) (fromTime: TimeValue -> int) (fromDateTime: DateTime -> int) : Scalar =
    function
    | [ VZeroDate _ ] -> VInt 0L
    | [ VZeroDateTime dateTime ] -> VInt(int64 (fromZero dateTime))
    | [ VTime value ] -> VInt(int64 (fromTime value))
    | [ value ] when not (anyNull [ value ]) -> tryDateTimeValue value |> Option.map (fromDateTime >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let internal dayNameFn (locale: TemporalLocale.Names) : Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        tryDateTimeValue v
        |> Option.map (fun date -> VString(locale.Days.[(int date.DayOfWeek + 6) % 7]))
        |> Option.defaultValue VNull
    | _ -> VNull

let internal monthNameFn (locale: TemporalLocale.Names) : Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        tryDateTimeValue v
        |> Option.map (fun date -> VString(locale.Months.[date.Month - 1]))
        |> Option.defaultValue VNull
    | _ -> VNull

let internal weekFn defaultMode: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateOnly v |> Option.map (weekOf defaultMode >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | [ v; m ] when not (anyNull [ v; m ]) ->
        asDateOnly v |> Option.map (weekOf (int (toDouble m)) >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private weekdayFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> tryDateTimeValue v |> Option.map (fun d -> VInt(int64 ((int d.DayOfWeek + 6) % 7))) |> Option.defaultValue VNull
    | _ -> VNull

let private weekOfYearFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map (weekOf 3 >> fst >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private yearWeekFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> asDateOnly v |> Option.map (yearWeekOf 0 >> int64 >> VInt) |> Option.defaultValue VNull
    | [ v; m ] when not (anyNull [ v; m ]) ->
        asDateOnly v |> Option.map (yearWeekOf (int (toDouble m)) >> int64 >> VInt) |> Option.defaultValue VNull
    | _ -> VNull

let private curDateFn: Scalar = fun _ -> VDate(DateOnly.FromDateTime DateTime.Now)

let private currentTimeFn (clock: unit -> DateTime) : Scalar =
    function
    | [ precision ] when not (anyNull [ precision ]) ->
        let fsp = toDouble precision |> int |> max 0 |> min 6
        VTime(timeValueOrClamp (roundTimeTicksToFsp fsp ((clock ()).TimeOfDay.Ticks)))
    | _ -> VTime(timeValueOrClamp (truncateToSecond (clock ())).TimeOfDay.Ticks)

let private curTimeFn = currentTimeFn (fun () -> DateTime.Now)
let private utcDateFn: Scalar = fun _ -> VDate(DateOnly.FromDateTime DateTime.UtcNow)
let private utcTimeFn = currentTimeFn (fun () -> DateTime.UtcNow)
let private utcTimestampFn: Scalar = fun _ -> VDateTime(truncateToSecond DateTime.UtcNow)

let private tryTimeTicks (value: Value) =
    match value with
    | VTime time -> Some(timeTicks time)
    | _ -> value |> toText |> Option.bind tryParseTimeInputTicks |> Option.map (roundTimeTicksToFsp 6)

let private timeResult ticks = VTime(timeValueOrClamp ticks)

/// `TIMESTAMP(expr)` coerces to a datetime; the two-argument form adds a
/// TIME value to that datetime.
let private timestampFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> tryDateTimeValue v |> Option.map VDateTime |> Option.defaultValue VNull
    | [ date; time ] when not (anyNull [ date; time ]) ->
        match tryDateTimeValue date, tryTimeTicks time with
        | Some value, Some ticks ->
            try
                VDateTime(value.AddTicks ticks)
            with _ ->
                VNull
        | _ -> VNull
    | _ -> VNull

let private addTimeFn (direction: int64) : Scalar =
    function
    | [ value; interval ] when not (anyNull [ value; interval ]) ->
        match tryTimeTicks interval with
        | None -> VNull
        | Some intervalTicks ->
            match tryTimeTicks value with
            | Some valueTicks -> timeResult (valueTicks + direction * intervalTicks)
            | None ->
                match tryDateTimeValue value with
                | Some dateTime ->
                    try
                        VDateTime(dateTime.AddTicks(direction * intervalTicks))
                    with :? ArgumentOutOfRangeException ->
                        VNull
                | None -> VNull
    | _ -> VNull

let private timeDiffFn: Scalar =
    function
    | [ left; right ] when not (anyNull [ left; right ]) ->
        match tryTimeTicks left, tryTimeTicks right with
        | Some leftTicks, Some rightTicks -> timeResult (leftTicks - rightTicks)
        | None, None ->
            match tryDateTimeValue left, tryDateTimeValue right with
            | Some leftDate, Some rightDate -> timeResult ((leftDate - rightDate).Ticks)
            | _ -> VNull
        | _ -> VNull
    | _ -> VNull

let private secToTimeFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let seconds = toDouble value
        let maximumSeconds = float maxTimeTicks / float TimeSpan.TicksPerSecond

        let ticks =
            if seconds >= maximumSeconds then
                if seconds > maximumSeconds then
                    Diagnostics.warning 1292 "Truncated incorrect time value"

                maxTimeTicks
            elif seconds <= -maximumSeconds then
                if seconds < -maximumSeconds then
                    Diagnostics.warning 1292 "Truncated incorrect time value"

                -maxTimeTicks
            else
                decimal seconds * decimal TimeSpan.TicksPerSecond |> Decimal.Round |> int64

        timeResult ticks
    | _ -> VNull

let private makeTimeFn: Scalar =
    function
    | [ hours; minutes; seconds ] when not (anyNull [ hours; minutes; seconds ]) ->
        let hours =
            match hours with
            | VInt value -> value
            | VUInt value when value > uint64 Int64.MaxValue -> Int64.MaxValue
            | VUInt value -> int64 value
            | value ->
                let number = toDouble value

                if number >= float Int64.MaxValue then
                    Int64.MaxValue
                elif number <= float Int64.MinValue then
                    Int64.MinValue
                else
                    int64 number

        let minutes = int64 (toDouble minutes)
        let seconds = toDouble seconds

        if minutes < 0L || minutes > 59L || seconds < 0.0 || seconds >= 60.0 then
            VNull
        elif hours < -838L || hours > 838L then
            Diagnostics.warning 1292 "Truncated incorrect time value"
            timeResult (if hours < 0L then -maxTimeTicks else maxTimeTicks)
        else
            let sign = if hours < 0L then -1L else 1L
            let magnitude = if hours < 0L then -hours else hours
            let ticks = (magnitude * 3600L + minutes * 60L) * TimeSpan.TicksPerSecond + int64 (seconds * float TimeSpan.TicksPerSecond)
            timeResult (sign * ticks)
    | _ -> VNull

let private timeFormatFn: Scalar =
    function
    | [ value; format ] when not (anyNull [ value; format ]) ->
        match tryTimeTicks value with
        | None -> VNull
        | Some ticks ->
            let negative = ticks < 0L
            let magnitude = abs ticks
            let totalSeconds = magnitude / TimeSpan.TicksPerSecond
            let hours = totalSeconds / 3600L
            let minutes = totalSeconds % 3600L / 60L
            let seconds = totalSeconds % 60L
            let micros = magnitude % TimeSpan.TicksPerSecond / 10L
            let hour12 = let hour = hours % 24L % 12L in if hour = 0L then 12L else hour
            let mutable index = 0
            let result = StringBuilder()
            let format = req format

            while index < format.Length do
                if format.[index] = '%' && index + 1 < format.Length then
                    let piece =
                        match format.[index + 1] with
                        | 'H' -> hours.ToString("D2")
                        | 'k' -> string hours
                        | 'h' | 'I' -> hour12.ToString("D2")
                        | 'l' -> string hour12
                        | 'i' -> minutes.ToString("D2")
                        | 's' | 'S' -> seconds.ToString("D2")
                        | 'f' -> micros.ToString("D6")
                        | 'p' -> if hours % 24L < 12L then "AM" else "PM"
                        | '%' -> "%"
                        | other -> string other

                    result.Append piece |> ignore
                    index <- index + 2
                else
                    result.Append format.[index] |> ignore
                    index <- index + 1

            if negative then VString("-" + result.ToString()) else VString(result.ToString())
    | _ -> VNull

let private getFormatFn: Scalar =
    let formats =
        [ ("DATE", "USA"), "%m.%d.%Y"
          ("DATE", "JIS"), "%Y-%m-%d"
          ("DATE", "ISO"), "%Y-%m-%d"
          ("DATE", "EUR"), "%d.%m.%Y"
          ("DATE", "INTERNAL"), "%Y%m%d"
          ("DATETIME", "USA"), "%Y-%m-%d %H.%i.%s"
          ("DATETIME", "JIS"), "%Y-%m-%d %H:%i:%s"
          ("DATETIME", "ISO"), "%Y-%m-%d %H:%i:%s"
          ("DATETIME", "EUR"), "%Y-%m-%d %H.%i.%s"
          ("DATETIME", "INTERNAL"), "%Y%m%d%H%i%s"
          ("TIME", "USA"), "%h:%i:%s %p"
          ("TIME", "JIS"), "%H:%i:%s"
          ("TIME", "ISO"), "%H:%i:%s"
          ("TIME", "EUR"), "%H.%i.%s"
          ("TIME", "INTERNAL"), "%H%i%s" ]
        |> Map.ofList

    function
    | [ kind; locale ] when not (anyNull [ kind; locale ]) ->
        Map.tryFind ((req kind).ToUpperInvariant(), (req locale).ToUpperInvariant()) formats
        |> Option.map VString
        |> Option.defaultValue VNull
    | _ -> VNull

let private periodYearMonth (value: Value) =
    let period = int64 (toDouble value)
    let month = int (period % 100L)
    let shortYear = int (period / 100L)

    if month < 1 || month > 12 then
        None
    else
        let year = if shortYear < 70 then shortYear + 2000 elif shortYear < 100 then shortYear + 1900 else shortYear
        Some(year, month)

let private periodAddFn: Scalar =
    function
    | [ period; months ] when not (anyNull [ period; months ]) ->
        match periodYearMonth period with
        | None -> VNull
        | Some(year, month) ->
            let total = int64 year * 12L + int64 (month - 1) + int64 (toDouble months)
            VInt(total / 12L * 100L + total % 12L + 1L)
    | _ -> VNull

let private periodDiffFn: Scalar =
    function
    | [ left; right ] when not (anyNull [ left; right ]) ->
        match periodYearMonth left, periodYearMonth right with
        | Some(leftYear, leftMonth), Some(rightYear, rightMonth) ->
            VInt(int64 ((leftYear - rightYear) * 12 + leftMonth - rightMonth))
        | _ -> VNull
    | _ -> VNull

let private toDaysFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> asDateOnly value |> Option.map (fun date -> VInt(int64 date.DayNumber + 366L)) |> Option.defaultValue VNull
    | _ -> VNull

let private fromDaysFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let dayNumber = int64 (toDouble value) - 366L

        if dayNumber < 0L || dayNumber > int64 DateOnly.MaxValue.DayNumber then
            VNull
        else
            VDate(DateOnly.FromDayNumber(int dayNumber))
    | _ -> VNull

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
    | [ v ] when not (anyNull [ v ]) -> tryDateTimeValue v |> Option.map (fun dt -> VInt(int64 (dt - unixEpoch).TotalSeconds)) |> Option.defaultValue VNull
    | _ -> VNull

let private fromUnixSeconds (ts: Value) : DateTime option =
    let secs = toDouble ts
    if Double.IsNaN secs || abs secs > 3.2e11 then
        None // MySQL's FROM_UNIXTIME range tops out near year 3001; NULL past it
    else
        try Some(unixEpoch.AddSeconds secs) with :? ArgumentOutOfRangeException -> None

let internal fromUnixTimeFn (locale: TemporalLocale.Names) : Scalar =
    function
    | [ ts ] when not (anyNull [ ts ]) -> fromUnixSeconds ts |> Option.map VDateTime |> Option.defaultValue VNull
    | [ ts; f ] when not (anyNull [ ts; f ]) ->
        match toText f, fromUnixSeconds ts with
        | Some fmt, Some dt ->
            formatDate locale (partsOfDateTime dt) fmt
            |> Option.map VString
            |> Option.defaultValue VNull
        | _ -> VNull
    | _ -> VNull

let private timestampDiffFn: Scalar =
    function
    | [ u; a; b ] when not (anyNull [ u; a; b ]) ->
        match toText u, tryDateTimeValue a, tryDateTimeValue b with
        | Some unit, Some da, Some db ->
            let span = db - da

            let ordered () = if db < da then db, da, -1.0 else da, db, 1.0

            let wholeMonths (earlier: DateTime) (later: DateTime) =
                let months = (later.Year - earlier.Year) * 12 + later.Month - earlier.Month
                months - if (later.Day, later.TimeOfDay) < (earlier.Day, earlier.TimeOfDay) then 1 else 0

            let wholeYears (earlier: DateTime) (later: DateTime) =
                let years = later.Year - earlier.Year
                years - if (later.Month, later.Day, later.TimeOfDay) < (earlier.Month, earlier.Day, earlier.TimeOfDay) then 1 else 0

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
                    let earlier, later, sign = ordered ()
                    sign * float (wholeMonths earlier later)
                | "QUARTER" ->
                    let earlier, later, sign = ordered ()
                    sign * Math.Truncate(float (wholeMonths earlier later) / 3.0)
                | "YEAR" ->
                    let earlier, later, sign = ordered ()
                    sign * float (wholeYears earlier later)
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
        let compose (parts: (int * int) list) =
            parts |> List.fold (fun acc (value, width) -> acc * pown 10L width + int64 value) 0L

        let extract unit year month day hour minute second microseconds =
            match unit with
            | "MICROSECOND" -> VInt(int64 microseconds)
            | "SECOND" -> VInt(int64 second)
            | "MINUTE" -> VInt(int64 minute)
            | "HOUR" -> VInt(int64 hour)
            | "DAY" -> VInt(int64 day)
            | "MONTH" -> VInt(int64 month)
            | "QUARTER" -> VInt(int64 ((month - 1) / 3 + 1))
            | "YEAR" -> VInt(int64 year)
            | "SECOND_MICROSECOND" -> VInt(compose [ second, 0; microseconds, 6 ])
            | "MINUTE_MICROSECOND" -> VInt(compose [ minute, 0; second, 2; microseconds, 6 ])
            | "MINUTE_SECOND" -> VInt(compose [ minute, 0; second, 2 ])
            | "HOUR_MICROSECOND" -> VInt(compose [ hour, 0; minute, 2; second, 2; microseconds, 6 ])
            | "HOUR_SECOND" -> VInt(compose [ hour, 0; minute, 2; second, 2 ])
            | "HOUR_MINUTE" -> VInt(compose [ hour, 0; minute, 2 ])
            | "DAY_MICROSECOND" -> VInt(compose [ day, 0; hour, 2; minute, 2; second, 2; microseconds, 6 ])
            | "DAY_SECOND" -> VInt(compose [ day, 0; hour, 2; minute, 2; second, 2 ])
            | "DAY_MINUTE" -> VInt(compose [ day, 0; hour, 2; minute, 2 ])
            | "DAY_HOUR" -> VInt(compose [ day, 0; hour, 2 ])
            | "YEAR_MONTH" -> VInt(compose [ year, 0; month, 2 ])
            | _ -> VNull

        match toText u |> Option.map (fun unit -> unit.ToUpperInvariant()), v with
        | Some "WEEK", _ -> weekFn 0 [ v ]
        | Some unit, VZeroDate date ->
            let year, month, day = zeroDateParts date
            extract unit year month day 0 0 0 0
        | Some unit, VZeroDateTime dateTime ->
            let date, hour, minute, second, microseconds = zeroDateTimeParts dateTime
            let year, month, day = zeroDateParts date
            extract unit year month day hour minute second microseconds
        | Some unit, VTime value ->
            let hour, minute, second, microseconds = timeParts value

            match unit with
            | "MICROSECOND"
            | "SECOND"
            | "MINUTE"
            | "HOUR"
            | "SECOND_MICROSECOND"
            | "MINUTE_MICROSECOND"
            | "MINUTE_SECOND"
            | "HOUR_MICROSECOND"
            | "HOUR_SECOND"
            | "HOUR_MINUTE"
            | "DAY_MICROSECOND"
            | "DAY_SECOND"
            | "DAY_MINUTE"
            | "DAY_HOUR" ->
                match extract unit 0 0 0 hour minute second microseconds with
                | VInt result when timeTicks value < 0L -> VInt(-result)
                | result -> result
            | _ -> VNull
        | Some unit, value ->
            tryDateTimeValue value
            |> Option.map (fun dateTime ->
                let microseconds = int ((dateTime.Ticks % 10_000_000L) / 10L)
                extract unit dateTime.Year dateTime.Month dateTime.Day dateTime.Hour dateTime.Minute dateTime.Second microseconds)
            |> Option.defaultValue VNull
        | _ -> VNull
    | _ -> VNull

let private lastDayZeroDate date =
    let year, month, _ = zeroDateParts date

    if year = 0 || month = 0 then
        VNull
    else
        VDate(DateOnly(year, month, DateTime.DaysInMonth(year, month)))

let private lastDayFn: Scalar =
    function
    | [ VZeroDate date ] -> lastDayZeroDate date
    | [ VZeroDateTime dateTime ] -> lastDayZeroDate (zeroDateOfDateTime dateTime)
    | [ v ] when not (anyNull [ v ]) ->
        tryDateTimeValue v
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

type private ConversionZone =
    | FixedOffset of minutes: int
    | SystemZone

/// A `CONVERT_TZ` zone argument. Numeric offsets use MySQL's asymmetric
/// documented range '-13:59'..'+14:00'; `SYSTEM` follows the server process's
/// local time zone. Named zones need MySQL's loadable time-zone tables and
/// remain unavailable when those tables are empty.
let private conversionZone (spec: string) : ConversionZone option =
    let s = spec.Trim()

    if s.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase) then
        Some SystemZone
    else
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
                    if total >= -839 && total <= 840 then Some(FixedOffset total) else None
                | _ -> None
            | _ -> None

let private toUtc (zone: ConversionZone) (value: DateTime) : DateTime =
    match zone with
    | FixedOffset minutes -> value.AddMinutes(float -minutes)
    | SystemZone -> TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), TimeZoneInfo.Local)

let private fromUtc (zone: ConversionZone) (value: DateTime) : DateTime =
    match zone with
    | FixedOffset minutes -> value.AddMinutes(float minutes)
    | SystemZone -> TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(value, DateTimeKind.Utc), TimeZoneInfo.Local)

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
        match tryDateTimeValue dt, conversionZone (req f), conversionZone (req t) with
        | Some d, Some fromZone, Some toZone ->
            let utc =
                try
                    Some(toUtc fromZone d)
                with _ ->
                    None

            match utc with
            | Some u when u >= windowStart && u <= windowEnd ->
                try
                    VDateTime(fromUtc toZone u)
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
    | [ value; posV ] when not (anyNull [ value; posV ]) ->
        match tryRawBytes value with
        | Some bytes ->
            match resolveStart bytes.Length (int (toDouble posV)) with
            | None -> VBytes [||]
            | Some start -> VBytes(bytes.[start..])
        | None ->
            let text = req value

            match resolveStart text.Length (int (toDouble posV)) with
            | None -> VString ""
            | Some start -> VString(text.Substring start)
    | [ value; posV; lenV ] when not (anyNull [ value; posV; lenV ]) ->
        let takeLen = int (toDouble lenV)

        match tryRawBytes value with
        | Some bytes ->
            match resolveStart bytes.Length (int (toDouble posV)) with
            | None -> VBytes [||]
            | Some start when takeLen <= 0 || start = bytes.Length -> VBytes [||]
            | Some start -> VBytes(bytes.[start .. start + min takeLen (bytes.Length - start) - 1])
        | None ->
            let text = req value

            match resolveStart text.Length (int (toDouble posV)) with
            | None -> VString ""
            | Some _ when takeLen <= 0 -> VString ""
            | Some start -> VString(text.Substring(start, min takeLen (text.Length - start)))
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

let private binaryLocateAt (str: string) (sub: string) (startIdx: int) : Value =
    if startIdx > str.Length || startIdx < 0 then
        VInt 0L
    else
        VInt(int64 (str.IndexOf(sub, startIdx, StringComparison.Ordinal) + 1))

let private locateFn: Scalar =
    function
    | [ sub; str ] when not (anyNull [ sub; str ]) ->
        if hasRawBytes [ sub; str ] then binaryLocateAt (binaryText str) (binaryText sub) 0 else locateAt (req str) (req sub) 0
    // A start position below 1 is invalid and yields 0, not a search from
    // the beginning: LOCATE('l', 'Hello', 0) = 0 in MySQL.
    | [ sub; str; posV ] when not (anyNull [ sub; str; posV ]) ->
        let pos = int (toDouble posV)
        if pos < 1 then
            VInt 0L
        elif hasRawBytes [ sub; str ] then
            binaryLocateAt (binaryText str) (binaryText sub) (pos - 1)
        else
            locateAt (req str) (req sub) (pos - 1)
    | _ -> VNull

let private instrFn: Scalar =
    function
    | [ str; sub ] when not (anyNull [ str; sub ]) ->
        if hasRawBytes [ str; sub ] then binaryLocateAt (binaryText str) (binaryText sub) 0 else locateAt (req str) (req sub) 0
    | _ -> VNull

let private replaceFn: Scalar =
    function
    | [ s; f; t ] when not (anyNull [ s; f; t ]) ->
        let raw = hasRawBytes [ s; f; t ]
        let source = if raw then binaryText s else req s
        let from = if raw then binaryText f else req f
        let replacement = if raw then binaryText t else req t
        let result = if from = "" then source else source.Replace(from, replacement)
        if raw then binaryValue result else VString result
    | _ -> VNull

let private insertStringFn: Scalar =
    function
    | [ source; position; length; replacement ] when not (anyNull [ source; position; length; replacement ]) ->
        let integerArgument value =
            let number = toDouble value

            if Double.IsNaN number then 0L
            elif number >= float System.Int64.MaxValue then System.Int64.MaxValue
            elif number <= float System.Int64.MinValue then System.Int64.MinValue
            else int64 number

        let position = integerArgument position
        let length = integerArgument length

        match tryRawBytes source with
        | Some bytes ->
            if position < 1L || position > int64 bytes.Length then
                source
            else
                let start = int position - 1
                let removed =
                    if length < 0L then bytes.Length - start
                    else min (int (min length (int64 System.Int32.MaxValue))) (bytes.Length - start)

                let inserted = tryRawBytes replacement |> Option.defaultValue (Encoding.UTF8.GetBytes(req replacement))
                Array.concat [ Array.take start bytes; inserted; Array.skip (start + removed) bytes ] |> VBytes
        | None ->
            let characters = req source |> _.EnumerateRunes() |> Seq.map _.ToString() |> Seq.toArray

            if position < 1L || position > int64 characters.Length then
                source
            else
                let start = int position - 1
                let removed =
                    if length < 0L then characters.Length - start
                    else min (int (min length (int64 System.Int32.MaxValue))) (characters.Length - start)

                String.concat
                    ""
                    [ characters |> Array.take start |> String.concat ""
                      req replacement
                      characters |> Array.skip (start + removed) |> String.concat "" ]
                |> VString
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'insert'"))

let private padFn (left: bool) : Scalar =
    function
    | [ s; lenV; p ] when not (anyNull [ s; lenV; p ]) ->
        let raw = hasRawBytes [ s; p ]
        let str = if raw then binaryText s else req s
        let pad = if raw then binaryText p else req p
        let targetLen = int (toDouble lenV)

        let result value = if raw then binaryValue value else VString value

        if targetLen < 0 then
            VNull
        elif targetLen = 0 then
            result ""
        elif targetLen <= str.Length then
            result (str.Substring(0, targetLen))
        elif pad = "" then
            VNull
        elif targetLen > Limits.maxAllowedPacket then
            VNull
        else
            let needed = targetLen - str.Length
            let padding = (String.replicate (needed / pad.Length + 1) pad).Substring(0, needed)
            result (if left then padding + str else str + padding)
    | _ -> VNull

/// `LEFT` and `RIGHT` count bytes for binary values and characters for text.
let private leftFn: Scalar =
    function
    | [ value; n ] when not (anyNull [ value; n ]) ->
        match tryRawBytes value with
        | Some bytes -> VBytes(Array.truncate (max 0 (int (toDouble n))) bytes)
        | None ->
            let text = req value
            VString(text.Substring(0, max 0 (min text.Length (int (toDouble n)))))
    | _ -> VNull

let private rightFn: Scalar =
    function
    | [ value; n ] when not (anyNull [ value; n ]) ->
        match tryRawBytes value with
        | Some bytes ->
            let k = max 0 (min bytes.Length (int (toDouble n)))
            VBytes(bytes.[bytes.Length - k ..])
        | None ->
            let text = req value
            let k = max 0 (min text.Length (int (toDouble n)))
            VString(text.Substring(text.Length - k))
    | _ -> VNull

let private repeatFn: Scalar =
    function
    | [ value; n ] when not (anyNull [ value; n ]) ->
        let k = int (toDouble n)

        match tryRawBytes value with
        | Some bytes ->
            if k <= 0 then VBytes [||]
            elif int64 k * int64 bytes.Length > int64 Limits.maxAllowedPacket then VNull
            else Array.replicate k bytes |> Array.concat |> VBytes
        | None ->
            let text = req value
            if k <= 0 then VString ""
            elif int64 k * int64 text.Length > int64 Limits.maxAllowedPacket then VNull
            else VString(String.replicate k text)
    | _ -> VNull

let private spaceFn: Scalar =
    function
    | [ n ] when not (anyNull [ n ]) ->
        let k = max 0 (int (toDouble n))
        if k > Limits.maxAllowedPacket then VNull else VString(String(' ', k))
    | _ -> VNull

/// The first byte of the string's UTF-8 encoding, not the first UTF-16 code
/// unit — `ASCII('é')` is `195` (0xC3, the lead byte of é's 2-byte UTF-8
/// encoding) in MySQL, not é's UTF-16 value 233.
let private asciiFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        match tryRawBytes value with
        | Some bytes -> VInt(if bytes.Length = 0 then 0L else int64 bytes.[0])
        | None ->
            let text = req value
            VInt(if text = "" then 0L else int64 (Text.Encoding.UTF8.GetBytes(text).[0]))
    | _ -> VNull

let private ordFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        match tryRawBytes value with
        | Some bytes -> VInt(if bytes.Length = 0 then 0L else int64 bytes.[0])
        | None ->
            let text = req value

            if text.Length = 0 then
                VInt 0L
            else
                let scalarLength = if Char.IsSurrogatePair(text, 0) then 2 else 1
                let bytes = Text.Encoding.UTF8.GetBytes(text.Substring(0, scalarLength))
                VInt(bytes |> Array.fold (fun result part -> result * 256L + int64 part) 0L)
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
    | [ value ] when not (anyNull [ value ]) ->
        match tryRawBytes value with
        | Some bytes -> VString(bytes |> Array.map (fun byte -> byte.ToString "X2") |> String.concat "")
        | None ->
            match value with
            | VInt value -> VString(value.ToString "X")
            | VUInt value -> VString(value.ToString "X")
            | VString value -> VString(Text.Encoding.UTF8.GetBytes value |> Array.map (fun byte -> byte.ToString "X2") |> String.concat "")
            | _ -> VString((int64 (toDouble value)).ToString "X")
    | _ -> VNull

let private unhexFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        let s = req v

        if not (s |> Seq.forall Uri.IsHexDigit) then
            VNull
        else
            let digits = if s.Length % 2 = 0 then s else "0" + s

            [| for i in 0 .. 2 .. digits.Length - 1 -> Convert.ToByte(digits.Substring(i, 2), 16) |]
            |> VBytes
    | _ -> VNull

type private AesCipherMode =
    | AesEcb
    | AesCbc
    | AesCfb1
    | AesCfb8
    | AesCfb128
    | AesOfb

type private AesConfiguration =
    { KeyLength: int
      CipherMode: AesCipherMode }

let private aesModeNames =
    [ for keySize in [ 128; 192; 256 ] do
          for cipherMode in [ "ecb"; "cbc"; "cfb1"; "cfb8"; "cfb128"; "ofb" ] do
              yield sprintf "aes-%d-%s" keySize cipherMode ]

/// The canonical `block_encryption_mode` value, when MySQL supports it.
let tryBlockEncryptionMode (value: string) : string option =
    let canonical = value.Trim().ToLowerInvariant()
    if List.contains canonical aesModeNames then Some canonical else None

let private aesConfiguration (value: string) : AesConfiguration =
    match tryBlockEncryptionMode value with
    | Some canonical ->
        let parts = canonical.Split '-'

        { KeyLength = int parts.[1] / 8
          CipherMode =
            match parts.[2] with
            | "ecb" -> AesEcb
            | "cbc" -> AesCbc
            | "cfb1" -> AesCfb1
            | "cfb8" -> AesCfb8
            | "cfb128" -> AesCfb128
            | "ofb" -> AesOfb
            | _ -> invalidArg "value" "Unsupported AES cipher mode" }
    | None -> invalidArg "value" "Unsupported AES block encryption mode"

let private aesBytes (value: Value) : byte[] =
    tryRawBytes value |> Option.defaultWith (fun () -> Encoding.UTF8.GetBytes(req value))

let private aesParameterCountError (name: string) : 'a =
    raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" name))

let private aesKdfOptionError maxLength : 'a =
    raise (SqlError(3238, sprintf "KDF option size is invalid, please provide valid size < %d bytes and not NULL" maxLength))

let private aesPbkdf2IterationError () : 'a =
    raise (
        SqlError(
            3236,
            "For KDF method pbkdf2_hmac iterations value less than 1000 or more than 65535 is not allowed due to security reasons. Please provide iterations >= 1000 and iterations < 65535"
        )
    )

let private aesKeyWithoutKdf (keyLength: int) (keyMaterial: byte[]) : byte[] =
    let key = Array.zeroCreate keyLength

    for i in 0 .. keyMaterial.Length - 1 do
        key.[i % key.Length] <- key.[i % key.Length] ^^^ keyMaterial.[i]

    key

let private aesKdfOption (maxLength: int) (value: Value) : byte[] =
    match value with
    | VNull -> aesKdfOptionError maxLength
    | _ ->
        let bytes = aesBytes value

        if bytes.Length >= maxLength then
            aesKdfOptionError maxLength

        Array.copy bytes

let private aesPbkdf2Iterations (value: Value) : int =
    let bytes = aesKdfOption 6 value

    try
        let iterations = int (toDouble value)

        if iterations < 1000 || iterations > 65535 then
            aesPbkdf2IterationError ()

        iterations
    finally
        CryptographicOperations.ZeroMemory bytes

let private deriveAesKey (functionName: string) (keyLength: int) (keyMaterial: byte[]) (kdfName: Value) (options: Value list) : byte[] =
    let kdf = aesKdfOption 256 kdfName

    try
        match Encoding.UTF8.GetString(kdf).ToLowerInvariant(), options with
        | "hkdf", [] -> HKDF.DeriveKey(HashAlgorithmName.SHA512, keyMaterial, keyLength, Array.empty, Array.empty)
        | "hkdf", [ salt ] ->
            let saltBytes = aesKdfOption 256 salt

            try
                HKDF.DeriveKey(HashAlgorithmName.SHA512, keyMaterial, keyLength, saltBytes, Array.empty)
            finally
                CryptographicOperations.ZeroMemory saltBytes
        | "hkdf", [ salt; info ] ->
            let saltBytes = aesKdfOption 256 salt
            let infoBytes = aesKdfOption 256 info

            try
                HKDF.DeriveKey(HashAlgorithmName.SHA512, keyMaterial, keyLength, saltBytes, infoBytes)
            finally
                CryptographicOperations.ZeroMemory saltBytes
                CryptographicOperations.ZeroMemory infoBytes
        | "pbkdf2_hmac", [] -> Rfc2898DeriveBytes.Pbkdf2(keyMaterial, Array.empty, 1000, HashAlgorithmName.SHA512, keyLength)
        | "pbkdf2_hmac", [ salt ] ->
            let saltBytes = aesKdfOption 256 salt

            try
                Rfc2898DeriveBytes.Pbkdf2(keyMaterial, saltBytes, 1000, HashAlgorithmName.SHA512, keyLength)
            finally
                CryptographicOperations.ZeroMemory saltBytes
        | "pbkdf2_hmac", [ salt; iterations ] ->
            let saltBytes = aesKdfOption 256 salt

            try
                Rfc2898DeriveBytes.Pbkdf2(keyMaterial, saltBytes, aesPbkdf2Iterations iterations, HashAlgorithmName.SHA512, keyLength)
            finally
                CryptographicOperations.ZeroMemory saltBytes
        | "hkdf", _
        | "pbkdf2_hmac", _ -> aesParameterCountError functionName
        | _ -> raise (SqlError(3235, "KDF method name is not valid. Please use hkdf or pbkdf2_hmac method name"))
    finally
        CryptographicOperations.ZeroMemory kdf

let private aesEncryptBlock (key: byte[]) (block: byte[]) : byte[] =
    use aes = Aes.Create()
    aes.Key <- key
    aes.Mode <- CipherMode.ECB
    aes.Padding <- PaddingMode.None
    use transform = aes.CreateEncryptor()
    let encrypted = Array.zeroCreate 16
    transform.TransformBlock(block, 0, block.Length, encrypted, 0) |> ignore
    encrypted

let private aesDecryptBlock (key: byte[]) (block: byte[]) : byte[] =
    use aes = Aes.Create()
    aes.Key <- key
    aes.Mode <- CipherMode.ECB
    aes.Padding <- PaddingMode.None
    use transform = aes.CreateDecryptor()
    let decrypted = Array.zeroCreate 16
    transform.TransformBlock(block, 0, block.Length, decrypted, 0) |> ignore
    decrypted

let private aesPad (input: byte[]) : byte[] =
    let paddingLength = 16 - input.Length % 16
    let padded = Array.zeroCreate (input.Length + paddingLength)
    Array.Copy(input, padded, input.Length)
    Array.Fill(padded, byte paddingLength, input.Length, paddingLength)
    padded

let private aesTryUnpad (input: byte[]) : byte[] option =
    if input.Length = 0 || input.Length % 16 <> 0 then
        None
    else
        let paddingLength = int input.[input.Length - 1]

        if paddingLength < 1 || paddingLength > 16 || input.[input.Length - paddingLength ..] |> Array.exists (fun value -> int value <> paddingLength) then
            None
        else
            Some input.[0 .. input.Length - paddingLength - 1]

let private xorInto (target: byte[]) (left: byte[]) (right: byte[]) : unit =
    for i in 0 .. target.Length - 1 do
        target.[i] <- left.[i] ^^^ right.[i]

let private shiftRegister (register: byte[]) (feedback: byte[]) : unit =
    let count = feedback.Length
    Array.Copy(register, count, register, 0, register.Length - count)
    Array.Copy(feedback, 0, register, register.Length - count, count)

let private shiftRegisterBit (register: byte[]) (feedback: byte) : unit =
    for i in 0 .. register.Length - 2 do
        register.[i] <- (register.[i] <<< 1) ||| (register.[i + 1] >>> 7)

    register.[register.Length - 1] <- (register.[register.Length - 1] <<< 1) ||| feedback

let private aesEncryptBlockMode (configuration: AesConfiguration) (key: byte[]) (initializationVector: byte[]) (input: byte[]) : byte[] =
    match configuration.CipherMode with
    | AesEcb
    | AesCbc ->
        let padded = aesPad input
        let output = Array.zeroCreate padded.Length
        let previous = Array.copy initializationVector
        let block = Array.zeroCreate 16

        try
            for offset in 0 .. 16 .. padded.Length - 16 do
                Array.Copy(padded, offset, block, 0, 16)

                if configuration.CipherMode = AesCbc then
                    xorInto block block previous

                let encrypted = aesEncryptBlock key block
                Array.Copy(encrypted, 0, output, offset, 16)
                Array.Copy(encrypted, previous, 16)
                CryptographicOperations.ZeroMemory encrypted

            output
        finally
            CryptographicOperations.ZeroMemory padded
            CryptographicOperations.ZeroMemory previous
            CryptographicOperations.ZeroMemory block
    | AesCfb1 ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector

        try
            for i in 0 .. input.Length - 1 do
                let mutable outputByte = 0uy

                for bit in 7 .. -1 .. 0 do
                    let encrypted = aesEncryptBlock key register
                    let sourceBit = (input.[i] >>> bit) &&& 1uy
                    let encryptedBit = (encrypted.[0] >>> 7) &&& 1uy
                    let cipherBit = sourceBit ^^^ encryptedBit
                    outputByte <- outputByte ||| (cipherBit <<< bit)
                    shiftRegisterBit register cipherBit
                    CryptographicOperations.ZeroMemory encrypted

                output.[i] <- outputByte

            output
        finally
            CryptographicOperations.ZeroMemory register
    | AesCfb8
    | AesCfb128 ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector
        let segmentLength = if configuration.CipherMode = AesCfb8 then 1 else 16

        try
            for offset in 0 .. segmentLength .. input.Length - 1 do
                let length = min segmentLength (input.Length - offset)
                let encrypted = aesEncryptBlock key register
                let feedback = Array.zeroCreate length

                for i in 0 .. length - 1 do
                    let cipherByte = input.[offset + i] ^^^ encrypted.[i]
                    output.[offset + i] <- cipherByte
                    feedback.[i] <- cipherByte

                shiftRegister register feedback
                CryptographicOperations.ZeroMemory encrypted
                CryptographicOperations.ZeroMemory feedback

            output
        finally
            CryptographicOperations.ZeroMemory register
    | AesOfb ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector

        try
            for offset in 0 .. 16 .. input.Length - 1 do
                let length = min 16 (input.Length - offset)
                let next = aesEncryptBlock key register
                Array.Copy(next, register, 16)

                for i in 0 .. length - 1 do
                    output.[offset + i] <- input.[offset + i] ^^^ next.[i]

                CryptographicOperations.ZeroMemory next

            output
        finally
            CryptographicOperations.ZeroMemory register

let private aesDecryptBlockMode (configuration: AesConfiguration) (key: byte[]) (initializationVector: byte[]) (input: byte[]) : byte[] option =
    match configuration.CipherMode with
    | AesEcb
    | AesCbc ->
        if input.Length = 0 || input.Length % 16 <> 0 then
            None
        else
            let output = Array.zeroCreate input.Length
            let previous = Array.copy initializationVector
            let block = Array.zeroCreate 16

            try
                for offset in 0 .. 16 .. input.Length - 16 do
                    Array.Copy(input, offset, block, 0, 16)
                    let decrypted = aesDecryptBlock key block

                    if configuration.CipherMode = AesCbc then
                        xorInto decrypted decrypted previous

                    Array.Copy(decrypted, 0, output, offset, 16)
                    Array.Copy(block, previous, 16)
                    CryptographicOperations.ZeroMemory decrypted

                aesTryUnpad output
            finally
                CryptographicOperations.ZeroMemory output
                CryptographicOperations.ZeroMemory previous
                CryptographicOperations.ZeroMemory block
    | AesCfb1 ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector

        try
            for i in 0 .. input.Length - 1 do
                let mutable outputByte = 0uy

                for bit in 7 .. -1 .. 0 do
                    let encrypted = aesEncryptBlock key register
                    let cipherBit = (input.[i] >>> bit) &&& 1uy
                    let plainBit = cipherBit ^^^ ((encrypted.[0] >>> 7) &&& 1uy)
                    outputByte <- outputByte ||| (plainBit <<< bit)
                    shiftRegisterBit register cipherBit
                    CryptographicOperations.ZeroMemory encrypted

                output.[i] <- outputByte

            Some output
        finally
            CryptographicOperations.ZeroMemory register
    | AesCfb8
    | AesCfb128 ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector
        let segmentLength = if configuration.CipherMode = AesCfb8 then 1 else 16

        try
            for offset in 0 .. segmentLength .. input.Length - 1 do
                let length = min segmentLength (input.Length - offset)
                let encrypted = aesEncryptBlock key register
                let feedback = input.[offset .. offset + length - 1]

                for i in 0 .. length - 1 do
                    output.[offset + i] <- input.[offset + i] ^^^ encrypted.[i]

                shiftRegister register feedback
                CryptographicOperations.ZeroMemory encrypted

            Some output
        finally
            CryptographicOperations.ZeroMemory register
    | AesOfb ->
        let output = Array.zeroCreate input.Length
        let register = Array.copy initializationVector

        try
            for offset in 0 .. 16 .. input.Length - 1 do
                let length = min 16 (input.Length - offset)
                let next = aesEncryptBlock key register
                Array.Copy(next, register, 16)

                for i in 0 .. length - 1 do
                    output.[offset + i] <- input.[offset + i] ^^^ next.[i]

                CryptographicOperations.ZeroMemory next

            Some output
        finally
            CryptographicOperations.ZeroMemory register

let private aesArguments (functionName: string) (configuration: AesConfiguration) (args: Value list) : (byte[] * byte[] * byte[]) option =
    match args with
    | data :: key :: rest ->
        let requiresInitializationVector = configuration.CipherMode <> AesEcb
        let reportedFunctionName = if requiresInitializationVector then functionName.ToLowerInvariant() else functionName

        if rest.Length > 4 || (requiresInitializationVector && rest.IsEmpty) then
            aesParameterCountError reportedFunctionName

        if not requiresInitializationVector && rest.Length = 1 then
            Diagnostics.warning 1618 "<IV> option ignored"

        if data = VNull || key = VNull then
            None
        else
            let initializationVector =
                if requiresInitializationVector then
                    let vector = aesBytes rest.Head

                    if vector.Length < 16 then
                        raise (
                            SqlError(
                                1882,
                                sprintf "The initialization vector supplied to %s is too short. Must be at least 16 bytes long" (functionName.ToLowerInvariant())
                            )
                        )

                    vector.[0..15]
                else
                    Array.zeroCreate 16

            let keyMaterial = aesBytes key
            let derivedKey =
                match rest with
                | _ :: kdfName :: options -> deriveAesKey reportedFunctionName configuration.KeyLength keyMaterial kdfName options
                | _ -> aesKeyWithoutKdf configuration.KeyLength keyMaterial

            Some(aesBytes data, derivedKey, initializationVector)
    | _ -> aesParameterCountError functionName

/// Builds an AES function bound to one session's `block_encryption_mode`.
let aesEncrypt (blockEncryptionMode: string) : Scalar =
    let configuration = aesConfiguration blockEncryptionMode

    fun args ->
        match aesArguments "AES_ENCRYPT" configuration args with
        | None -> VNull
        | Some(input, key, initializationVector) ->
            try
                aesEncryptBlockMode configuration key initializationVector input |> VBytes
            finally
                CryptographicOperations.ZeroMemory key
                CryptographicOperations.ZeroMemory initializationVector

/// Builds an AES function bound to one session's `block_encryption_mode`.
let aesDecrypt (blockEncryptionMode: string) : Scalar =
    let configuration = aesConfiguration blockEncryptionMode

    fun args ->
        match aesArguments "AES_DECRYPT" configuration args with
        | None -> VNull
        | Some(input, key, initializationVector) ->
            try
                aesDecryptBlockMode configuration key initializationVector input |> Option.map VBytes |> Option.defaultValue VNull
            finally
                CryptographicOperations.ZeroMemory key
                CryptographicOperations.ZeroMemory initializationVector

let private md5Fn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> VString(Convert.ToHexString(MD5.HashData(stringBytes value)).ToLowerInvariant())
    | _ -> VNull

let private sha1Fn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> VString(Convert.ToHexString(SHA1.HashData(stringBytes value)).ToLowerInvariant())
    | _ -> VNull

let private makeSetFn: Scalar =
    function
    | VNull :: _ -> VNull
    | bits :: values ->
        let mask = toUInt64 (roundNumeric bits)

        values
        |> List.mapi (fun index value -> index, value)
        |> List.choose (fun (index, value) ->
            if index < 64 && mask &&& (1UL <<< index) <> 0UL then
                toText value
            else
                None)
        |> String.concat ","
        |> VString
    | [] -> VNull

let private soundexCode =
    function
    | 'B' | 'F' | 'P' | 'V' -> '1'
    | 'C' | 'G' | 'J' | 'K' | 'Q' | 'S' | 'X' | 'Z' -> '2'
    | 'D' | 'T' -> '3'
    | 'L' -> '4'
    | 'M' | 'N' -> '5'
    | 'R' -> '6'
    | _ -> '0'

let private soundexFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let letters = req value |> Seq.skipWhile (Char.IsLetter >> not) |> Seq.toArray

        if letters.Length = 0 then
            VString ""
        else
            let first = Char.ToUpperInvariant letters.[0]
            let result = StringBuilder().Append first
            let mutable previous = soundexCode first

            for letter in letters.[1..] do
                let code = soundexCode (Char.ToUpperInvariant letter)

                if code <> '0' && code <> previous then
                    result.Append code |> ignore

                if code <> '0' then
                    previous <- code

            while result.Length < 4 do
                result.Append '0' |> ignore

            VString(result.ToString())
    | _ -> VNull

let private toBase64Fn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let bytes =
            tryRawBytes value |> Option.defaultWith (fun () -> Text.Encoding.UTF8.GetBytes(req value))

        let encodedLength = (int64 bytes.Length + 2L) / 3L * 4L
        let lineBreaks = if encodedLength = 0L then 0L else (encodedLength - 1L) / 76L

        if encodedLength + lineBreaks > int64 Limits.maxAllowedPacket then
            VNull
        else
            Convert.ToBase64String bytes
            |> Seq.chunkBySize 76
            |> Seq.map String
            |> String.concat "\n"
            |> VString
    | _ -> VNull

let private fromBase64Fn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        try
            Convert.FromBase64String(req value) |> VBytes
        with :? FormatException ->
            VNull
    | _ -> VNull

let private bytesOfValue =
    function
    | value -> tryRawBytes value |> Option.defaultWith (fun () -> Text.Encoding.UTF8.GetBytes(req value))

let private compressFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let input = bytesOfValue value

        if input.Length = 0 then
            VBytes [||]
        else
            use output = new MemoryStream()
            let lengthBytes = BitConverter.GetBytes(uint32 input.Length)
            output.Write(lengthBytes, 0, lengthBytes.Length)

            use compressor = new ZLibStream(output, CompressionLevel.Optimal, true)
            compressor.Write(input, 0, input.Length)
            compressor.Close()
            let compressed = output.ToArray()
            VBytes(if compressed.[compressed.Length - 1] = 0x20uy then Array.append compressed [| 0x2euy |] else compressed)
    | _ -> VNull

let private uncompressFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let input = bytesOfValue value

        if input.Length = 0 then
            VBytes [||]
        elif input.Length < 6 then
            VNull
        else
            try
                use source = new MemoryStream(input, 4, input.Length - 4)
                use decompressor = new ZLibStream(source, CompressionMode.Decompress)
                use output = new MemoryStream()
                decompressor.CopyTo output
                VBytes(output.ToArray())
            with _ ->
                VNull
    | _ -> VNull

let private uncompressedLengthFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let input = bytesOfValue value
        if input.Length = 0 then VInt 0L elif input.Length < 4 then VNull else VUInt(uint64 (BitConverter.ToUInt32(input, 0)))
    | _ -> VNull

let private randomBytesFn: Scalar =
    function
    | [ count ] when not (anyNull [ count ]) ->
        let length = int (toDouble count)

        if length < 1 || length > 1024 then
            raise (SqlError(1690, "The length of RANDOM_BYTES must be between 1 and 1024"))

        VBytes(RandomNumberGenerator.GetBytes length)
    | _ -> VNull

let mutable private uuidShortSequence = DateTimeOffset.UtcNow.ToUnixTimeSeconds() <<< 24

let private uuidShortFn: Scalar =
    function
    | [] -> Threading.Interlocked.Increment(&uuidShortSequence) |> uint64 |> VUInt
    | _ -> VNull

let private nameConstFn: Scalar =
    function
    | [ name; value ] when not (anyNull [ name ]) -> value
    | _ -> VNull

let private anyValueFn: Scalar =
    function
    | [ value ] -> value
    | _ -> VNull

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
        let bytes = stringBytes s

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
        let raw = hasRawBytes [ s; d ]
        let str = if raw then binaryText s else req s
        let delim = if raw then binaryText d else req d
        let count = int (toDouble c)

        let result value = if raw then binaryValue value else VString value

        if delim = "" || count = 0 then
            result ""
        else
            let parts = str.Split([| delim |], StringSplitOptions.None)

            if count > 0 then
                result (String.Join(delim, parts |> Array.truncate count))
            else
                let take = min (-count) parts.Length
                result (String.Join(delim, parts |> Array.skip (parts.Length - take)))
    | _ -> VNull

let private trimSubstring trimLeading trimTrailing : Scalar =
    function
    | [ removed; source ] when not (anyNull [ removed; source ]) ->
        let raw = hasRawBytes [ removed; source ]
        let removed = if raw then binaryText removed else req removed
        let mutable result = if raw then binaryText source else req source

        if removed <> "" then
            if trimLeading then
                while result.StartsWith(removed, StringComparison.Ordinal) do
                    result <- result.Substring removed.Length

            if trimTrailing then
                while result.EndsWith(removed, StringComparison.Ordinal) do
                    result <- result.Substring(0, result.Length - removed.Length)

        if raw then binaryValue result else VString result
    | _ -> VNull

let private concatWsFn: Scalar =
    function
    | sep :: rest when not (anyNull [ sep ]) ->
        let values = rest |> List.filter ((<>) VNull)

        if hasRawBytes (sep :: values) then
            values |> List.map binaryText |> String.concat (binaryText sep) |> binaryValue
        else
            values |> List.map req |> String.concat (req sep) |> VString
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

let private bitwiseUnary (operation: uint64 -> uint64) : Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> value |> roundNumeric |> toUInt64 |> operation |> VUInt
    | _ -> VNull

let private bitwiseBinary (operation: uint64 -> uint64 -> uint64) : Scalar =
    function
    | [ left; right ] when not (anyNull [ left; right ]) ->
        VUInt(operation (toUInt64 (roundNumeric left)) (toUInt64 (roundNumeric right)))
    | _ -> VNull

let private bitwiseShift (operation: uint64 -> int -> uint64) : Scalar =
    function
    | [ value; count ] when not (anyNull [ value; count ]) ->
        let shift = toUInt64 (roundNumeric count)

        if shift >= 64UL then
            VUInt 0UL
        else
            VUInt(operation (toUInt64 (roundNumeric value)) (int shift))
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
// REGEXP_LIKE/REPLACE/SUBSTR/INSTR share the bounded compiler used by the
// REGEXP operator, so invalid patterns and pathological matches agree.
// ---------------------------------------------------------------------------

let private raiseRegexError (functionName: string) = function
    | Regexp.InvalidPattern _ as error -> raise (SqlError(Regexp.errorCode error, Regexp.errorMessage error))
    | Regexp.InvalidMatchType -> raise (SqlError(1210, sprintf "Incorrect arguments to %s" functionName))

let private regexResult (functionName: string) (collation: Collation.Collation) (matchType: string option) (pattern: string) =
    match Regexp.compile collation matchType pattern with
    | Ok regex -> regex
    | Error error -> raiseRegexError functionName error

let private withRegexTimeout operation =
    try
        operation ()
    with :? RegexMatchTimeoutException ->
        raise (SqlError(3699, "Timeout exceeded in regular expression match."))

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

let private normalizedOffset (input: Regexp.PreparedInput) sourceOffset =
    input.SourceOffsets
    |> Array.tryFindIndex (fun offset -> offset > sourceOffset)
    |> Option.map (fun index -> index - 1)
    |> Option.defaultValue input.Text.Length

let private regexpPositionError () =
    raise (SqlError(3686, "Index out of bounds in regular expression search."))

let private regexpLikeFn (collation: Collation.Collation) : Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        if anyNull rest then VNull
        else
            let regex = regexResult "regexp_like" collation (matchTypeArg rest 0) (req p)
            let input = Regexp.prepareInput (matchTypeArg rest 0) (req p) (req e)
            withRegexTimeout (fun () -> if regex.IsMatch input.Text then VInt 1L else VInt 0L)
    | _ -> VNull

let private regexpInstrFn (collation: Collation.Collation) : Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        if anyNull rest then VNull
        else
            let input = Regexp.prepareInput (matchTypeArg rest 3) (req p) (req e)
            let source = req e
            let pos = intArgOr 1 rest 0
            let occurrence = intArgOr 1 rest 1
            let returnEnd = intArgOr 0 rest 2 <> 0

            let regex = regexResult "regexp_instr" collation (matchTypeArg rest 3) (req p)

            withRegexTimeout (fun () ->
                if pos < 1 || pos > Regexp.scalarCount source then
                    regexpPositionError ()

                let sourceStart = Regexp.utf16OffsetAtScalar source (pos - 1)
                let start = normalizedOffset input sourceStart + 1

                match nthMatch regex input.Text start occurrence with
                | Some m ->
                    let offset = if returnEnd then m.Index + m.Length else m.Index
                    let sourceOffset = max sourceStart (Regexp.sourceOffset input offset)
                    VInt(int64 (Regexp.scalarAtUtf16Offset source sourceOffset + 1))
                | None -> VInt 0L)
    | _ -> VNull

let private regexpSubstrFn (collation: Collation.Collation) : Scalar =
    function
    | e :: p :: rest when not (anyNull [ e; p ]) ->
        if anyNull rest then VNull
        else
            let source = req e
            let input = Regexp.prepareInput (matchTypeArg rest 2) (req p) source
            let pos = intArgOr 1 rest 0
            let occurrence = intArgOr 1 rest 1

            let regex = regexResult "regexp_substr" collation (matchTypeArg rest 2) (req p)

            withRegexTimeout (fun () ->
                if pos < 1 then
                    regexpPositionError ()

                let sourceStart = Regexp.utf16OffsetAtScalar source (pos - 1)

                if sourceStart = source.Length && pos > Regexp.scalarCount source then
                    VNull
                else
                    let start = normalizedOffset input sourceStart + 1

                    match nthMatch regex input.Text start occurrence with
                    | Some m ->
                        let start = max sourceStart (Regexp.sourceOffset input m.Index)
                        let finish = Regexp.sourceOffset input (m.Index + m.Length)
                        VString(source.Substring(start, finish - start))
                    | None -> VNull)
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

let private replacementText (source: string) (input: Regexp.PreparedInput) (repl: string) (m: Match) =
    Regex.Replace(
        repl,
        @"\$(\d+)",
        fun token ->
            let digits = token.Groups.[1].Value
            let mutable index = 0
            let mutable consumed = 0
            let mutable group = None

            while consumed < digits.Length do
                let digit = int digits[consumed] - int '0'

                if index <= (m.Groups.Count - 1 - digit) / 10 then
                    index <- index * 10 + digit

                    if index < m.Groups.Count then
                        group <- Some(index, consumed + 1)

                consumed <- consumed + 1

            let index, suffix =
                match group with
                | Some(index, length) -> index, digits.Substring length
                | None -> raise (SqlError(3686, "Index out of bounds in regular expression search."))

            let group = m.Groups[index]

            if group.Success then
                let start = Regexp.sourceOffset input group.Index
                let finish = Regexp.sourceOffset input (group.Index + group.Length)
                source.Substring(start, finish - start) + suffix
            else
                suffix
    )

let private replaceMatches (regex: Regex) (input: Regexp.PreparedInput) (source: string) pos occurrence repl =
    let sourceStart = Regexp.utf16OffsetAtScalar source (pos - 1)
    let start = normalizedOffset input sourceStart
    let sourceInput: Regexp.PreparedInput =
        { Text = source
          SourceOffsets = [| 0 .. source.Length |] }

    let sourceRegex = Regex(regex.ToString(), regex.Options, Limits.regexpMatchTimeout)
    let builder = StringBuilder(min source.Length Limits.maxAllowedPacket)
    let mutable current = sourceStart
    let mutable count = 0
    let mutable m = regex.Match(input.Text, start)

    let append (text: string) offset length =
        if int64 builder.Length + int64 length > int64 Limits.maxAllowedPacket then
            raise (SqlError(1153, "Result of REGEXP_REPLACE() exceeds max_allowed_packet"))

        builder.Append(text, offset, length) |> ignore

    let appendText (text: string) = append text 0 text.Length

    append source 0 sourceStart

    let emit matchInput m matchStart matchEnd =
        append source current (matchStart - current)
        count <- count + 1

        if occurrence <= 0 || count = occurrence then
            appendText (replacementText source matchInput repl m)
        else
            append source matchStart (matchEnd - matchStart)

        current <- matchEnd

    while m.Success do
        let matchStart = max current (Regexp.sourceOffset input m.Index)
        let matchEnd = max matchStart (Regexp.sourceOffset input (m.Index + m.Length))
        emit input m matchStart matchEnd

        if m.Length = 0
           && matchStart = Regexp.sourceOffset input m.Index
           && m.Index + 1 < input.SourceOffsets.Length
           && input.SourceOffsets[m.Index + 1] = matchStart + 2
           && source[matchStart] = '\r' then
            let next = sourceRegex.Match(source, matchStart + 1, 1)

            if next.Success && next.Index = matchStart + 1 && next.Length = 0 then
                emit sourceInput next (matchStart + 1) (matchStart + 1)

        m <- m.NextMatch()

    append source current (source.Length - current)
    builder.ToString()

/// `occurrence = 0` (the default) replaces every match; a positive
/// `occurrence` replaces only that one match, leaving the rest of the
/// string untouched either way.
let private regexpReplaceFn (collation: Collation.Collation) : Scalar =
    function
    | e :: p :: r :: rest when not (anyNull [ e; p; r ]) ->
        if anyNull rest then VNull
        else
            let source = req e
            let repl = toDotNetReplacement (req r)
            let pos = intArgOr 1 rest 0
            let occurrence = intArgOr 0 rest 1

            let regex = regexResult "regexp_replace" collation (matchTypeArg rest 2) (req p)
            let input = Regexp.prepareInput (matchTypeArg rest 2) (req p) source

            withRegexTimeout (fun () ->
                if pos < 1 then
                    regexpPositionError ()
                elif pos > Regexp.scalarCount source + 1 then
                    VNull
                else
                    VString(replaceMatches regex input source pos occurrence repl))
    | _ -> VNull

let private regexpArity (name: string) =
    match name.ToUpperInvariant() with
    | "REGEXP_LIKE" -> Some(2, 3)
    | "REGEXP_INSTR" -> Some(2, 6)
    | "REGEXP_SUBSTR" -> Some(2, 5)
    | "REGEXP_REPLACE" -> Some(3, 6)
    | _ -> None

let validateRegexpArity (name: string) arguments =
    let count = List.length arguments

    match regexpArity name with
    | Some(minimum, maximum) when count < minimum || count > maximum ->
        raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" name))
    | _ -> ()

let regexpFunction (name: string) (collation: Collation.Collation) : Scalar option =
    match name.ToUpperInvariant() with
    | "REGEXP_LIKE" -> Some(fun arguments -> validateRegexpArity name arguments; regexpLikeFn collation arguments)
    | "REGEXP_INSTR" -> Some(fun arguments -> validateRegexpArity name arguments; regexpInstrFn collation arguments)
    | "REGEXP_SUBSTR" -> Some(fun arguments -> validateRegexpArity name arguments; regexpSubstrFn collation arguments)
    | "REGEXP_REPLACE" -> Some(fun arguments -> validateRegexpArity name arguments; regexpReplaceFn collation arguments)
    | _ -> None

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

let private mathResult (name: string) (value: float) =
    if Double.IsNaN value then
        VNull
    elif Double.IsInfinity value then
        raise (SqlError(1690, sprintf "DOUBLE value is out of range in '%s'" name))
    else
        VDouble value

let private unaryMath (name: string) (f: float -> float) : Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> mathResult name (f (toDouble value))
    | _ -> VNull

let private logFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let value = toDouble value
        if value <= 0.0 then VNull else mathResult "log" (Math.Log value)
    | [ baseValue; value ] when not (anyNull [ baseValue; value ]) ->
        let baseValue = toDouble baseValue
        let value = toDouble value

        if baseValue <= 0.0 || baseValue = 1.0 || value <= 0.0 then
            VNull
        else
            mathResult "log" (Math.Log(value, baseValue))
    | _ -> VNull

let private positiveLog (name: string) (f: float -> float) : Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let value = toDouble value
        if value <= 0.0 then VNull else mathResult name (f value)
    | _ -> VNull

let private cotFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> mathResult "cot" (1.0 / Math.Tan(toDouble value))
    | _ -> VNull

let private atanFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> VDouble(Math.Atan(toDouble value))
    | [ y; x ] when not (anyNull [ y; x ]) -> VDouble(Math.Atan2(toDouble y, toDouble x))
    | _ -> VNull

let private atan2Fn: Scalar =
    function
    | [ y; x ] when not (anyNull [ y; x ]) -> VDouble(Math.Atan2(toDouble y, toDouble x))
    | _ -> VNull

let private piFn: Scalar =
    function
    | [] -> VDouble Math.PI
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

let private seededRandom seed =
    let modulus = 0x3fffffffuL
    let seed = toUInt64 (roundNumeric seed) % modulus
    let mutable first = (seed * 0x10001uL + 55555555uL) % modulus
    let mutable second = (seed * 0x10000001uL) % modulus
    first <- (first * 3uL + second) % modulus
    second <- (first + second + 33uL) % modulus
    float first / float modulus

let private randFn: Scalar =
    function
    | [] -> VDouble(random.NextDouble())
    | [ seed ] when not (anyNull [ seed ]) -> VDouble(seededRandom seed)
    | _ -> VNull

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
    | [ value ] when not (anyNull [ value ]) -> VInt(int64 (crc32 (stringBytes value)))
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

let private uuidByteLength = 16

let private hasUuidByteLength (bytes: byte[]) =
    bytes.Length = uuidByteLength

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
        if not (hasUuidByteLength b) then
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

        if not (hasUuidByteLength raw) then
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

let private ipv4ByteLength = 4
let private ipv6ByteLength = 16

let private tryParseIpv4 (text: string) =
    let parts = text.Split '.'

    if parts.Length <> ipv4ByteLength then
        None
    else
        parts
        |> Array.choose (fun part ->
            match Byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, value when part.Length > 0 -> Some value
            | _ -> None)
        |> fun bytes -> if bytes.Length = ipv4ByteLength then Some bytes else None

let private tryParseIpv6 (text: string) =
    if text.IndexOfAny [| '%'; '['; ']' |] >= 0 then
        None
    else
        match IPAddress.TryParse text with
        | true, address when address.AddressFamily = AddressFamily.InterNetworkV6 -> Some address
        | _ -> None

let private inet6AtonFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let text = req value

        match tryParseIpv4 text with
        | Some bytes -> VBytes bytes
        | None -> tryParseIpv6 text |> Option.map (fun address -> VBytes(address.GetAddressBytes())) |> Option.defaultValue VNull
    | _ -> VNull

let private packedAddressBytes =
    function
    | value -> tryRawBytes value |> Option.defaultWith (fun () -> Text.Encoding.Latin1.GetBytes(req value))

let private hasPackedAddressLength (bytes: byte[]) =
    bytes.Length = ipv4ByteLength || bytes.Length = ipv6ByteLength

let private hasIpv6Length (bytes: byte[]) =
    bytes.Length = ipv6ByteLength

let private inet6NtoaFn: Scalar =
    function
    | [ value ] when not (anyNull [ value ]) ->
        let bytes = packedAddressBytes value

        if hasPackedAddressLength bytes then
            VString(IPAddress(bytes).ToString())
        else
            VNull
    | _ -> VNull

let private addressPredicate (predicate: Value -> bool) : Scalar =
    function
    | [ value ] when not (anyNull [ value ]) -> VInt(if predicate value then 1L else 0L)
    | _ -> VNull

let private isIpv4Fn = addressPredicate (req >> tryParseIpv4 >> Option.isSome)

let private isIpv6Fn =
    addressPredicate (req >> tryParseIpv6 >> Option.isSome)

let private isIpv4CompatFn =
    addressPredicate (fun value ->
        let bytes = packedAddressBytes value

        hasIpv6Length bytes
        && bytes.[0..11] |> Array.forall ((=) 0uy)
        && bytes.[12..15] <> [| 0uy; 0uy; 0uy; 0uy |]
        && bytes.[12..15] <> [| 0uy; 0uy; 0uy; 1uy |])

let private isIpv4MappedFn =
    addressPredicate (fun value ->
        let bytes = packedAddressBytes value
        hasIpv6Length bytes && bytes.[0..9] |> Array.forall ((=) 0uy) && bytes.[10] = 0xffuy && bytes.[11] = 0xffuy)

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

let private bitAndAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (&&&))
let private bitOrAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (|||))
let private bitXorAgg: Aggregate = fun vs -> VInt(vs |> List.map (toDouble >> int64) |> List.reduce (^^^))

let internal tryEmptyAggregate (name: string) =
    match name.ToUpperInvariant() with
    | "BIT_AND" -> Some(VUInt UInt64.MaxValue)
    | "BIT_OR"
    | "BIT_XOR" -> Some(VInt 0L)
    | _ -> None

// ---------------------------------------------------------------------------
// MySQL 9 VECTOR. A vector is a `VBytes` of little-endian 4-byte floats —
// no new `Value` case, so storage/persistence/wire all carry it as the
// binary string real pre-9 clients see anyway. Only these
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

let private geometryError functionName detail : 'a =
    raise (SqlError(3037, sprintf "Invalid GIS data provided to function %s: %s" functionName detail))

let private geometryArgument functionName = function
    | VGeometry geometry -> geometry
    | _ -> geometryError functionName "a geometry argument is required"

let private geometrySrid functionName = function
    | VInt srid when srid >= 0L && srid <= int64 Int32.MaxValue -> int srid
    | VUInt srid when srid <= uint64 Int32.MaxValue -> int srid
    | _ -> geometryError functionName "the SRID must be a non-negative integer"

let private requirePlanar (functionName: string) (geometry: Geometry) =
    if geometry.Srid <> 0 then
        raise (SqlError(1235, sprintf "This version of MySQL doesn't yet support '%s with nonzero SRIDs'" functionName))

    geometry

let private requireSamePlanarSrid (functionName: string) (first: Geometry) (second: Geometry) =
    if first.Srid <> second.Srid then
        raise (SqlError(3033, sprintf "Binary geometry function %s given two geometries of different SRIDs: %d and %d, which should have been identical." (functionName.ToLowerInvariant()) first.Srid second.Srid))

    requirePlanar functionName first |> ignore
    first, second

let private geometryFromTextFn requiredKind functionName: Scalar =
    function
    | [ VNull ]
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ value ]
    | [ value; VInt 0L ] ->
        match tryGeometryFromText 0 (req value) with
        | Some geometry when requiredKind = Geometry || geometryKind geometry.Shape = requiredKind -> VGeometry geometry
        | Some _ -> geometryError functionName (sprintf "%s is not a %s" (req value) (geometryTypeName requiredKind))
        | None -> geometryError functionName (sprintf "'%s'" (req value))
    | [ value; sridValue ] ->
        let srid = geometrySrid functionName sridValue

        match tryGeometryFromText srid (req value) with
        | Some geometry when requiredKind = Geometry || geometryKind geometry.Shape = requiredKind -> VGeometry geometry
        | Some _ -> geometryError functionName (sprintf "%s is not a %s" (req value) (geometryTypeName requiredKind))
        | None -> geometryError functionName (sprintf "'%s'" (req value))
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" functionName))

let private geometryFromWkbFn requiredKind functionName: Scalar =
    function
    | [ VNull ]
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ VBytes bytes ]
    | [ VBytes bytes; VInt 0L ] ->
        match tryGeometryFromWkb 0 bytes with
        | Some geometry when requiredKind = Geometry || geometryKind geometry.Shape = requiredKind -> VGeometry geometry
        | Some _ -> geometryError functionName "the WKB geometry has a different type"
        | None -> geometryError functionName "invalid WKB"
    | [ VBytes bytes; sridValue ] ->
        let srid = geometrySrid functionName sridValue

        match tryGeometryFromWkb srid bytes with
        | Some geometry when requiredKind = Geometry || geometryKind geometry.Shape = requiredKind -> VGeometry geometry
        | Some _ -> geometryError functionName "the WKB geometry has a different type"
        | None -> geometryError functionName "invalid WKB"
    | [ _ ]
    | [ _; _ ] -> geometryError functionName "a binary WKB argument is required"
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" functionName))

let private geometryToTextFn functionName: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] -> geometryArgument functionName value |> geometryToText |> VString
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" functionName))

let private geometryToWkbFn functionName: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] -> geometryArgument functionName value |> geometryToWkb |> VBytes
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" functionName))

let private geometryTypeFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match geometryArgument "ST_GEOMETRYTYPE" value |> _.Shape |> geometryKind with
        | GeometryCollection -> VString "GEOMCOLLECTION"
        | kind -> VString(geometryTypeName kind)
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_geometrytype'"))

let private geometryDimensionFn: Scalar =
    let rec dimension = function
        | GEmpty -> -1
        | GPoint _
        | GMultiPoint _ -> 0
        | GLineString _
        | GMultiLineString _ -> 1
        | GPolygon _
        | GMultiPolygon _ -> 2
        | GGeometryCollection geometries -> geometries |> List.map (fun geometry -> dimension geometry.Shape) |> List.append [ -1 ] |> List.max

    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match (geometryArgument "ST_DIMENSION" value).Shape with
        | GEmpty -> VNull
        | shape -> VInt(int64 (dimension shape))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_dimension'"))

let private geometryIsEmptyFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match geometryArgument "ST_ISEMPTY" value with
        | { Shape = GEmpty } -> VInt 1L
        | _ -> VInt 0L
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_isempty'"))

let private geometryIsValidFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        let geometry = geometryArgument "ST_ISVALID" value |> requirePlanar "ST_ISVALID"
        VInt(if geometryIsValidPlanar geometry then 1L else 0L)
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_isvalid'"))

let private pointCoordinateFn functionName select: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] ->
        match (geometryArgument functionName value).Shape with
        | GPoint(x, y) -> VDouble(select x y)
        | _ -> geometryError functionName "a Point argument is required"
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" functionName))

let private geometrySridFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] -> geometryArgument "ST_SRID" value |> fun geometry -> VInt(int64 geometry.Srid)
    | [ _; _ ] -> raise (SqlError(1235, "This version of MySQL doesn't yet support 'ST_SRID geometry mutation'"))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_srid'"))

let private geometryDistanceFn: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ first; second ] ->
        let first, second = requireSamePlanarSrid "ST_DISTANCE" (geometryArgument "ST_DISTANCE" first) (geometryArgument "ST_DISTANCE" second)
        geometryDistancePlanar first second |> Option.map VDouble |> Option.defaultValue VNull
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_distance'"))

let private geometryEnvelopeFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] -> geometryArgument "ST_ENVELOPE" value |> requirePlanar "ST_ENVELOPE" |> geometryEnvelope |> VGeometry
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_envelope'"))

let private geometryConvexHullFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ value ] -> geometryArgument "ST_CONVEXHULL" value |> requirePlanar "ST_CONVEXHULL" |> geometryConvexHullPlanar |> VGeometry
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_convexhull'"))

let private geometryBufferFn: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ value; distanceValue ] ->
        let geometry = geometryArgument "ST_BUFFER" value |> requirePlanar "ST_BUFFER"
        let distance = toDouble distanceValue

        if distance < 0.0 || not (Double.IsFinite distance) then
            raise (SqlError(1210, "Incorrect arguments to st_buffer"))

        match geometryPointBufferPlanar distance geometry with
        | Some buffer -> VGeometry buffer
        | None when geometry.Shape |> geometryKind = Point -> raise (SqlError(1210, "Incorrect arguments to st_buffer"))
        | None -> raise (SqlError(1235, "This version of MySQL doesn't yet support 'ST_BUFFER for non-point geometries'"))
    | [ _; _; _ ] -> raise (SqlError(1235, "This version of MySQL doesn't yet support 'ST_BUFFER strategies'"))
    | _ -> raise (SqlError(1582, "Incorrect parameter count in the call to native function 'st_buffer'"))

let private geometryRelationFn functionName project: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ first; second ] ->
        let first, second = requireSamePlanarSrid functionName (geometryArgument functionName first) (geometryArgument functionName second)

        geometryIntersectsPlanar first second
        |> Option.map (project >> fun value -> VInt(if value then 1L else 0L))
        |> Option.defaultValue VNull
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" (functionName.ToLowerInvariant())))

let private geometryPredicateFn functionName predicate: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ first; second ] ->
        let first, second = requireSamePlanarSrid functionName (geometryArgument functionName first) (geometryArgument functionName second)

        predicate first second
        |> Option.map (fun value -> VInt(if value then 1L else 0L))
        |> Option.defaultValue VNull
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" (functionName.ToLowerInvariant())))

let private mbrContains first second =
    match geometryBounds first, geometryBounds second with
    | Some outer, Some inner ->
        let contains minimum maximum innerMinimum innerMaximum =
            if minimum = maximum then
                innerMinimum = innerMaximum && minimum = innerMinimum
            elif innerMinimum = innerMaximum then
                minimum < innerMinimum && innerMinimum < maximum
            else
                minimum <= innerMinimum && innerMaximum <= maximum

        contains outer.MinX outer.MaxX inner.MinX inner.MaxX
        && contains outer.MinY outer.MaxY inner.MinY inner.MaxY
    | _ -> false

let private mbrIntersects first second =
    match geometryBounds first, geometryBounds second with
    | Some first, Some second ->
        first.MinX <= second.MaxX
        && second.MinX <= first.MaxX
        && first.MinY <= second.MaxY
        && second.MinY <= first.MaxY
    | _ -> false

let private mbrPredicateFn functionName predicate: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ first; second ] ->
        let first, second = requireSamePlanarSrid functionName (geometryArgument functionName first) (geometryArgument functionName second)

        match geometryBounds first, geometryBounds second with
        | None, _
        | _, None -> VNull
        | Some _, Some _ -> VInt(if predicate first second then 1L else 0L)
    | _ -> raise (SqlError(1582, sprintf "Incorrect parameter count in the call to native function '%s'" (functionName.ToLowerInvariant())))

let private everyArgument _ = true
let private firstArgument index = index = 0
let private arguments positions index = Set.contains index positions
let private argumentsAfter position index = index > position

let builtins: Registry =
    empty
    |> registerScalar "NOW" nowFn
    |> registerScalar "CURRENT_TIMESTAMP" nowFn
    |> registerStringScalar "CONCAT" everyArgument (CombineArguments everyArgument) concatFn
    |> registerStringScalar "UPPER" firstArgument (InheritArgument 0) (textMap VBytes (fun s -> s.ToUpperInvariant()))
    |> registerStringScalar "UCASE" firstArgument (InheritArgument 0) (textMap VBytes (fun s -> s.ToUpperInvariant()))
    |> registerStringScalar "LOWER" firstArgument (InheritArgument 0) (textMap VBytes (fun s -> s.ToLowerInvariant()))
    |> registerStringScalar "LCASE" firstArgument (InheritArgument 0) (textMap VBytes (fun s -> s.ToLowerInvariant()))
    |> registerByteTextScalar "LENGTH" firstArgument lengthFn
    |> registerByteTextScalar "OCTET_LENGTH" firstArgument lengthFn
    |> registerByteTextScalar "BIT_LENGTH" firstArgument bitLengthFn
    |> registerTextScalar "CHAR_LENGTH" firstArgument charLengthFn
    |> registerTextScalar "CHARACTER_LENGTH" firstArgument charLengthFn
    |> registerScalarResult "COALESCE" (CombineArguments everyArgument) coalesceFn
    |> registerScalarResult "IFNULL" (CombineArguments everyArgument) ifNullFn
    |> registerScalarResult "IF" (CombineArguments (arguments (set [ 1; 2 ]))) ifFn
    |> registerScalar "ABS" absFn
    |> registerScalar "ROUND" roundFn
    |> registerScalar "MOD" modFn
    // JSON
    |> registerScalarResult "JSON_EXTRACT" jsonResult jsonExtractFn
    |> registerScalarResult "JSON_VALUE" (FixedCollation("utf8mb4_0900_bin", 4)) jsonValueFn
    |> registerScalarResult "JSON_UNQUOTE" jsonTextResult jsonUnquoteFn
    |> registerScalar "JSON_CONTAINS" jsonContainsFn
    |> registerScalar "JSON_MEMBER_OF" jsonMemberOfFn
    |> registerScalar "JSON_CONTAINS_PATH" jsonContainsPathFn
    |> registerScalar "JSON_OVERLAPS" jsonOverlapsFn
    |> registerScalarResult "JSON_QUOTE" jsonTextResult jsonQuoteFn
    |> registerScalarResult "JSON_PRETTY" jsonTextResult jsonPrettyFn
    |> registerScalarResult "JSON_MERGE_PATCH" jsonResult (jsonMergeFn mergeJsonPatch)
    |> registerScalarResult "JSON_MERGE_PRESERVE" jsonResult (jsonMergeFn mergeJsonPreserve)
    |> registerScalarResult "JSON_ARRAY_APPEND" jsonResult jsonArrayAppendFn
    |> registerScalarResult "JSON_ARRAY_INSERT" jsonResult jsonArrayInsertFn
    |> registerScalar "JSON_STORAGE_SIZE" jsonStorageSizeFn
    |> registerScalar "JSON_STORAGE_FREE" jsonStorageFreeFn
    |> registerScalarResult "JSON_SET" jsonResult (jsonWriteFn JSet)
    |> registerScalarResult "JSON_INSERT" jsonResult (jsonWriteFn JInsert)
    |> registerScalarResult "JSON_REPLACE" jsonResult (jsonWriteFn JReplace)
    |> registerScalarResult "JSON_REMOVE" jsonResult jsonRemoveFn
    |> registerScalarResult "JSON_ARRAY" jsonResult jsonArrayFn
    |> registerScalarResult "JSON_OBJECT" jsonResult jsonObjectFn
    |> registerScalar "JSON_LENGTH" jsonLengthFn
    |> registerScalar "JSON_DEPTH" jsonDepthFn
    |> registerScalar "JSON_VALID" jsonValidFn
    |> registerScalar "JSON_SCHEMA_VALID" jsonSchemaValidFn
    |> registerScalarResult "JSON_SCHEMA_VALIDATION_REPORT" jsonResult jsonSchemaValidationReportFn
    |> registerScalarResult "JSON_TYPE" jsonTextResult jsonTypeFn
    |> registerScalarResult "JSON_KEYS" jsonResult jsonKeysFn
    |> registerScalarResult "JSON_SEARCH" jsonResult jsonSearchFn
    |> registerScalarResult "WEIGHT_STRING" binaryResult weightStringFn
    |> registerScalarResult "ST_GEOMFROMTEXT" binaryResult (geometryFromTextFn Geometry "ST_GeomFromText")
    |> registerScalarResult "ST_GEOMETRYFROMTEXT" binaryResult (geometryFromTextFn Geometry "ST_GeometryFromText")
    |> registerScalarResult "GEOMFROMTEXT" binaryResult (geometryFromTextFn Geometry "GeomFromText")
    |> registerScalarResult "GEOMETRYFROMTEXT" binaryResult (geometryFromTextFn Geometry "GeometryFromText")
    |> registerScalarResult "ST_POINTFROMTEXT" binaryResult (geometryFromTextFn Point "ST_PointFromText")
    |> registerScalarResult "POINTFROMTEXT" binaryResult (geometryFromTextFn Point "PointFromText")
    |> registerScalarResult "ST_LINESTRINGFROMTEXT" binaryResult (geometryFromTextFn LineString "ST_LineStringFromText")
    |> registerScalarResult "ST_POLYGONFROMTEXT" binaryResult (geometryFromTextFn Polygon "ST_PolygonFromText")
    |> registerScalarResult "ST_GEOMFROMWKB" binaryResult (geometryFromWkbFn Geometry "ST_GeomFromWKB")
    |> registerScalarResult "ST_GEOMETRYFROMWKB" binaryResult (geometryFromWkbFn Geometry "ST_GeometryFromWKB")
    |> registerScalarResult "GEOMFROMWKB" binaryResult (geometryFromWkbFn Geometry "GeomFromWKB")
    |> registerScalarResult "ST_POINTFROMWKB" binaryResult (geometryFromWkbFn Point "ST_PointFromWKB")
    |> registerScalar "ST_ASTEXT" (geometryToTextFn "ST_AsText")
    |> registerScalar "ST_ASWKT" (geometryToTextFn "ST_AsWKT")
    |> registerScalar "ASTEXT" (geometryToTextFn "AsText")
    |> registerScalarResult "ST_ASWKB" binaryResult (geometryToWkbFn "ST_AsWKB")
    |> registerScalarResult "ST_ASBINARY" binaryResult (geometryToWkbFn "ST_AsBinary")
    |> registerScalarResult "ASBINARY" binaryResult (geometryToWkbFn "AsBinary")
    |> registerScalar "ST_SRID" geometrySridFn
    |> registerScalar "ST_GEOMETRYTYPE" geometryTypeFn
    |> registerScalar "GEOMETRYTYPE" geometryTypeFn
    |> registerScalar "ST_DIMENSION" geometryDimensionFn
    |> registerScalar "DIMENSION" geometryDimensionFn
    |> registerScalar "ST_ISEMPTY" geometryIsEmptyFn
    |> registerScalar "ST_ISVALID" geometryIsValidFn
    |> registerScalar "ISEMPTY" geometryIsEmptyFn
    |> registerScalar "ST_X" (pointCoordinateFn "ST_X" (fun x _ -> x))
    |> registerScalar "ST_Y" (pointCoordinateFn "ST_Y" (fun _ y -> y))
    |> registerScalar "X" (pointCoordinateFn "X" (fun x _ -> x))
    |> registerScalar "Y" (pointCoordinateFn "Y" (fun _ y -> y))
    |> registerScalar "ST_DISTANCE" geometryDistanceFn
    |> registerScalar "ST_EQUALS" (geometryPredicateFn "ST_EQUALS" geometryEqualsPlanar)
    |> registerScalar "ST_CONTAINS" (geometryPredicateFn "ST_CONTAINS" geometryContainsPlanar)
    |> registerScalar "ST_WITHIN" (geometryPredicateFn "ST_WITHIN" (fun first second -> geometryContainsPlanar second first))
    |> registerScalar "ST_INTERSECTS" (geometryRelationFn "ST_INTERSECTS" id)
    |> registerScalar "ST_DISJOINT" (geometryRelationFn "ST_DISJOINT" not)
    |> registerScalar "ST_TOUCHES" (geometryPredicateFn "ST_TOUCHES" geometryTouchesPlanar)
    |> registerScalarResult "ST_BUFFER" binaryResult geometryBufferFn
    |> registerScalarResult "ST_CONVEXHULL" binaryResult geometryConvexHullFn
    |> registerScalarResult "ST_ENVELOPE" binaryResult geometryEnvelopeFn
    |> registerScalar "MBRCONTAINS" (mbrPredicateFn "MBRCONTAINS" mbrContains)
    |> registerScalar "MBRWITHIN" (mbrPredicateFn "MBRWITHIN" (fun first second -> mbrContains second first))
    |> registerScalar "MBRINTERSECTS" (mbrPredicateFn "MBRINTERSECTS" mbrIntersects)
    // Dates
    |> registerScalar "DATE_ADD" (dateAddCore 1.0)
    |> registerScalar "TIMESTAMPADD" timestampAddFn
    |> registerScalar "ADDDATE" (addSubDateCore 1.0)
    |> registerScalar "DATE_SUB" (dateAddCore -1.0)
    |> registerScalar "SUBDATE" (addSubDateCore -1.0)
    |> registerScalar "INTERVAL" intervalFn
    |> registerScalar "DATEDIFF" dateDiffFn
    |> registerScalar "DATE_FORMAT" (dateFormatFn defaultTimeLocale)
    |> registerTextScalar "CONVERT" firstArgument convertFn
    |> registerScalar "DATE" dateFn
    |> registerScalar "TIME" timeFn
    |> registerScalar "TIMESTAMP" timestampFn
    |> registerScalar "YEAR" (zeroAwareDatePart (fun date -> let year, _, _ = zeroDateParts date in year) (fun d -> d.Year))
    |> registerScalar "MONTH" (zeroAwareDatePart (fun date -> let _, month, _ = zeroDateParts date in month) (fun d -> d.Month))
    |> registerScalar "DAY" (zeroAwareDatePart (fun date -> let _, _, day = zeroDateParts date in day) (fun d -> d.Day))
    |> registerScalar "DAYOFMONTH" (zeroAwareDatePart (fun date -> let _, _, day = zeroDateParts date in day) (fun d -> d.Day))
    |> registerScalar "HOUR" (zeroAwareTimePart (fun dateTime -> let _, hour, _, _, _ = zeroDateTimeParts dateTime in hour) timeHour (fun d -> d.Hour))
    |> registerScalar "MINUTE" (zeroAwareTimePart (fun dateTime -> let _, _, minute, _, _ = zeroDateTimeParts dateTime in minute) timeMinute (fun d -> d.Minute))
    |> registerScalar "SECOND" (zeroAwareTimePart (fun dateTime -> let _, _, _, second, _ = zeroDateTimeParts dateTime in second) timeSecond (fun d -> d.Second))
    |> registerScalar "MICROSECOND" (zeroAwareTimePart (fun dateTime -> let _, _, _, _, microseconds = zeroDateTimeParts dateTime in microseconds) timeMicroseconds (fun d -> int (d.Ticks % TimeSpan.TicksPerSecond / 10L)))
    |> registerScalar "DAYOFWEEK" (datePartFn (fun d -> int d.DayOfWeek + 1))
    |> registerScalar "DAYOFYEAR" (datePartFn (fun d -> d.DayOfYear))
    |> registerScalar "DAYNAME" (dayNameFn defaultTimeLocale)
    |> registerScalar "MONTHNAME" (monthNameFn defaultTimeLocale)
    |> registerScalar "WEEK" (weekFn 0)
    |> registerScalar "WEEKDAY" weekdayFn
    |> registerScalar "WEEKOFYEAR" weekOfYearFn
    |> registerScalar "YEARWEEK" yearWeekFn
    |> registerScalar "QUARTER" (datePartFn (fun d -> (d.Month - 1) / 3 + 1))
    |> registerScalar "CURDATE" curDateFn
    |> registerScalar "CURRENT_DATE" curDateFn
    |> registerScalar "CURTIME" curTimeFn
    |> registerScalar "CURRENT_TIME" curTimeFn
    |> registerScalar "LOCALTIME" nowFn
    |> registerScalar "LOCALTIMESTAMP" nowFn
    |> registerScalar "SYSDATE" nowFn
    |> registerScalar "UTC_DATE" utcDateFn
    |> registerScalar "UTC_TIME" utcTimeFn
    |> registerScalar "UTC_TIMESTAMP" utcTimestampFn
    |> registerScalar "ADDTIME" (addTimeFn 1L)
    |> registerScalar "SUBTIME" (addTimeFn -1L)
    |> registerScalar "TIMEDIFF" timeDiffFn
    |> registerScalar "SEC_TO_TIME" secToTimeFn
    |> registerScalar "MAKETIME" makeTimeFn
    |> registerScalar "TIME_FORMAT" timeFormatFn
    |> registerScalar "GET_FORMAT" getFormatFn
    |> registerScalar "PERIOD_ADD" periodAddFn
    |> registerScalar "PERIOD_DIFF" periodDiffFn
    |> registerScalar "FROM_DAYS" fromDaysFn
    |> registerScalar "TO_DAYS" toDaysFn
    |> registerScalar "UNIX_TIMESTAMP" unixTimestampFn
    |> registerScalar "FROM_UNIXTIME" (fromUnixTimeFn defaultTimeLocale)
    |> registerScalar "TIMESTAMPDIFF" timestampDiffFn
    |> registerScalar "EXTRACT" extractFn
    |> registerScalar "LAST_DAY" lastDayFn
    |> registerScalar "MAKEDATE" makeDateFn
    |> registerScalar "CONVERT_TZ" convertTzFn
    |> registerScalar "STR_TO_DATE" strToDateFn
    // Strings
    |> registerStringScalar "SUBSTRING" firstArgument (InheritArgument 0) substringFn
    |> registerStringScalar "SUBSTR" firstArgument (InheritArgument 0) substringFn
    |> registerStringScalar "MID" firstArgument (InheritArgument 0) substringFn
    |> registerTextScalar "LOCATE" (arguments (set [ 0; 1 ])) locateFn
    |> registerTextScalar "INSTR" everyArgument instrFn
    |> registerTextScalar "POSITION" everyArgument locateFn
    |> registerStringScalar "REPLACE" everyArgument (InheritArgument 0) replaceFn
    |> registerStringScalar "INSERT" everyArgument (InheritArgument 0) insertStringFn
    |> registerStringScalar "TRIM" firstArgument (InheritArgument 0) (textMap (trimRaw true true) (fun s -> s.Trim()))
    |> registerStringScalar "TRIM_BOTH" everyArgument (InheritArgument 1) (trimSubstring true true)
    |> registerStringScalar "TRIM_LEADING" everyArgument (InheritArgument 1) (trimSubstring true false)
    |> registerStringScalar "TRIM_TRAILING" everyArgument (InheritArgument 1) (trimSubstring false true)
    |> registerStringScalar "LTRIM" firstArgument (InheritArgument 0) (textMap (trimRaw true false) (fun s -> s.TrimStart()))
    |> registerStringScalar "RTRIM" firstArgument (InheritArgument 0) (textMap (trimRaw false true) (fun s -> s.TrimEnd()))
    |> registerStringScalar "LPAD" (arguments (set [ 0; 2 ])) (InheritArgument 0) (padFn true)
    |> registerStringScalar "RPAD" (arguments (set [ 0; 2 ])) (InheritArgument 0) (padFn false)
    |> registerStringScalar "LEFT" firstArgument (InheritArgument 0) leftFn
    |> registerStringScalar "RIGHT" firstArgument (InheritArgument 0) rightFn
    |> registerStringScalar "REVERSE" firstArgument (InheritArgument 0) (textMap (Array.rev >> VBytes) (fun s -> String(Array.rev (s.ToCharArray()))))
    |> registerStringScalar "REPEAT" firstArgument (InheritArgument 0) repeatFn
    |> registerScalar "SPACE" spaceFn
    |> registerTextScalar "ASCII" firstArgument asciiFn
    |> registerTextScalar "ORD" firstArgument ordFn
    |> registerScalarResult "CHAR" binaryResult charFn
    |> registerByteScalar "HEX" firstArgument hexFn
    |> registerStringScalar "UNHEX" firstArgument binaryResult unhexFn
    |> registerStringScalar "AES_ENCRYPT" (arguments (set [ 0; 1 ])) binaryResult (aesEncrypt "aes-128-ecb")
    |> registerStringScalar "AES_DECRYPT" (arguments (set [ 0; 1 ])) binaryResult (aesDecrypt "aes-128-ecb")
    |> registerByteTextScalar "MD5" firstArgument md5Fn
    |> registerByteTextScalar "SHA1" firstArgument sha1Fn
    |> registerByteTextScalar "SHA" firstArgument sha1Fn
    |> registerByteTextScalar "SHA2" firstArgument sha2Fn
    |> registerScalar "FORMAT" formatFn
    |> registerStringScalar "SUBSTRING_INDEX" (arguments (set [ 0; 1 ])) (InheritArgument 0) substringIndexFn
    |> registerStringScalar "CONCAT_WS" everyArgument (CombineArguments everyArgument) concatWsFn
    |> registerStringScalar "ELT" (argumentsAfter 0) (CombineArguments (argumentsAfter 0)) eltFn
    |> registerStringScalar "EXPORT_SET" (arguments (set [ 1; 2; 3 ])) (CombineArguments (arguments (set [ 1; 2; 3 ]))) exportSetFn
    |> registerStringScalar "MAKE_SET" (argumentsAfter 0) (CombineArguments (argumentsAfter 0)) makeSetFn
    |> registerScalar "FIELD" fieldFn
    |> registerTextScalar "FIND_IN_SET" everyArgument findInSetFn
    |> registerStringScalar "QUOTE" firstArgument (InheritArgument 0) quoteFn
    |> registerTextScalar "STRCMP" everyArgument strcmpFn
    |> registerStringScalar "SOUNDEX" firstArgument (InheritArgument 0) soundexFn
    |> registerByteTextScalar "TO_BASE64" firstArgument toBase64Fn
    |> registerStringScalar "FROM_BASE64" firstArgument binaryResult fromBase64Fn
    |> registerByteStringScalar "COMPRESS" firstArgument binaryResult compressFn
    |> registerStringScalar "UNCOMPRESS" firstArgument binaryResult uncompressFn
    |> registerByteTextScalar "UNCOMPRESSED_LENGTH" firstArgument uncompressedLengthFn
    |> registerScalarResult "RANDOM_BYTES" binaryResult randomBytesFn
    |> registerScalar "UUID_SHORT" uuidShortFn
    |> registerScalarResult "NAME_CONST" (InheritArgument 1) nameConstFn
    |> registerScalar "REGEXP_LIKE" (regexpFunction "REGEXP_LIKE" Collation.defaultCollation |> Option.get)
    |> registerScalarResult "REGEXP_REPLACE" (InheritArgument 0) (regexpFunction "REGEXP_REPLACE" Collation.defaultCollation |> Option.get)
    |> registerScalarResult "REGEXP_SUBSTR" (InheritArgument 0) (regexpFunction "REGEXP_SUBSTR" Collation.defaultCollation |> Option.get)
    |> registerScalar "REGEXP_INSTR" (regexpFunction "REGEXP_INSTR" Collation.defaultCollation |> Option.get)
    // Math/misc
    |> registerScalar "CEIL" ceilFn
    |> registerScalar "CEILING" ceilFn
    |> registerScalar "FLOOR" floorFn
    |> registerScalar "POW" powFn
    |> registerScalar "POWER" powFn
    |> registerScalar "SQRT" sqrtFn
    |> registerScalar "LOG" logFn
    |> registerScalar "LN" (positiveLog "ln" Math.Log)
    |> registerScalar "LOG2" (positiveLog "log2" Math.Log2)
    |> registerScalar "LOG10" (positiveLog "log10" Math.Log10)
    |> registerScalar "EXP" (unaryMath "exp" Math.Exp)
    |> registerScalar "PI" piFn
    |> registerScalar "SIN" (unaryMath "sin" Math.Sin)
    |> registerScalar "COS" (unaryMath "cos" Math.Cos)
    |> registerScalar "TAN" (unaryMath "tan" Math.Tan)
    |> registerScalar "COT" cotFn
    |> registerScalar "ASIN" (unaryMath "asin" Math.Asin)
    |> registerScalar "ACOS" (unaryMath "acos" Math.Acos)
    |> registerScalar "ATAN" atanFn
    |> registerScalar "ATAN2" atan2Fn
    |> registerScalar "DEGREES" (unaryMath "degrees" (fun value -> value * 180.0 / Math.PI))
    |> registerScalar "RADIANS" (unaryMath "radians" (fun value -> value * Math.PI / 180.0))
    |> registerScalar "SIGN" signFn
    |> registerScalar "TRUNCATE" truncateFn
    |> registerScalar "RAND" randFn
    |> registerScalarResult "GREATEST" (CombineArguments everyArgument) greatestFn
    |> registerScalarResult "LEAST" (CombineArguments everyArgument) leastFn
    |> registerScalarResult "NULLIF" (InheritArgument 0) nullIfFn
    |> registerScalarResult "ANY_VALUE" (InheritArgument 0) anyValueFn
    |> registerScalar "ISNULL" isNullFn
    |> registerScalar "CONV" convFn
    |> registerScalar "BIN" binFn
    |> registerScalar "BIT_COUNT" bitCountFn
    |> registerScalar "BITWISE_NOT" (bitwiseUnary (~~~))
    |> registerScalar "BITWISE_AND" (bitwiseBinary (&&&))
    |> registerScalar "BITWISE_OR" (bitwiseBinary (|||))
    |> registerScalar "BITWISE_XOR" (bitwiseBinary (^^^))
    |> registerScalar "BITWISE_SHIFT_LEFT" (bitwiseShift (fun value count -> value <<< count))
    |> registerScalar "BITWISE_SHIFT_RIGHT" (bitwiseShift (fun value count -> value >>> count))
    |> registerScalar "OCT" octFn
    |> registerByteTextScalar "CRC32" firstArgument crc32Fn
    |> registerScalar "UUID" uuidFn
    |> registerScalarResult "UUID_TO_BIN" binaryResult uuidToBinFn
    |> registerScalar "BIN_TO_UUID" binToUuidFn
    |> registerScalar "IS_UUID" isUuidFn
    |> registerScalar "INET_ATON" inetAtonFn
    |> registerScalar "INET_NTOA" inetNtoaFn
    |> registerScalarResult "INET6_ATON" binaryResult inet6AtonFn
    |> registerScalar "INET6_NTOA" inet6NtoaFn
    |> registerScalar "IS_IPV4" isIpv4Fn
    |> registerScalar "IS_IPV6" isIpv6Fn
    |> registerScalar "IS_IPV4_COMPAT" isIpv4CompatFn
    |> registerScalar "IS_IPV4_MAPPED" isIpv4MappedFn
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

let internal isUnmodifiedBuiltinAggregate (name: string) (registry: Registry) =
    match lookupAggregate name builtins, lookupAggregate name registry with
    | Some builtin, Some current -> obj.ReferenceEquals(builtin, current)
    | _ -> false

let internal isUnmodifiedBuiltinScalar (name: string) (registry: Registry) =
    match lookup name builtins, lookup name registry with
    | Some builtin, Some current -> obj.ReferenceEquals(builtin, current)
    | _ -> false
