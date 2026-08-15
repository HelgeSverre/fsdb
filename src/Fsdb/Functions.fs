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
      Aggregates: Map<string, Aggregate> }

let empty: Registry = { Scalars = Map.empty; Aggregates = Map.empty }

let registerScalar (name: string) (fn: Scalar) (registry: Registry) : Registry =
    { registry with Scalars = Map.add (name.ToUpperInvariant()) fn registry.Scalars }

let registerAggregate (name: string) (fn: Aggregate) (registry: Registry) : Registry =
    { registry with Aggregates = Map.add (name.ToUpperInvariant()) fn registry.Aggregates }

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

let private lengthFn: Scalar =
    function
    | [ VNull ] -> VNull
    | [ v ] -> v |> toText |> Option.defaultValue "" |> String.length |> int64 |> VInt
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
    | [ VDouble d ] -> VDouble(abs d)
    | [ VDecimal d ] -> VDecimal(abs d)
    | [ v ] -> VDouble(abs (toDouble v))
    | _ -> VNull

/// ROUND(x) rounds to the nearest integer; ROUND(x, n) to `n` decimal
/// places, matching (roughly) MySQL's half-away-from-zero rounding.
let private roundFn: Scalar =
    function
    | [ VNull ]
    | [ VNull; _ ] -> VNull
    | [ VInt i ] -> VInt i
    | [ VInt i; _ ] -> VInt i
    | [ VDecimal d ] -> VDecimal(Math.Round(d, MidpointRounding.AwayFromZero))
    | [ VDecimal d; VInt digits ] -> VDecimal(Math.Round(d, int digits, MidpointRounding.AwayFromZero))
    | [ VDouble d ] -> VDouble(Math.Round(d, MidpointRounding.AwayFromZero))
    | [ VDouble d; VInt digits ] -> VDouble(Math.Round(d, int digits, MidpointRounding.AwayFromZero))
    | [ v ] -> VDouble(Math.Round(toDouble v, MidpointRounding.AwayFromZero))
    | [ v; VInt digits ] -> VDouble(Math.Round(toDouble v, int digits, MidpointRounding.AwayFromZero))
    | _ -> VNull

let private modFn: Scalar =
    function
    | [ VNull; _ ]
    | [ _; VNull ] -> VNull
    | [ VInt a; VInt b ] -> if b = 0L then VNull else VInt(a % b)
    | [ a; b ] ->
        let db = toDouble b
        if db = 0.0 then VNull else VDouble(toDouble a % db)
    | _ -> VNull

let private nowFn: Scalar = fun _ -> VDateTime DateTime.Now

// ---------------------------------------------------------------------------
// JSON. `VJson`/`VString` both hold raw JSON text (a JSON column coerces to
// `VString` today — `Storage.coerceValue`'s call, not this module's — so
// every function here reads through `tryParseJsonValue`, which treats the
// two the same rather than special-casing `VJson`). Parsed on demand with
// `System.Text.Json.Nodes.JsonNode`, whose object/array nodes are mutable in
// place, which is what makes JSON_SET/INSERT/REPLACE/REMOVE tractable
// without hand-rolling a second JSON tree type.
// ---------------------------------------------------------------------------

/// One step of a `$.a[2].b`-style path. `Wildcard` is the minimal `$.*`
/// case: one level, not a recursive descent operator.
type private JPath =
    | JKey of string
    | JIndex of int
    | JWildcard

/// Parses MySQL's JSON path grammar: `$`, then any run of `.key`,
/// `."quoted key"`, `[n]`, `.*`, or `[*]`. Returns `None` on anything it
/// doesn't recognize rather than guessing.
let private parseJsonPath (path: string) : JPath list option =
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
                    segs.Add JWildcard
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
                        segs.Add JWildcard
                    else
                        match Int32.TryParse(inner, NumberStyles.Integer, CultureInfo.InvariantCulture) with
                        | true, n -> segs.Add(JIndex n)
                        | false, _ -> ok <- false

                    i <- close + 1
            | _ -> ok <- false

        if ok then Some(List.ofSeq segs) else None

