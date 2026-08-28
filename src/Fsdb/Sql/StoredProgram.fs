module internal Fsdb.StoredProgram

open System
open System.Text.RegularExpressions
open Fsdb.Ast

type Declaration =
    { Name: string
      ColumnType: ColumnType
      InitialValue: Expr option }

type Statement =
    | Sql of Ast.Statement
    | If of condition: Expr * whenTrue: Statement list * whenFalse: Statement list
    | Declare of Declaration
    | SetLocal of name: string * value: Expr

type ParameterMode =
    | In
    | Out
    | InOut

type Parameter =
    { Name: string
      ColumnType: ColumnType
      Mode: ParameterMode }

let rec sqlStatements =
    function
    | Sql statement -> [ statement ]
    | If(_, whenTrue, whenFalse) ->
        (whenTrue @ whenFalse) |> List.collect sqlStatements
    | Declare _
    | SetLocal _ -> []

let rec expressions =
    function
    | Sql _ -> []
    | If(condition, whenTrue, whenFalse) ->
        condition :: ((whenTrue @ whenFalse) |> List.collect expressions)
    | Declare declaration -> Option.toList declaration.InitialValue
    | SetLocal(_, value) -> [ value ]

let private traverse f values =
    let rec loop results =
        function
        | [] -> Ok(List.rev results)
        | value :: rest ->
            match f value with
            | Ok result -> loop (result :: results) rest
            | Error error -> Error error

    loop [] values

type private Boundary =
    | Then
    | ElseIf
    | Else
    | EndIf
    | End
    | Semicolon

let private parameterPattern =
    Regex(
        @"^(?:(?<mode>INOUT|IN|OUT)\s+)?(?<name>`(?:``|[^`])+`|[A-Za-z_$][A-Za-z0-9_$]*)\s+(?<type>[\s\S]+)$",
        RegexOptions.IgnoreCase
    )

let private compoundPattern =
    Regex(@"^\s*BEGIN\b(?<body>[\s\S]*)\bEND\s*$", RegexOptions.IgnoreCase)

let private declarationPattern =
    Regex(
        @"^DECLARE\s+(?<name>[A-Za-z_][A-Za-z0-9_$]*)\s+(?<type>[A-Za-z]+(?:\s*\([^)]*\))?(?:\s+UNSIGNED)?)(?:\s+DEFAULT\s+(?<default>[\s\S]+))?$",
        RegexOptions.IgnoreCase
    )

let private assignmentPattern =
    Regex(@"^SET\s+(?<name>[A-Za-z_][A-Za-z0-9_$]*)\s*=\s*(?<value>[\s\S]+)$", RegexOptions.IgnoreCase)

let private wordAt (text: string) index (word: string) =
    let finish = index + word.Length
    let isWordCharacter character = Char.IsLetterOrDigit character || character = '_' || character = '$'

    index >= 0
    && finish <= text.Length
    && String.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) = 0
    && (index = 0 || not (isWordCharacter text.[index - 1]))
    && (finish = text.Length || not (isWordCharacter text.[finish]))

let private endIfAt (text: string) index =
    if wordAt text index "END" then
        let mutable next = index + 3

        while next < text.Length && Char.IsWhiteSpace text.[next] do
            next <- next + 1

        if wordAt text next "IF" then Some(next + 2) else None
    else
        None

let private boundaryAt (boundaries: Set<Boundary>) (text: string) index =
    if boundaries.Contains Semicolon && text.[index] = ';' then
        Some(Semicolon, index + 1)
    elif boundaries.Contains EndIf then
        match endIfAt text index with
        | Some finish -> Some(EndIf, finish)
        | None when boundaries.Contains ElseIf && wordAt text index "ELSEIF" -> Some(ElseIf, index + 6)
        | None when boundaries.Contains Else && wordAt text index "ELSE" -> Some(Else, index + 4)
        | None when boundaries.Contains Then && wordAt text index "THEN" -> Some(Then, index + 4)
        | None -> None
    elif boundaries.Contains End && wordAt text index "END" then
        Some(End, index + 3)
    elif boundaries.Contains ElseIf && wordAt text index "ELSEIF" then
        Some(ElseIf, index + 6)
    elif boundaries.Contains Else && wordAt text index "ELSE" then
        Some(Else, index + 4)
    elif boundaries.Contains Then && wordAt text index "THEN" then
        Some(Then, index + 4)
    else
        None

let private findBoundary boundaries (text: string) start =
    let mutable index = start
    let mutable depth = 0
    let mutable quote = None
    let mutable blockComment = false
    let mutable lineComment = false
    let mutable found = None

    while index < text.Length && found.IsNone do
        if blockComment then
            if text.[index] = '*' && index + 1 < text.Length && text.[index + 1] = '/' then
                blockComment <- false
                index <- index + 2
            else
                index <- index + 1
        elif lineComment then
            if text.[index] = '\n' || text.[index] = '\r' then
                lineComment <- false

            index <- index + 1
        else
            match quote with
            | Some delimiter when text.[index] = '\\' && index + 1 < text.Length -> index <- index + 2
            | Some delimiter when text.[index] = delimiter ->
                if index + 1 < text.Length && text.[index + 1] = delimiter then
                    index <- index + 2
                else
                    quote <- None
                    index <- index + 1
            | Some _ -> index <- index + 1
            | None when text.[index] = '\'' || text.[index] = '"' || text.[index] = '`' ->
                quote <- Some text.[index]
                index <- index + 1
            | None when text.[index] = '#' ->
                lineComment <- true
                index <- index + 1
            | None when
                text.[index] = '-'
                && index + 2 < text.Length
                && text.[index + 1] = '-'
                && Char.IsWhiteSpace text.[index + 2]
                ->
                lineComment <- true
                index <- index + 2
            | None when text.[index] = '/' && index + 1 < text.Length && text.[index + 1] = '*' ->
                blockComment <- true
                index <- index + 2
            | None when text.[index] = '(' ->
                depth <- depth + 1
                index <- index + 1
            | None when text.[index] = ')' ->
                depth <- max 0 (depth - 1)
                index <- index + 1
            | None when depth = 0 ->
                match boundaryAt boundaries text index with
                | Some(boundary, finish) -> found <- Some(index, finish, boundary)
                | None -> index <- index + 1
            | None -> index <- index + 1

    found

