/// Ordered optimizer-hint syntax; applying hints belongs to statement execution.
module internal Fsdb.OptimizerHints

open System
open System.Text

[<RequireQualifiedAccess>]
type JoinOrderKind = Fixed | Prefix | Suffix | Relative

let joinOrderName = function
    | JoinOrderKind.Fixed -> "JOIN_FIXED_ORDER"
    | JoinOrderKind.Prefix -> "JOIN_PREFIX"
    | JoinOrderKind.Suffix -> "JOIN_SUFFIX"
    | JoinOrderKind.Relative -> "JOIN_ORDER"

type Value =
    | Timeout of uint64
    | SetVariable of name: string * value: string
    | QueryBlockName of string
    | TableHint of name: string * block: string option * targets: (string * string option) list
    | IndexHint of name: string * table: string * block: string option * indexes: string list
    | JoinOrderHint of kind: JoinOrderKind * block: string option * targets: (string * string option) list
    | QueryBlockHint of name: string * block: string option * strategies: string list
    | OtherHint of string

type Hint =
    { Offset: int
      ContextOrder: int option
      Location: Parser.OptimizerHintLocation
      Value: Value }

let contextOrder (hint: Hint) = hint.ContextOrder |> Option.defaultValue hint.Offset

type Diagnostic =
    { Prefix: string
      Offset: int }

type private TokenKind = Word | Number | Text | QueryBlock | Symbol of char | Invalid | End

type private Token =
    { Kind: TokenKind
      Text: string
      Start: int
      Finish: int }

exception private SyntaxAt of int

let private tokens (options: Parser.ParserOptions) (body: string) =
    let result = ResizeArray<Token>()
    let mutable index = 0
    let isName character = Char.IsLetterOrDigit character || character = '_' || character = '$'
    let quoted delimiter =
        index <- index + 1
        let value = StringBuilder()
        let mutable closed = false
        while index < body.Length && not closed do
            if body.[index] = delimiter then
                index <- index + 1
                if index < body.Length && body.[index] = delimiter then
                    value.Append delimiter |> ignore
                    index <- index + 1
                else closed <- true
            elif body.[index] = '\\' && not options.NoBackslashEscapes && index + 1 < body.Length then
                value.Append body.[index + 1] |> ignore
                index <- index + 2
            else
                value.Append body.[index] |> ignore
                index <- index + 1
        closed, value.ToString()
    while index < body.Length do
        if Char.IsWhiteSpace body.[index] then index <- index + 1
        else
            let start = index
            let kind, text =
                match body.[index] with
                | ('\'' | '"' | '`') as delimiter ->
                    let closed, text = quoted delimiter
                    (if not closed then Invalid elif delimiter = '\'' then Text else Word), text
                | '@' ->
                    index <- index + 1
                    let nameStart = index
                    if index < body.Length && (body.[index] = '`' || body.[index] = '"') then
                        let closed, text = quoted body.[index]
                        (if closed then QueryBlock else Invalid), text
                    else
                        while index < body.Length && isName body.[index] do index <- index + 1
                        (if index = nameStart then Invalid else QueryBlock), body.Substring(nameStart, index - nameStart)
                | character when Char.IsDigit character ->
                    index <- index + 1
                    while index < body.Length &&
                          (isName body.[index] || body.[index] = '.' ||
                           ((body.[index] = '+' || body.[index] = '-') && (body.[index - 1] = 'e' || body.[index - 1] = 'E'))) do
                        index <- index + 1
                    Number, body.Substring(start, index - start)
                | character when isName character ->
                    index <- index + 1
                    while index < body.Length && isName body.[index] do index <- index + 1
                    Word, body.Substring(start, index - start)
                | character ->
                    index <- index + 1
                    Symbol character, string character
            result.Add { Kind = kind; Text = text; Start = start; Finish = index }
    result.Add { Kind = End; Text = ""; Start = body.Length; Finish = body.Length }
    result.ToArray()

let private tableHints =
    set [ "BKA"; "NO_BKA"; "BNL"; "NO_BNL"; "HASH_JOIN"; "NO_HASH_JOIN"
          "MERGE"; "NO_MERGE"; "DERIVED_CONDITION_PUSHDOWN"; "NO_DERIVED_CONDITION_PUSHDOWN"
          "JOIN_PREFIX"; "JOIN_SUFFIX"; "JOIN_ORDER" ]

let private indexHints =
    set [ "MRR"; "NO_MRR"; "NO_ICP"; "NO_RANGE_OPTIMIZATION"; "INDEX_MERGE"; "NO_INDEX_MERGE"
          "SKIP_SCAN"; "NO_SKIP_SCAN"; "INDEX"; "NO_INDEX"; "JOIN_INDEX"; "NO_JOIN_INDEX"
          "GROUP_INDEX"; "NO_GROUP_INDEX"; "ORDER_INDEX"; "NO_ORDER_INDEX" ]

let private knownHints =
    Set.unionMany
        [ tableHints
          indexHints
          set [ "MAX_EXECUTION_TIME"; "SET_VAR"; "QB_NAME"; "RESOURCE_GROUP"
                "JOIN_FIXED_ORDER"; "SEMIJOIN"; "NO_SEMIJOIN"; "SUBQUERY" ] ]