/// Walks `node` along `segs`, returning every match (more than one only
/// when a `JWildcard` segment fans out). A found JSON `null` is a valid
/// match — represented by a `null` `JsonNode` reference in the list — so
/// callers distinguish "found null" (`[null]`) from "not found" (`[]`).
let rec private navigateJson (node: JsonNode) (segs: JPath list) : JsonNode list =
    match segs with
    | [] -> [ node ]
    | JWildcard :: rest ->
        match node with
        | :? JsonObject as o -> o |> Seq.collect (fun kv -> navigateJson kv.Value rest) |> List.ofSeq
        | :? JsonArray as a -> a |> Seq.collect (fun v -> navigateJson v rest) |> List.ofSeq
        | _ -> []
    | JKey k :: rest ->
        match node with
        | :? JsonObject as o -> if o.ContainsKey k then navigateJson o.[k] rest else []
        | _ -> []
    | JIndex idx :: rest ->
        match node with
        | :? JsonArray as a ->
            let i = if idx < 0 then a.Count + idx else idx
            if i >= 0 && i < a.Count then navigateJson a.[i] rest else []
        | _ -> []

/// Renders a `JsonNode` the way MySQL's JSON printer does: a space after
/// every `:` and `,`, recursively. (`JsonNode.ToJsonString()` alone is
/// compact with no spaces, which reads as JSON but not as *MySQL's* JSON.)
let rec private formatJsonNode (node: JsonNode) : string =
    match node with
    | null -> "null"
    | :? JsonObject as o ->
        "{"
        + (o
           |> Seq.map (fun kv -> JsonSerializer.Serialize kv.Key + ": " + formatJsonNode kv.Value)
           |> String.concat ", ")
        + "}"
    | :? JsonArray as a -> "[" + (a |> Seq.map formatJsonNode |> String.concat ", ") + "]"
    | _ -> node.ToJsonString()

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

let private jsonKeysOf (node: JsonNode option) : Value =
    match node with
    | Some(:? JsonObject as o) -> VJson("[" + (o |> Seq.map (fun kv -> JsonSerializer.Serialize kv.Key) |> String.concat ", ") + "]")
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

let rec private setJsonPath (root: JsonNode) (segs: JPath list) (value: JsonNode) (mode: JsonWriteMode) : unit =
    match segs with
    | [ JKey k ] ->
        match root with
        | :? JsonObject as o ->
            match mode, o.ContainsKey k with
            | JInsert, true
            | JReplace, false -> ()
            | _ -> o.[k] <- value
        | _ -> ()
    | [ JIndex idx ] ->
        match root with
        | :? JsonArray as a ->
            let i = if idx < 0 then a.Count + idx else idx

            if i >= 0 && i < a.Count then
                match mode with
                | JInsert -> ()
                | _ -> a.[i] <- value
            elif i = a.Count then
                match mode with
                | JReplace -> ()
                | _ -> a.Add value
        | _ -> ()
    | JKey k :: rest ->
        match root with
        | :? JsonObject as o ->
            if o.ContainsKey k && not (isNull o.[k]) then
                setJsonPath o.[k] rest value mode
        | _ -> ()
    | JIndex idx :: rest ->
        match root with
        | :? JsonArray as a ->
            let i = if idx < 0 then a.Count + idx else idx

            if i >= 0 && i < a.Count then
                let child = a.[i]
                if not (isNull child) then setJsonPath child rest value mode
        | _ -> ()
    | JWildcard :: _
    | [] -> ()

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

let rec private removeJsonPath (root: JsonNode) (segs: JPath list) : unit =
    match segs with
    | [ JKey k ] ->
        match root with
        | :? JsonObject as o -> o.Remove k |> ignore
        | _ -> ()
    | [ JIndex idx ] ->
        match root with
        | :? JsonArray as a ->
            let i = if idx < 0 then a.Count + idx else idx
            if i >= 0 && i < a.Count then a.RemoveAt i
        | _ -> ()
    | JKey k :: rest ->
        match root with
        | :? JsonObject as o ->
            if o.ContainsKey k && not (isNull o.[k]) then
                removeJsonPath o.[k] rest
        | _ -> ()
    | JIndex idx :: rest ->
        match root with
        | :? JsonArray as a ->
            let i = if idx < 0 then a.Count + idx else idx

            if i >= 0 && i < a.Count then
                let child = a.[i]
                if not (isNull child) then removeJsonPath child rest
        | _ -> ()
    | JWildcard :: _
    | [] -> ()

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
            | ps, "ALL" -> VJson("[" + (ps |> List.map JsonSerializer.Serialize |> String.concat ", ") + "]")
            | p :: _, _ -> VJson(JsonSerializer.Serialize p)
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

