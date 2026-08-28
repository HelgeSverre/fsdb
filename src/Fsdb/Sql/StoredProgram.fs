module internal Fsdb.StoredProgram

open System
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value

type Declaration =
    { Name: string
      ColumnType: ColumnType
      InitialValue: Expr option }

type Statement =
    | Sql of Ast.Statement
    | TextSql of string
    | Block of label: string option * body: Statement list
    | If of condition: Expr * whenTrue: Statement list * whenFalse: Statement list
    | Case of value: Expr option * branches: (Expr * Statement list) list * otherwise: Statement list option
    | While of label: string option * condition: Expr * body: Statement list
    | Repeat of label: string option * body: Statement list * until: Expr
    | Loop of label: string option * body: Statement list
    | Leave of label: string
    | Iterate of label: string
    | Declare of Declaration
    | SetLocal of name: string * value: Expr

[<RequireQualifiedAccess>]
type Flow =
    | Complete
    | Leave of label: string
    | Iterate of label: string

type ParameterMode =
    | In
    | Out
    | InOut

type Parameter =
    { Name: string
      ColumnType: ColumnType
      Mode: ParameterMode }

type ValidationError =
    | DuplicateParameter of name: string
    | DuplicateVariable of name: string
    | UnknownVariable of name: string
    | RedefiningLabel of name: string
    | UnknownLabel of operation: string * name: string

let validationError =
    function
    | DuplicateParameter name -> 1330, sprintf "Duplicate parameter: %s" name
    | DuplicateVariable name -> 1331, sprintf "Duplicate variable: %s" name
    | UnknownVariable name -> 1193, sprintf "Unknown system variable '%s'" name
    | RedefiningLabel name -> 1309, sprintf "Redefining label %s" name
    | UnknownLabel(operation, name) -> 1308, sprintf "%s with no matching label: %s" operation name

let rec sqlStatements =
    function
    | Sql statement -> [ statement ]
    | TextSql _ -> []
    | Block(_, body)
    | Loop(_, body) -> body |> List.collect sqlStatements
    | While(_, _, body)
    | Repeat(_, body, _) -> body |> List.collect sqlStatements
    | If(_, whenTrue, whenFalse) ->
        (whenTrue @ whenFalse) |> List.collect sqlStatements
    | Case(_, branches, otherwise) ->
        (branches |> List.collect (snd >> List.collect sqlStatements))
        @ (otherwise |> Option.defaultValue [] |> List.collect sqlStatements)
    | Declare _
    | SetLocal _
    | Leave _
    | Iterate _ -> []

let rec expressions =
    function
    | Sql _
    | TextSql _ -> []
    | Block(_, body)
    | Loop(_, body) -> body |> List.collect expressions
    | While(_, condition, body) -> condition :: (body |> List.collect expressions)
    | Repeat(_, body, until) -> until :: (body |> List.collect expressions)
    | If(condition, whenTrue, whenFalse) ->
        condition :: ((whenTrue @ whenFalse) |> List.collect expressions)
    | Case(value, branches, otherwise) ->
        (value |> Option.toList)
        @ (branches |> List.collect (fun (condition, body) -> condition :: (body |> List.collect expressions)))
        @ (otherwise |> Option.defaultValue [] |> List.collect expressions)
    | Declare declaration -> Option.toList declaration.InitialValue
    | SetLocal(_, value) -> [ value ]
    | Leave _
    | Iterate _ -> []

let declaredNames statements =
    statements
    |> List.choose (function
        | Declare declaration -> Some declaration.Name
        | _ -> None)
    |> Set.ofList

