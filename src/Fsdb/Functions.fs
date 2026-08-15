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

/// SQL `LIKE` wildcards (`%`/`_`) translated to a regex, the minimal
/// matcher `JSON_SEARCH`'s `search_str` needs.
let private likeToRegex (pattern: string) : Regex =
    let escaped = (Regex.Escape pattern).Replace(@"\%", ".*").Replace(@"\_", ".")
    Regex("^" + escaped + "$", RegexOptions.IgnoreCase ||| RegexOptions.Singleline)

/// Minimal `JSON_SEARCH(doc, 'one'|'all', search_str)` — ponytail: no
/// `escape_char`/restricting `path` argument support, add them if a
/// migration's search actually needs escaped wildcards or a narrowed scope.
let private jsonSearchFn: Scalar =
    function
    | doc :: modeV :: searchV :: _ when not (anyNull [ doc; modeV; searchV ]) ->
        match tryParseJsonValue doc, toText searchV with
        | Some root, Some search ->
            let rx = likeToRegex search
            let matches = collectJsonStrings root "$" |> List.filter (snd >> rx.IsMatch) |> List.map fst

            match matches, (toText modeV |> Option.defaultValue "one").ToUpperInvariant() with
            | [], _ -> VNull
            | ps, "ALL" -> VJson("[" + (ps |> List.map JsonSerializer.Serialize |> String.concat ", ") + "]")
            | p :: _, _ -> VJson(JsonSerializer.Serialize p)
        | _ -> VNull
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
    |> registerAggregate "COUNT" countAgg
    |> registerAggregate "SUM" sumAgg
    |> registerAggregate "AVG" avgAgg
    |> registerAggregate "MIN" minAgg
    |> registerAggregate "MAX" maxAgg