let private addInterval (dt: DateTime) (amount: float) (unit: string) : DateTime =
    match unit.ToUpperInvariant() with
    | "SECOND" -> dt.AddSeconds amount
    | "MINUTE" -> dt.AddMinutes amount
    | "HOUR" -> dt.AddHours amount
    | "DAY" -> dt.AddDays amount
    | "WEEK" -> dt.AddDays(amount * 7.0)
    | "MONTH" -> dt.AddMonths(int amount)
    | "QUARTER" -> dt.AddMonths(int amount * 3)
    | "YEAR" -> dt.AddYears(int amount)
    | _ -> dt

/// `Parser.fs`'s `INTERVAL n UNIT` grammar desugars to
/// `FuncCall("INTERVAL", [n; Lit(VString UNIT)])` (no separate `Interval`
/// AST node), which `evalExpr` evaluates like any other function call
/// before `DATE_ADD`/`DATE_SUB` see it — so the marker string this scalar
/// returns is exactly `dateAddCore`'s 2-arg-form input. Still independently
/// testable by evaluating the registered functions with `Value` args
/// directly.
let private intervalMarker = " INTERVAL "

let private intervalFn: Scalar =
    function
    | [ amt; unit ] -> VString(intervalMarker + req amt + " " + req unit)
    | _ -> VNull

/// Reads the `INTERVAL` encoding above, or tolerates a plain `"N UNIT"`
/// string (e.g. `DATE_ADD(d, '1 DAY')`) as a fallback shape.
let private tryParseIntervalArg (v: Value) : (float * string) option =
    match v with
    | VString s when s.StartsWith intervalMarker ->
        match s.Substring(intervalMarker.Length).Split(' ') with
        | [| n; u |] ->
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
    let result = addInterval dt (sign * amount) unit
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

/// No `TIME` case in `Value` (see the `VJson` comment on the same theme) —
/// ponytail: rendered as a plain `"HH:mm:ss"` string, add a `VTime` case if
/// a migration needs it to compare/sort as a real time value.
let private timeFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateTime v |> Option.map (fun dt -> VString(dt.ToString("HH:mm:ss"))) |> Option.defaultValue VNull
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

/// `WEEK(date[, mode])` — ponytail: only the mode-0-ish default (Sunday
/// first day of the week) is modeled; a `mode` argument, if given, is
/// accepted but ignored rather than implementing all 8 MySQL week modes.
let private weekFn: Scalar =
    function
    | (v :: _) when not (anyNull [ v ]) ->
        asDateTime v
        |> Option.map (fun dt ->
            VInt(int64 (CultureInfo.InvariantCulture.Calendar.GetWeekOfYear(dt, CalendarWeekRule.FirstDay, DayOfWeek.Sunday))))
        |> Option.defaultValue VNull
    | _ -> VNull

let private curDateFn: Scalar = fun _ -> VDate(DateOnly.FromDateTime DateTime.Now)
let private curTimeFn: Scalar = fun _ -> VString(DateTime.Now.ToString "HH:mm:ss")

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

let private fromUnixTimeFn: Scalar =
    function
    | [ ts ] when not (anyNull [ ts ]) -> VDateTime(unixEpoch.AddSeconds(toDouble ts))
    | [ ts; f ] when not (anyNull [ ts; f ]) ->
        match toText f with
        | Some fmt -> VString(formatDate (unixEpoch.AddSeconds(toDouble ts)) fmt)
        | None -> VNull
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
                | "MONTH" -> float ((db.Year - da.Year) * 12 + db.Month - da.Month) - (if db.Day < da.Day then 1.0 else 0.0)
                | "QUARTER" -> float ((db.Year - da.Year) * 12 + db.Month - da.Month) / 3.0
                | "YEAR" ->
                    float (db.Year - da.Year)
                    - (if (db.Month, db.Day) < (da.Month, da.Day) then 1.0 else 0.0)
                | _ -> span.TotalSeconds

            VInt(int64 (Math.Truncate result))
        | _ -> VNull
    | _ -> VNull

let private lastDayFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) ->
        asDateTime v
        |> Option.map (fun dt -> VDate(DateOnly(dt.Year, dt.Month, DateTime.DaysInMonth(dt.Year, dt.Month))))
        |> Option.defaultValue VNull
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
    elif pos < 0 then Some(max 0 (len + pos))
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

let private locateAt (str: string) (sub: string) (startIdx: int) : Value =
    if startIdx > str.Length || startIdx < 0 then
        VInt 0L
    else
        VInt(int64 (str.IndexOf(sub, startIdx, StringComparison.OrdinalIgnoreCase) + 1))