let parseParameters (options: Parser.ParserOptions) (text: string) : Result<Parameter list, string> =
    let parseOne value =
        let matched = parameterPattern.Match value

        if not matched.Success then
            Error(sprintf "Invalid routine parameter: %s" value)
        else
            let name = matched.Groups.["name"].Value.Trim('`').Replace("``", "`").ToLowerInvariant()
            let mode =
                match matched.Groups.["mode"].Value.ToUpperInvariant() with
                | "OUT" -> Out
                | "INOUT" -> InOut
                | _ -> In

            Parser.parseColumnTypeWithOptions options matched.Groups.["type"].Value
            |> Result.map (fun columnType ->
                { Name = name
                  ColumnType = columnType
                  Mode = mode })

    if String.IsNullOrWhiteSpace text then
        Ok []
    else
        Parser.splitTopLevelCommaSeparatedWithOptions options text |> traverse parseOne

let parse (options: Parser.ParserOptions) (body: string) : Result<Statement list, string> =
    let compound = compoundPattern.Match body

    if compound.Success then
        let inner = compound.Groups.["body"].Value

        let skipSeparators offset =
            let mutable next = offset

            while next < inner.Length && (Char.IsWhiteSpace inner.[next] || inner.[next] = ';') do
                next <- next + 1

            next

        let rec parseStatements offset (stops: Set<Boundary>) statements =
            let offset = skipSeparators offset

            if offset >= inner.Length then
                if stops.IsEmpty then Ok(List.rev statements, offset, None) else Error "Unterminated IF"
            else
                match boundaryAt stops inner offset with
                | Some(boundary, finish) -> Ok(List.rev statements, finish, Some boundary)
                | None when wordAt inner offset "IF" ->
                    parseIf (offset + 2)
                    |> Result.bind (fun (statement, next) -> parseStatements next stops (statement :: statements))
                | None when wordAt inner offset "BEGIN" ->
                    parseStatements (offset + 5) (Set.singleton End) []
                    |> Result.bind (fun (block, next, boundary) ->
                        match boundary with
                        | Some End -> parseStatements next stops (List.rev block @ statements)
                        | _ -> Error "BEGIN is missing END")
                | None ->
                    match findBoundary (Set.singleton Semicolon) inner offset with
                    | Some(finishStart, finish, _) ->
                        parseStatement (inner.Substring(offset, finishStart - offset).Trim())
                        |> Result.bind (fun statement -> parseStatements finish stops (statement :: statements))
                    | None ->
                        parseStatement (inner.Substring(offset).Trim())
                        |> Result.map (fun statement -> List.rev (statement :: statements), inner.Length, None)

        and parseIf conditionStart =
            match findBoundary (Set.singleton Then) inner conditionStart with
            | None -> Error "IF is missing THEN"
            | Some(conditionEnd, bodyStart, _) ->
                Parser.parseExpressionWithOptions options (inner.Substring(conditionStart, conditionEnd - conditionStart).Trim())
                |> Result.bind (fun condition ->
                    parseStatements bodyStart (Set.ofList [ ElseIf; Else; EndIf ]) []
                    |> Result.bind (fun (whenTrue, next, boundary) ->
                        match boundary with
                        | Some EndIf -> Ok(If(condition, whenTrue, []), next)
                        | Some Else ->
                            parseStatements next (Set.singleton EndIf) []
                            |> Result.bind (fun (whenFalse, finish, ended) ->
                                match ended with
                                | Some EndIf -> Ok(If(condition, whenTrue, whenFalse), finish)
                                | _ -> Error "ELSE is missing END IF")
                        | Some ElseIf ->
                            parseIf next
                            |> Result.map (fun (alternative, finish) -> If(condition, whenTrue, [ alternative ]), finish)
                        | _ -> Error "IF is missing END IF"))

        and parseStatement text =
            let declaration = declarationPattern.Match text
            let assignment = assignmentPattern.Match text

            if declaration.Success then
                Parser.parseColumnTypeWithOptions options declaration.Groups.["type"].Value
                |> Result.bind (fun columnType ->
                    if declaration.Groups.["default"].Success then
                        Parser.parseExpressionWithOptions options declaration.Groups.["default"].Value
                        |> Result.map Some
                    else
                        Ok None
                    |> Result.map (fun initialValue ->
                        Declare
                            { Name = declaration.Groups.["name"].Value.ToLowerInvariant()
                              ColumnType = columnType
                              InitialValue = initialValue }))
            elif assignment.Success then
                Parser.parseExpressionWithOptions options assignment.Groups.["value"].Value
                |> Result.map (fun value -> SetLocal(assignment.Groups.["name"].Value.ToLowerInvariant(), value))
            else
                Parser.parseWithOptions options text |> Result.map Sql

        parseStatements 0 Set.empty []
        |> Result.bind (fun (statements, _, _) -> if statements.IsEmpty then Error "Body cannot be empty" else Ok statements)
    else
        Parser.parseWithOptions options body |> Result.map (Sql >> List.singleton)