let parse options (location: Parser.OptimizerHintLocation) =
    let input = tokens options location.Body
    let hints = ResizeArray<Hint>()
    let diagnostics = ResizeArray<Diagnostic>()
    let mutable cursor = 0
    let current () = input.[cursor]
    let take () =
        let token = current ()
        cursor <- cursor + 1
        token
    let fail () = raise (SyntaxAt(current().Start))
    let symbol wanted =
        if current().Kind <> Symbol wanted then fail ()
        take ()
    let word () =
        if current().Kind <> Word then fail ()
        take().Text
    let queryBlock () =
        if current().Kind = QueryBlock then Some(take().Text) else None
    let commaList allowEmpty read =
        let values = ResizeArray<_>()
        if not allowEmpty || current().Kind <> Symbol ')' then
            values.Add(read ())
            while current().Kind = Symbol ',' do
                take () |> ignore
                values.Add(read ())
        List.ofSeq values
    let warn prefix offset = diagnostics.Add { Prefix = prefix; Offset = location.BodyOffset + offset }
    try
        while current().Kind <> End do
            let start = current().Start
            let name = word().ToUpperInvariant()
            if not (Set.contains name knownHints) then raise (SyntaxAt start)
            symbol '(' |> ignore
            let value =
                match name with
                | "MAX_EXECUTION_TIME" ->
                    let number = current ()
                    if number.Kind <> Number || not (number.Text |> Seq.forall (fun c -> c >= '0' && c <= '9')) then fail ()
                    take () |> ignore
                    let close = symbol ')'
                    match UInt64.TryParse number.Text with
                    | true, value when value <= uint64 UInt32.MaxValue -> Some(Timeout value)
                    | _ ->
                        warn "Unsupported MAX_EXECUTION_TIME" close.Start
                        None
                | "SET_VAR" ->
                    let variable = word ()
                    symbol '=' |> ignore
                    let argument = current ()
                    match argument.Kind with Word | Number | Text -> () | _ -> fail ()
                    take () |> ignore
                    symbol ')' |> ignore
                    Some(SetVariable(variable, location.Body.Substring(argument.Start, argument.Finish - argument.Start)))
                | "QB_NAME" | "RESOURCE_GROUP" ->
                    let argument = word ()
                    symbol ')' |> ignore
                    Some(if name = "QB_NAME" then QueryBlockName argument else OtherHint name)
                | "JOIN_FIXED_ORDER" ->
                    let block = queryBlock ()
                    symbol ')' |> ignore
                    Some(JoinOrderHint(JoinOrderKind.Fixed, block, []))
                | "SEMIJOIN" | "NO_SEMIJOIN" | "SUBQUERY" ->
                    let block = queryBlock ()
                    let strategy () =
                        let token = current ()
                        let value = word().ToUpperInvariant()
                        let accepted =
                            if name = "SUBQUERY" then [ "MATERIALIZATION"; "INTOEXISTS" ]
                            else [ "FIRSTMATCH"; "LOOSESCAN"; "MATERIALIZATION"; "DUPSWEEDOUT" ]
                        if not (List.contains value accepted) then raise (SyntaxAt token.Start)
                        value
                    let strategies = if name = "SUBQUERY" then [ strategy () ] else commaList true strategy
                    symbol ')' |> ignore
                    Some(QueryBlockHint(name, block, strategies))
                | _ when Set.contains name tableHints ->
                    let block = queryBlock ()
                    let target () =
                        let table = word ()
                        let targetBlock = if block.IsSome then None else queryBlock ()
                        table, targetBlock
                    let targets = commaList true target
                    symbol ')' |> ignore
                    let joinKind =
                        match name with
                        | "JOIN_PREFIX" -> Some JoinOrderKind.Prefix
                        | "JOIN_SUFFIX" -> Some JoinOrderKind.Suffix
                        | "JOIN_ORDER" -> Some JoinOrderKind.Relative
                        | _ -> None
                    match joinKind with
                    | Some kind -> Some(JoinOrderHint(kind, block, targets))
                    | None -> Some(TableHint(name, block, targets))
                | _ ->
                    let block = queryBlock ()
                    let table = word ()
                    let block = if block.IsSome then block else queryBlock ()
                    let indexes = commaList true word
                    symbol ')' |> ignore
                    Some(IndexHint(name, table, block, indexes))
            value |> Option.iter (fun value -> hints.Add { Offset = location.BodyOffset + start; ContextOrder = None; Location = location; Value = value })
    with SyntaxAt offset -> warn "Optimizer hint syntax error" offset
    List.ofSeq hints, List.ofSeq diagnostics

let diagnosticFormatter (sql: string) =
    let lineStarts = ResizeArray<int>()
    lineStarts.Add 0
    for index = 0 to sql.Length - 1 do
        if sql.[index] = '\n' then lineStarts.Add(index + 1)
    let lineStarts = lineStarts.ToArray()

    fun (diagnostic: Diagnostic) ->
        let position = diagnostic.Offset
        let suffix = sql.Substring(position, min 80 (sql.Length - position))
        let found = Array.BinarySearch(lineStarts, position)
        let line = if found >= 0 then found + 1 else ~~~found
        sprintf "%s near '%s' at line %d" diagnostic.Prefix suffix line

let formatDiagnostic sql diagnostic = diagnosticFormatter sql diagnostic

let syntaxDiagnostics options sql =
    Parser.optimizerHintLocationsWithOptions options sql
    |> List.collect (parse options >> snd)
    |> List.map (diagnosticFormatter sql)