let private locateFn: Scalar =
    function
    | [ sub; str ] when not (anyNull [ sub; str ]) -> locateAt (req str) (req sub) 0
    | [ sub; str; posV ] when not (anyNull [ sub; str; posV ]) -> locateAt (req str) (req sub) (max 0 (int (toDouble posV) - 1))
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

        if targetLen <= 0 then
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

let private leftFn: Scalar =
    function
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let str = req s
        VString(str.Substring(0, max 0 (min str.Length (int (toDouble n)))))
    | _ -> VNull

let private rightFn: Scalar =
    function
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let str = req s
        let k = max 0 (min str.Length (int (toDouble n)))
        VString(str.Substring(str.Length - k))
    | _ -> VNull

let private repeatFn: Scalar =
    function
    | [ s; n ] when not (anyNull [ s; n ]) ->
        let k = int (toDouble n)
        VString(if k <= 0 then "" else String.replicate k (req s))
    | _ -> VNull

let private spaceFn: Scalar =
    function
    | [ n ] when not (anyNull [ n ]) -> VString(String(' ', max 0 (int (toDouble n))))
    | _ -> VNull

let private asciiFn: Scalar =
    function
    | [ s ] when not (anyNull [ s ]) ->
        let str = req s
        VInt(if str = "" then 0L else int64 (byte str.[0]))
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
    | [ VString s ] -> VString(s |> Seq.map (fun c -> (int c).ToString "X2") |> String.concat "")
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
            |> Text.Encoding.Latin1.GetString
            |> VString
    | _ -> VNull

let private md5Fn: Scalar = textMap (fun s -> Convert.ToHexString(MD5.HashData(Text.Encoding.UTF8.GetBytes s)).ToLowerInvariant())
let private sha1Fn: Scalar = textMap (fun s -> Convert.ToHexString(SHA1.HashData(Text.Encoding.UTF8.GetBytes s)).ToLowerInvariant())

/// ponytail: SHA-224 isn't in the BCL (no `SHA224` type) — returns NULL for
/// that length rather than hand-rolling it, add a real implementation if a
/// migration actually calls `SHA2(x, 224)`.
let private sha2Fn: Scalar =
    function
    | [ s; lenV ] when not (anyNull [ s; lenV ]) ->
        let bytes = Text.Encoding.UTF8.GetBytes(req s)

        let hash =
            match int (toDouble lenV) with
            | 0
            | 256 -> Some(SHA256.HashData bytes)
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

let private findInSetFn: Scalar =
    function
    | [ s; list ] when not (anyNull [ s; list ]) ->
        let target = req s

        match (req list).Split(',') |> Array.tryFindIndex (fun x -> String.Equals(x, target, StringComparison.OrdinalIgnoreCase)) with
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
            | c -> sb.Append c |> ignore

        sb.Append '\'' |> ignore
        VString(sb.ToString())
    | _ -> VNull

let private strcmpFn: Scalar =
    function
    | [ a; b ] when not (anyNull [ a; b ]) -> VInt(int64 (sign (Value.compare a b)))
    | _ -> VNull

// ---------------------------------------------------------------------------
// Math/misc.
// ---------------------------------------------------------------------------

let private ceilFn: Scalar =
    function
    | [ VInt i ] -> VInt i
    | [ v ] when not (anyNull [ v ]) -> VInt(int64 (Math.Ceiling(toDouble v)))
    | _ -> VNull

let private floorFn: Scalar =
    function
    | [ VInt i ] -> VInt i
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

let private truncateFn: Scalar =
    function
    | [ VDecimal dec; d ] when not (anyNull [ d ]) ->
        let factor = decimal (Math.Pow(10.0, float (int (toDouble d))))
        VDecimal(Math.Truncate(dec * factor) / factor)
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

let private parseInBase (s: string) (b: int) : int64 option =
    let s = s.Trim().ToUpperInvariant()
    let neg, s = if s.StartsWith "-" then true, s.Substring 1 else false, s

    if s = "" then
        None
    else
        let mutable ok = true
        let mutable acc = 0L

        for c in s do
            let d = baseDigits.IndexOf c
            if d < 0 || d >= b then ok <- false else acc <- acc * int64 b + int64 d

        if ok then Some(if neg then -acc else acc) else None

let private toBase (n: int64) (b: int) : string =
    if n = 0L then
        "0"
    else
        let neg = n < 0L
        let mutable v = abs n
        let sb = StringBuilder()

        while v > 0L do
            sb.Insert(0, baseDigits.[int (v % int64 b)]) |> ignore
            v <- v / int64 b

        (if neg then "-" else "") + sb.ToString()