let restoreOuterScope statements (before: Map<string, 'value>) after =
    let shadowed = declaredNames statements

    before
    |> Map.map (fun name value ->
        if Set.contains name shadowed then
            value
        else
            after |> Map.tryFind name |> Option.defaultValue value)

let caseBranchIndexExpression selector branches =
    Ast.Case(
        selector,
        branches |> List.mapi (fun index (condition, _) -> condition, Lit(VInt(int64 index))),
        Some(Lit(VInt -1L))
    )

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
    | Do
    | When
    | Until
    | ElseIf
    | Else
    | EndIf
    | EndCase
    | EndWhile
    | EndRepeat
    | EndLoop
    | End
    | Semicolon

[<RequireQualifiedAccess>]
type private LabelKind =
    | Block
    | Loop

let private compoundEndBoundaries =
    [ EndIf, "IF"
      EndCase, "CASE"
      EndWhile, "WHILE"
      EndRepeat, "REPEAT"
      EndLoop, "LOOP" ]

let private keywordBoundaries =
    [ ElseIf, "ELSEIF"
      Else, "ELSE"
      When, "WHEN"
      Then, "THEN"
      Do, "DO"
      Until, "UNTIL" ]

let private parameterPattern =
    Regex(
        @"^(?:(?<mode>INOUT|IN|OUT)\s+)?(?<name>`(?:``|[^`])+`|[A-Za-z_$][A-Za-z0-9_$]*)\s+(?<type>[\s\S]+)$",
        RegexOptions.IgnoreCase
    )

let private labelPattern = @"`(?:``|[^`])+`|[A-Za-z_][A-Za-z0-9_$]*"

let private triviaPattern =
    @"(?:\s|/\*[\s\S]*?\*/|#[^\r\n]*(?:\r\n|\r|\n|$)|--(?=\s)[^\r\n]*(?:\r\n|\r|\n|$))*"

let private compoundPattern =
    Regex(
        sprintf
            @"^%s(?:(?<label>%s)%s:%s)?BEGIN\b(?<body>[\s\S]*)\bEND(?:%s(?<endLabel>%s))?%s$"
            triviaPattern
            labelPattern
            triviaPattern
            triviaPattern
            triviaPattern
            labelPattern
            triviaPattern,
        RegexOptions.IgnoreCase
    )

let private labelPrefixPattern =
    Regex(sprintf @"\G%s(?<label>%s)%s:%s" triviaPattern labelPattern triviaPattern triviaPattern, RegexOptions.IgnoreCase)

let private closingLabelPattern =
    Regex(sprintf @"\G%s(?<label>%s)" triviaPattern labelPattern, RegexOptions.IgnoreCase)

let private declarationPattern =
    Regex(
        @"^DECLARE\s+(?<name>[A-Za-z_][A-Za-z0-9_$]*)\s+(?<type>[A-Za-z]+(?:\s*\([^)]*\))?(?:\s+UNSIGNED)?)(?:\s+DEFAULT\s+(?<default>[\s\S]+))?$",
        RegexOptions.IgnoreCase
    )

let private assignmentPattern =
    Regex(@"^SET\s+(?<name>[A-Za-z_][A-Za-z0-9_$]*)\s*=\s*(?<value>[\s\S]+)$", RegexOptions.IgnoreCase)

let private leavePattern = Regex(sprintf @"^LEAVE\s+(?<label>%s)$" labelPattern, RegexOptions.IgnoreCase)
let private iteratePattern = Regex(sprintf @"^ITERATE\s+(?<label>%s)$" labelPattern, RegexOptions.IgnoreCase)

let private normalizeLabel (label: string) =
    label.Trim('`').Replace("``", "`").ToLowerInvariant()

let private normalizedGroup (name: string) (matched: Match) =
    let group = matched.Groups.[name]
    if group.Success then Some(normalizeLabel group.Value) else None

let private wordAt (text: string) index (word: string) =
    let finish = index + word.Length
    let isWordCharacter character = Char.IsLetterOrDigit character || character = '_' || character = '$'

    index >= 0
    && finish <= text.Length
    && String.Compare(text, index, word, 0, word.Length, StringComparison.OrdinalIgnoreCase) = 0
    && (index = 0 || not (isWordCharacter text.[index - 1]))
    && (finish = text.Length || not (isWordCharacter text.[finish]))

let private skipTrivia (text: string) offset =
    let mutable next = offset
    let mutable scanning = true

    while scanning do
        while next < text.Length && Char.IsWhiteSpace text.[next] do
            next <- next + 1

        if next < text.Length && text.[next] = '#' then
            while next < text.Length && text.[next] <> '\n' && text.[next] <> '\r' do
                next <- next + 1
        elif
            next + 2 < text.Length
            && text.[next] = '-'
            && text.[next + 1] = '-'
            && Char.IsWhiteSpace text.[next + 2]
        then
            next <- next + 2

            while next < text.Length && text.[next] <> '\n' && text.[next] <> '\r' do
                next <- next + 1
        elif next + 1 < text.Length && text.[next] = '/' && text.[next + 1] = '*' then
            let finish = text.IndexOf("*/", next + 2, StringComparison.Ordinal)
            next <- if finish < 0 then text.Length else finish + 2
        else
            scanning <- false

    next

let private compoundEndAt (text: string) index keyword =
    if wordAt text index "END" then
        let next = skipTrivia text (index + 3)

        if wordAt text next keyword then Some(next + keyword.Length) else None
    else
        None

let private boundaryAt (boundaries: Set<Boundary>) (text: string) index =
    if boundaries.Contains Semicolon && text.[index] = ';' then
        Some(Semicolon, index + 1)
    else
        compoundEndBoundaries
        |> List.tryPick (fun (boundary, keyword) ->
            if boundaries.Contains boundary then
                compoundEndAt text index keyword |> Option.map (fun finish -> boundary, finish)
            else
                None)
        |> Option.orElseWith (fun () ->
            keywordBoundaries
            |> List.tryPick (fun (boundary, keyword) ->
                if boundaries.Contains boundary && wordAt text index keyword then
                    Some(boundary, index + keyword.Length)
                else
                    None))
        |> Option.orElseWith (fun () ->
            if boundaries.Contains End && wordAt text index "END" then Some(End, index + 3) else None)

let private findBoundary boundaries (text: string) start =
    let mutable index = start
    let mutable depth = 0
    let mutable caseDepth = 0
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
            | None when depth = 0 && wordAt text index "CASE" ->
                caseDepth <- caseDepth + 1
                index <- index + 4
            | None when depth = 0 && caseDepth > 0 && wordAt text index "END" ->
                caseDepth <- caseDepth - 1
                index <- index + 3
            | None when depth = 0 && caseDepth = 0 ->
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

let parseArguments (options: Parser.ParserOptions) (text: string) : Result<Expr list, string> =
    if String.IsNullOrWhiteSpace text then
        Ok []
    else
        Parser.splitTopLevelCommaSeparatedWithOptions options text
        |> traverse (Parser.parseExpressionWithOptions options)

let private parseWithFallback
    (options: Parser.ParserOptions)
    (isSupportedText: string -> bool)
    (body: string)
    : Result<Statement list, string> =
    let compound = compoundPattern.Match body

    if compound.Success then
        let inner = compound.Groups.["body"].Value
        let rootLabel = normalizedGroup "label" compound
        let rootEndLabel = normalizedGroup "endLabel" compound

        let skipSeparators offset =
            let mutable next = offset
            let mutable scanning = true

            while scanning do
                next <- skipTrivia inner next

                if next < inner.Length && inner.[next] = ';' then
                    next <- next + 1
                else
                    scanning <- false

            next

        let labelAt offset =
            let matched = labelPrefixPattern.Match(inner, offset)

            if matched.Success && matched.Index = offset then
                Some(normalizeLabel matched.Groups.["label"].Value, matched.Index + matched.Length)
            else
                None

        let closingLabelAt offset =
            let offset = skipTrivia inner offset
            let matched = closingLabelPattern.Match(inner, offset)

            if matched.Success && matched.Index = offset then
                Some(normalizeLabel matched.Groups.["label"].Value, matched.Index + matched.Length)
            else
                None

        let consumeClosingLabel openingLabel offset =
            match openingLabel, closingLabelAt offset with
            | None, None
            | Some _, None -> Ok offset
            | Some expected, Some(actual, next) when expected = actual -> Ok next
            | Some expected, Some(actual, _) -> Error(sprintf "End label '%s' does not match '%s'" actual expected)
            | None, Some(actual, _) -> Error(sprintf "End label '%s' has no matching start label" actual)

        let rec parseStatements offset (stops: Set<Boundary>) statements =
            let offset = skipSeparators offset

            if offset >= inner.Length then
                if stops.IsEmpty then
                    Ok(List.rev statements, offset, None)
                else
                    Error "Unterminated stored-program block"
            else
                match boundaryAt stops inner offset with
                | Some(boundary, finish) -> Ok(List.rev statements, finish, Some boundary)
                | None ->
                    let label, statementStart =
                        match labelAt offset with
                        | Some(label, next) -> Some label, next
                        | None -> None, offset

                    parseStructured label statementStart
                    |> Result.bind (fun (statement, next) -> parseStatements next stops (statement :: statements))

        and parseStructured label offset =
            let offset = skipTrivia inner offset

            match label with
            | None when wordAt inner offset "IF" -> parseIf (offset + 2)
            | None when wordAt inner offset "CASE" -> parseCase (offset + 4)
            | _ when wordAt inner offset "WHILE" -> parseWhile label (offset + 5)
            | _ when wordAt inner offset "REPEAT" -> parseRepeat label (offset + 6)
            | _ when wordAt inner offset "LOOP" -> parseLoop label (offset + 4)
            | _ when wordAt inner offset "BEGIN" -> parseBlock label (offset + 5)
            | Some name -> Error(sprintf "Label '%s' must name a block or loop" name)
            | None ->
                match findBoundary (Set.singleton Semicolon) inner offset with
                | Some(finishStart, finish, _) ->
                    parseStatement (inner.Substring(offset, finishStart - offset).Trim())
                    |> Result.map (fun statement -> statement, finish)
                | None ->
                    parseStatement (inner.Substring(offset).Trim())
                    |> Result.map (fun statement -> statement, inner.Length)

        and parseBlock label bodyStart =
            parseClosedBody label bodyStart End "BEGIN is missing END" (fun body -> Block(label, body))

        and parseClosedBody label bodyStart expectedBoundary missingEnd makeStatement =
            parseStatements bodyStart (Set.singleton expectedBoundary) []
            |> Result.bind (fun (statements, next, boundary) ->
                match boundary with
                | Some actual when actual = expectedBoundary ->
                    consumeClosingLabel label next
                    |> Result.map (fun finish -> makeStatement statements, finish)
                | _ -> Error missingEnd)

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

        and parseCase caseStart =
            let caseStart = skipTrivia inner caseStart

            let selector =
                if wordAt inner caseStart "WHEN" then
                    Ok(None, caseStart + 4)
                else
                    match findBoundary (Set.singleton When) inner caseStart with
                    | None -> Error "CASE is missing WHEN"
                    | Some(selectorEnd, whenStart, _) ->
                        Parser.parseExpressionWithOptions options (inner.Substring(caseStart, selectorEnd - caseStart).Trim())
                        |> Result.map (fun value -> Some value, whenStart)

            let rec parseBranches whenStart branches =
                match findBoundary (Set.singleton Then) inner whenStart with
                | None -> Error "CASE WHEN is missing THEN"
                | Some(conditionEnd, bodyStart, _) ->
                    Parser.parseExpressionWithOptions options (inner.Substring(whenStart, conditionEnd - whenStart).Trim())
                    |> Result.bind (fun condition ->
                        parseStatements bodyStart (Set.ofList [ When; Else; EndCase ]) []
                        |> Result.bind (fun (branch, next, boundary) ->
                            if branch.IsEmpty then
                                Error "CASE branch cannot be empty"
                            else
                                let branches = (condition, branch) :: branches

                                match boundary with
                                | Some When -> parseBranches next branches
                                | Some Else ->
                                    parseStatements next (Set.singleton EndCase) []
                                    |> Result.bind (fun (otherwise, finish, ended) ->
                                        match ended with
                                        | Some EndCase when not otherwise.IsEmpty ->
                                            Ok(List.rev branches, Some otherwise, finish)
                                        | Some EndCase -> Error "CASE ELSE cannot be empty"
                                        | _ -> Error "CASE ELSE is missing END CASE")
                                | Some EndCase -> Ok(List.rev branches, None, next)
                                | _ -> Error "CASE is missing END CASE"))

            selector
            |> Result.bind (fun (value, whenStart) ->
                parseBranches whenStart []
                |> Result.map (fun (branches, otherwise, finish) -> Case(value, branches, otherwise), finish))

        and parseWhile label conditionStart =
            match findBoundary (Set.singleton Do) inner conditionStart with
            | None -> Error "WHILE is missing DO"
            | Some(conditionEnd, bodyStart, _) ->
                Parser.parseExpressionWithOptions options (inner.Substring(conditionStart, conditionEnd - conditionStart).Trim())
                |> Result.bind (fun condition ->
                    parseClosedBody label bodyStart EndWhile "WHILE is missing END WHILE" (fun body ->
                        While(label, condition, body)))

        and parseRepeat label bodyStart =
            parseStatements bodyStart (Set.singleton Until) []
            |> Result.bind (fun (statements, conditionStart, boundary) ->
                match boundary with
                | Some Until ->
                    match findBoundary (Set.singleton EndRepeat) inner conditionStart with
                    | None -> Error "REPEAT is missing END REPEAT"
                    | Some(conditionEnd, next, _) ->
                        Parser.parseExpressionWithOptions options (inner.Substring(conditionStart, conditionEnd - conditionStart).Trim())
                        |> Result.bind (fun condition ->
                            consumeClosingLabel label next |> Result.map (fun finish -> Repeat(label, statements, condition), finish))
                | _ -> Error "REPEAT is missing UNTIL")

        and parseLoop label bodyStart =
            parseClosedBody label bodyStart EndLoop "LOOP is missing END LOOP" (fun body -> Loop(label, body))

        and parseStatement text =
            let declaration = declarationPattern.Match text
            let assignment = assignmentPattern.Match text
            let leave = leavePattern.Match text
            let iterate = iteratePattern.Match text

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
            elif leave.Success then
                Ok(Leave(normalizeLabel leave.Groups.["label"].Value))
            elif iterate.Success then
                Ok(Iterate(normalizeLabel iterate.Groups.["label"].Value))
            else
                match Parser.parseStoredStatementWithOptions options text with
                | Ok statement -> Ok(Sql statement)
                | Error _ when isSupportedText text -> Ok(TextSql text)
                | Error error -> Error error

        match rootLabel, rootEndLabel with
        | None, Some actual -> Error(sprintf "End label '%s' has no matching start label" actual)
        | Some expected, Some actual when expected <> actual ->
            Error(sprintf "End label '%s' does not match '%s'" actual expected)
        | _ ->
            parseStatements 0 Set.empty []
            |> Result.bind (fun (statements, _, _) ->
                if statements.IsEmpty then
                    Error "Body cannot be empty"
                else
                    Ok(match rootLabel with Some label -> [ Block(Some label, statements) ] | None -> statements))
    else
        match Parser.parseStoredStatementWithOptions options body with
        | Ok statement -> Ok [ Sql statement ]
        | Error _
            when not (body.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
                 && isSupportedText body ->
            Ok [ TextSql body ]
        | Error error -> Error error

let parse (options: Parser.ParserOptions) (body: string) : Result<Statement list, string> =
    parseWithFallback options (fun _ -> false) body

let parseRoutine (options: Parser.ParserOptions) isSupportedText body =
    parseWithFallback options isSupportedText body

let validate (parameters: Parameter list) (statements: Statement list) : Result<unit, ValidationError> =
    let rec addParameters names =
        function
        | [] -> Ok names
        | parameter :: rest when Set.contains parameter.Name names -> Error(DuplicateParameter parameter.Name)
        | parameter :: rest -> addParameters (Set.add parameter.Name names) rest

    let addLabel label kind labels =
        match label with
        | None -> Ok labels
        | Some name when labels |> List.exists (fun (active, _) -> active = name) -> Error(RedefiningLabel name)
        | Some name -> Ok((name, kind) :: labels)

    let rec validateStatements names declared labels =
        function
        | [] -> Ok(names, declared)
        | Sql _ :: rest
        | TextSql _ :: rest -> validateStatements names declared labels rest
        | Declare declaration :: _ when Set.contains declaration.Name declared ->
            Error(DuplicateVariable declaration.Name)
        | Declare declaration :: rest ->
            validateStatements
                (Set.add declaration.Name names)
                (Set.add declaration.Name declared)
                labels
                rest
        | SetLocal(name, _) :: _ when not (Set.contains name names) -> Error(UnknownVariable name)
        | SetLocal _ :: rest -> validateStatements names declared labels rest
        | If(_, whenTrue, whenFalse) :: rest ->
            validateStatements names declared labels whenTrue
            |> Result.bind (fun _ -> validateStatements names declared labels whenFalse)
            |> Result.bind (fun _ -> validateStatements names declared labels rest)
        | Case(_, branches, otherwise) :: rest ->
            branches
            |> traverse (snd >> validateStatements names declared labels)
            |> Result.bind (fun _ -> validateStatements names declared labels (otherwise |> Option.defaultValue []))
            |> Result.bind (fun _ -> validateStatements names declared labels rest)
        | Block(label, body) :: rest ->
            addLabel label LabelKind.Block labels
            |> Result.bind (fun nestedLabels -> validateStatements names Set.empty nestedLabels body)
            |> Result.bind (fun _ -> validateStatements names declared labels rest)
        | While(label, _, body) :: rest
        | Repeat(label, body, _) :: rest
        | Loop(label, body) :: rest ->
            addLabel label LabelKind.Loop labels
            |> Result.bind (fun nestedLabels -> validateStatements names Set.empty nestedLabels body)
            |> Result.bind (fun _ -> validateStatements names declared labels rest)
        | Leave label :: _ when labels |> List.exists (fun (name, _) -> name = label) |> not ->
            Error(UnknownLabel("LEAVE", label))
        | Iterate label :: _
            when labels |> List.exists (fun (name, kind) -> name = label && kind = LabelKind.Loop) |> not ->
            Error(UnknownLabel("ITERATE", label))
        | Leave _ :: rest
        | Iterate _ :: rest -> validateStatements names declared labels rest

    addParameters Set.empty parameters
    |> Result.bind (fun names -> validateStatements names names [] statements)
    |> Result.map ignore