let private convFn: Scalar =
    function
    | [ n; f; t ] when not (anyNull [ n; f; t ]) ->
        match parseInBase (req n) (int (toDouble f)) with
        | Some v -> VString(toBase v (int (toDouble t)))
        | None -> VNull
    | _ -> VNull

let private binFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VString(toBase (int64 (toDouble v)) 2)
    | _ -> VNull

let private octFn: Scalar =
    function
    | [ v ] when not (anyNull [ v ]) -> VString(toBase (int64 (toDouble v)) 8)
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
let private sumAgg: Aggregate = List.reduce Value.add
let private avgAgg: Aggregate = fun vs -> Value.div (vs |> List.reduce Value.add) (VInt(int64 (List.length vs)))
let private minAgg: Aggregate = List.reduce (fun a b -> if Value.compare a b <= 0 then a else b)
let private maxAgg: Aggregate = List.reduce (fun a b -> if Value.compare a b >= 0 then a else b)

let builtins: Registry =
    empty
    |> registerScalar "NOW" nowFn
    |> registerScalar "CURRENT_TIMESTAMP" nowFn
    |> registerScalar "CONCAT" concatFn
    |> registerScalar "UPPER" (textMap (fun s -> s.ToUpperInvariant()))
    |> registerScalar "LOWER" (textMap (fun s -> s.ToLowerInvariant()))
    |> registerScalar "LENGTH" lengthFn
    |> registerScalar "CHAR_LENGTH" lengthFn
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
    |> registerScalar "JSON_VALID" jsonValidFn
    |> registerScalar "JSON_KEYS" jsonKeysFn
    |> registerScalar "JSON_SEARCH" jsonSearchFn
    // Dates
    |> registerScalar "DATE_ADD" (dateAddCore 1.0)
    |> registerScalar "ADDDATE" (dateAddCore 1.0)
    |> registerScalar "DATE_SUB" (dateAddCore -1.0)
    |> registerScalar "SUBDATE" (dateAddCore -1.0)
    |> registerScalar "INTERVAL" intervalFn
    |> registerScalar "DATEDIFF" dateDiffFn
    |> registerScalar "DATE_FORMAT" dateFormatFn
    |> registerScalar "DATE" dateFn
    |> registerScalar "TIME" timeFn
    |> registerScalar "YEAR" (datePartFn (fun d -> d.Year))
    |> registerScalar "MONTH" (datePartFn (fun d -> d.Month))
    |> registerScalar "DAY" (datePartFn (fun d -> d.Day))
    |> registerScalar "DAYOFMONTH" (datePartFn (fun d -> d.Day))
    |> registerScalar "HOUR" (datePartFn (fun d -> d.Hour))
    |> registerScalar "MINUTE" (datePartFn (fun d -> d.Minute))
    |> registerScalar "SECOND" (datePartFn (fun d -> d.Second))
    |> registerScalar "DAYOFWEEK" (datePartFn (fun d -> int d.DayOfWeek + 1))
    |> registerScalar "DAYNAME" dayNameFn
    |> registerScalar "MONTHNAME" monthNameFn
    |> registerScalar "WEEK" weekFn
    |> registerScalar "QUARTER" (datePartFn (fun d -> (d.Month - 1) / 3 + 1))
    |> registerScalar "CURDATE" curDateFn
    |> registerScalar "CURRENT_DATE" curDateFn
    |> registerScalar "CURTIME" curTimeFn
    |> registerScalar "UNIX_TIMESTAMP" unixTimestampFn
    |> registerScalar "FROM_UNIXTIME" fromUnixTimeFn
    |> registerScalar "TIMESTAMPDIFF" timestampDiffFn
    |> registerScalar "LAST_DAY" lastDayFn
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
    |> registerScalar "FIELD" fieldFn
    |> registerScalar "FIND_IN_SET" findInSetFn
    |> registerScalar "QUOTE" quoteFn
    |> registerScalar "STRCMP" strcmpFn
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
    |> registerScalar "OCT" octFn
    |> registerScalar "CRC32" crc32Fn
    |> registerScalar "UUID" uuidFn
    |> registerScalar "INET_ATON" inetAtonFn
    |> registerScalar "INET_NTOA" inetNtoaFn
    |> registerAggregate "COUNT" countAgg
    |> registerAggregate "SUM" sumAgg
    |> registerAggregate "AVG" avgAgg
    |> registerAggregate "MIN" minAgg
    |> registerAggregate "MAX" maxAgg
