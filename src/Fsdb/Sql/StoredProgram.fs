module internal Fsdb.StoredProgram

open System
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value

type Declaration =
    { Name: string
      ColumnType: ColumnType
      InitialValue: Expr option }

type ConditionValue =
    | ErrorCode of int
    | SqlState of string
    | NamedCondition of string
    | SqlWarning
    | NotFound
    | SqlException

type HandlerAction =
    | Continue
    | Exit

type DiagnosticsArea =
    | Current
    | Stacked

type DiagnosticsTarget =
    | LocalVariable of string
    | UserVariable of UserVariableRef

type DiagnosticsItem =
    | Number
    | RowCount
    | ClassOrigin
    | SubclassOrigin
    | ReturnedSqlState
    | MessageText
    | MySqlErrorNumber
    | ConstraintCatalog
    | ConstraintSchema
    | ConstraintName
    | CatalogName
    | SchemaName
    | TableName
    | ColumnName
    | CursorName

type DiagnosticsRequest =
    | StatementInformation of (DiagnosticsTarget * DiagnosticsItem) list
    | ConditionInformation of conditionNumber: Expr * assignments: (DiagnosticsTarget * DiagnosticsItem) list

type DiagnosticsStatement =
    { Area: DiagnosticsArea
      Request: DiagnosticsRequest }

type Cursor =
    { Query: Ast.Statement
      Rows: Value[][] option
      ColumnCount: int
      Position: int }

type Statement =
    | Sql of Ast.Statement
    | SelectInto of query: Ast.Statement * targets: string list
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
    | DeclareCondition of name: string * value: ConditionValue
    | DeclareHandler of action: HandlerAction * conditions: ConditionValue list * body: Statement
    | DeclareCursor of name: string * query: Ast.Statement
    | OpenCursor of name: string
    | FetchCursor of name: string * targets: string list
    | CloseCursor of name: string
    | Signal of condition: ConditionValue * information: (string * Expr) list
    | Resignal of condition: ConditionValue option * information: (string * Expr) list
    | GetDiagnostics of DiagnosticsStatement
    | SetLocal of name: string * value: Expr
    | Return of value: Expr

[<RequireQualifiedAccess>]
type Flow =
    | Complete
    | Leave of label: string
    | Iterate of label: string
    | ExitHandler
    | Return of value: Value

type ParameterMode =
    | In
    | Out
    | InOut

type Parameter =
    { Name: string
      DisplayName: string
      ColumnType: ColumnType
      Charset: string option
      Collation: string option
      Mode: ParameterMode }

type ValidationError =
    | DuplicateParameter of name: string
    | DuplicateVariable of name: string
    | DuplicateCondition of name: string
    | DuplicateCursor of name: string
    | DuplicateHandler
    | UnknownVariable of name: string
    | UndeclaredVariable of name: string
    | UnknownCondition of name: string
    | UnknownCursor of name: string
    | VariableAfterCursorOrHandler
    | CursorAfterHandler
    | DeclarationAfterStatement
    | ReturnOutsideFunction
    | MissingReturn
    | InvalidSqlState of state: string
    | InvalidUserVariable of message: string
    | RedefiningLabel of name: string
    | UnknownLabel of operation: string * name: string

let validationError =
    function
    | DuplicateParameter name -> 1330, sprintf "Duplicate parameter: %s" name
    | DuplicateVariable name -> 1331, sprintf "Duplicate variable: %s" name
    | DuplicateCondition name -> 1332, sprintf "Duplicate condition: %s" name
    | DuplicateCursor name -> 1333, sprintf "Duplicate cursor: %s" name
    | DuplicateHandler -> 1413, "Duplicate handler declared in the same block"
    | UnknownVariable name -> 1193, sprintf "Unknown system variable '%s'" name
    | UndeclaredVariable name -> 1327, sprintf "Undeclared variable: %s" name
    | UnknownCondition name -> 1319, sprintf "Undefined CONDITION: %s" name
    | UnknownCursor name -> 1324, sprintf "Undefined CURSOR: %s" name
    | VariableAfterCursorOrHandler -> 1337, "Variable or condition declaration after cursor or handler declaration"
    | CursorAfterHandler -> 1338, "Cursor declaration after handler declaration"
    | DeclarationAfterStatement -> 1064, "Declarations must precede executable statements"
    | ReturnOutsideFunction -> 1313, "RETURN is only allowed in a FUNCTION"
    | MissingReturn -> 1320, "No RETURN found in FUNCTION"
    | InvalidSqlState state -> 1407, sprintf "Bad SQLSTATE: '%s'" state
    | InvalidUserVariable message -> 3061, message
    | RedefiningLabel name -> 1309, sprintf "Redefining label %s" name
    | UnknownLabel(operation, name) -> 1308, sprintf "%s with no matching label: %s" operation name

let rec private collectSqlStatements includeCursors =
    function
    | Sql statement
    | SelectInto(statement, _) -> [ statement ]
    | DeclareCursor(_, query) when includeCursors -> [ query ]
    | DeclareCursor _ -> []
    | TextSql _ -> []
    | Block(_, body)
    | Loop(_, body) -> body |> List.collect (collectSqlStatements includeCursors)
    | While(_, _, body)
    | Repeat(_, body, _) -> body |> List.collect (collectSqlStatements includeCursors)
    | If(_, whenTrue, whenFalse) ->
        (whenTrue @ whenFalse) |> List.collect (collectSqlStatements includeCursors)
    | Case(_, branches, otherwise) ->
        (branches |> List.collect (snd >> List.collect (collectSqlStatements includeCursors)))
        @ (otherwise |> Option.defaultValue [] |> List.collect (collectSqlStatements includeCursors))
    | DeclareHandler(_, _, body) -> collectSqlStatements includeCursors body
    | Declare _
    | DeclareCondition _
    | OpenCursor _
    | FetchCursor _
    | CloseCursor _
    | GetDiagnostics _
    | Signal _
    | Resignal _
    | SetLocal _
    | Return _
    | Leave _
    | Iterate _ -> []

let sqlStatements = collectSqlStatements true
let executableSqlStatements = collectSqlStatements false

let rec resultSetStatements =
    function
    | Sql((Ast.Select _ | Ast.Union _) as statement) -> [ statement ]
    | Block(_, body)
    | Loop(_, body) -> body |> List.collect resultSetStatements
    | While(_, _, body)
    | Repeat(_, body, _) -> body |> List.collect resultSetStatements
    | If(_, whenTrue, whenFalse) -> (whenTrue @ whenFalse) |> List.collect resultSetStatements
    | Case(_, branches, otherwise) ->
        (branches |> List.collect (snd >> List.collect resultSetStatements))
        @ (otherwise |> Option.defaultValue [] |> List.collect resultSetStatements)
    | DeclareHandler(_, _, body) -> resultSetStatements body
    | _ -> []

let rec textSqlStatements =
    function
    | TextSql sql -> [ sql ]
    | Block(_, body)
    | Loop(_, body)
    | While(_, _, body)
    | Repeat(_, body, _) -> body |> List.collect textSqlStatements
    | If(_, whenTrue, whenFalse) -> (whenTrue @ whenFalse) |> List.collect textSqlStatements
    | Case(_, branches, otherwise) ->
        (branches |> List.collect (snd >> List.collect textSqlStatements))
        @ (otherwise |> Option.defaultValue [] |> List.collect textSqlStatements)
    | DeclareHandler(_, _, body) -> textSqlStatements body
    | _ -> []

let rec expressions =
    function
    | Sql _
    | SelectInto _
    | DeclareCursor _
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
    | DeclareHandler(_, _, body) -> expressions body
    | Signal(_, information)
    | Resignal(_, information) -> information |> List.map snd
    | GetDiagnostics { Request = ConditionInformation(conditionNumber, _) } -> [ conditionNumber ]
    | GetDiagnostics _ -> []
    | DeclareCondition _ -> []
    | OpenCursor _
    | FetchCursor _
    | CloseCursor _ -> []
    | SetLocal(_, value) -> [ value ]
    | Return value -> [ value ]
    | Leave _
    | Iterate _ -> []

let declaredNames statements =
    statements
    |> List.choose (function
        | Declare declaration -> Some declaration.Name
        | _ -> None)
    |> Set.ofList

let declaredCursorNames statements =
    statements
    |> List.choose (function
        | DeclareCursor(name, _) -> Some name
        | _ -> None)
    |> Set.ofList

let conditionDefinitions inherited statements =
    statements
    |> List.fold
        (fun definitions statement ->
            match statement with
            | DeclareCondition(name, value) -> Map.add name value definitions
            | _ -> definitions)
        inherited

let handlers statements =
    statements
    |> List.choose (function
        | DeclareHandler(action, conditions, body) -> Some(action, conditions, body)
        | _ -> None)

let rec resolveCondition definitions =
    function
    | NamedCondition name -> definitions |> Map.tryFind name |> Option.bind (resolveCondition definitions)
    | condition -> Some condition

let private conditionSpecificity (error: SqlState.Error) =
    function
    | ErrorCode code when error.Code = code -> Some 3
    | SqlState state when error.State = state -> Some 2
    | SqlWarning when error.State.StartsWith("01", StringComparison.Ordinal) -> Some 1
    | NotFound when error.State.StartsWith("02", StringComparison.Ordinal) -> Some 1
    | SqlException
        when not (error.State.StartsWith("00", StringComparison.Ordinal))
             && not (error.State.StartsWith("01", StringComparison.Ordinal))
             && not (error.State.StartsWith("02", StringComparison.Ordinal)) ->
        Some 1
    | _ -> None

let tryHandler definitions statements error =
    handlers statements
    |> List.choose (fun (action, conditions, body) ->
        conditions
        |> List.choose (resolveCondition definitions >> Option.bind (conditionSpecificity error))
        |> List.sortDescending
        |> List.tryHead
        |> Option.map (fun specificity -> specificity, action, body))
    |> List.sortByDescending (fun (specificity, _, _) -> specificity)
    |> List.tryHead
    |> Option.map (fun (_, action, body) -> action, body)

let private defaultSignal (state: string) =
    let error =
        if state.StartsWith("01", StringComparison.Ordinal) then
            SqlState.createWithState 1642 state "Unhandled user-defined warning condition"
        elif state.StartsWith("02", StringComparison.Ordinal) then
            SqlState.createWithState 1643 state "Unhandled user-defined not found condition"
        else
            SqlState.createWithState 1644 state "Unhandled user-defined exception condition"

    { error with
        Information =
            error.Information
            |> Map.add "class_origin" ""
            |> Map.add "subclass_origin" "" }

let signalError
    (definitions: Map<string, ConditionValue>)
    (original: SqlState.Error option)
    (condition: ConditionValue option)
    (information: (string * Value) list)
    =
    let conditionError: Result<SqlState.Error, int * string> =
        match condition, original with
        | None, Some error -> Ok error
        | None, None -> Error(1645, "RESIGNAL when handler not active")
        | Some condition, _ ->
            match resolveCondition definitions condition with
            | Some(SqlState state) -> Ok(defaultSignal state)
            | Some _ -> Error(1646, "SIGNAL/RESIGNAL can only use a CONDITION defined with SQLSTATE")
            | None ->
                match condition with
                | NamedCondition name -> Error(1319, sprintf "Undefined CONDITION: %s" name)
                | _ -> Error(1646, "SIGNAL/RESIGNAL can only use a CONDITION defined with SQLSTATE")

    information
    |> List.fold
        (fun (state: Result<SqlState.Error, int * string>) (name, value) ->
            state
            |> Result.bind (fun (error: SqlState.Error) ->
                match name, Value.toText value with
                | _, None -> Error(1231, sprintf "Variable '%s' can't be set to the value of 'NULL'" name)
                | "mysql_errno", Some text ->
                    match Int32.TryParse text with
                    | true, code when code >= 1 && code <= 65535 -> Ok { error with Code = code }
                    | _ -> Error(1231, sprintf "Variable 'MYSQL_ERRNO' can't be set to the value of '%s'" text)
                | "message_text", Some text -> Ok { error with Message = text }
                | _, Some text -> Ok { error with Information = Map.add name text error.Information }))
        conditionError

let isWarning (error: SqlState.Error) =
    error.State.StartsWith("01", StringComparison.Ordinal)

type DiagnosticsSnapshot =
    { Conditions: Diagnostics.Condition list
      RowCount: int64 }

let diagnosticsForError current (error: SqlState.Error) =
    let condition =
        if isWarning error then
            Diagnostics.fromWarning error
        else
            Diagnostics.fromError error

    let alreadyCurrent =
        current.Conditions
        |> List.tryLast
        |> Option.exists (fun existing ->
            existing.Code = condition.Code
            && existing.State = condition.State
            && existing.Message = condition.Message)

    if alreadyCurrent then
        current
    else
        { Conditions = [ condition ]
          RowCount = if isWarning error then 0L else -1L }

let statementDiagnosticsValue snapshot =
    function
    | Number -> Some(VInt(int64 snapshot.Conditions.Length))
    | RowCount -> Some(VInt snapshot.RowCount)
    | _ -> None

let conditionDiagnosticsValue (condition: Diagnostics.Condition) =
    let origin name =
        condition.Information
        |> Map.tryFind name
        |> Option.defaultWith (fun () ->
            if condition.State.StartsWith("HY", StringComparison.Ordinal) then "MySQL" else "ISO 9075")

    function
    | ClassOrigin -> VString(origin "class_origin")
    | SubclassOrigin -> VString(origin "subclass_origin")
    | ReturnedSqlState -> VString condition.State
    | MessageText -> VString condition.Message
    | MySqlErrorNumber -> VInt(int64 condition.Code)
    | ConstraintCatalog -> VString(Map.tryFind "constraint_catalog" condition.Information |> Option.defaultValue "")
    | ConstraintSchema -> VString(Map.tryFind "constraint_schema" condition.Information |> Option.defaultValue "")
    | ConstraintName -> VString(Map.tryFind "constraint_name" condition.Information |> Option.defaultValue "")
    | CatalogName -> VString(Map.tryFind "catalog_name" condition.Information |> Option.defaultValue "")
    | SchemaName -> VString(Map.tryFind "schema_name" condition.Information |> Option.defaultValue "")
    | TableName -> VString(Map.tryFind "table_name" condition.Information |> Option.defaultValue "")
    | ColumnName -> VString(Map.tryFind "column_name" condition.Information |> Option.defaultValue "")
    | CursorName -> VString(Map.tryFind "cursor_name" condition.Information |> Option.defaultValue "")
    | Number
    | RowCount -> VNull

let tryConditionDiagnostics number snapshot =
    if number < 1 || number > snapshot.Conditions.Length then
        None
    else
        Some(List.item (number - 1) snapshot.Conditions)

let tryDiagnosticsConditionNumber value =
    let fromDecimal number =
        if number = Decimal.Truncate number && number >= 1M && number <= decimal Int32.MaxValue then
            Some(int number)
        else
            None

    let fromBinary bytes =
        let beyondMaximum = uint64 Int32.MaxValue + 1UL

        bytes
        |> Array.fold (fun value byte -> min beyondMaximum (value * 256UL + uint64 byte)) 0UL
        |> fun number -> if number <= uint64 Int32.MaxValue then Some(int number) else None

    match value with
    | VInt number -> fromDecimal (decimal number)
    | VUInt number when number <= uint64 Int32.MaxValue -> Some(int number)
    | VDecimal number -> fromDecimal number
    | VDouble number when Double.IsFinite number && number = Math.Truncate number ->
        if number >= 1.0 && number <= float Int32.MaxValue then Some(int number) else None
    | VBit(_, number) when number <= uint64 Int32.MaxValue -> Some(int number)
    | VBytes bytes -> fromBinary bytes
    | VString text ->
        match
            Decimal.TryParse(
                text,
                Globalization.NumberStyles.Number ||| Globalization.NumberStyles.AllowExponent,
                Globalization.CultureInfo.InvariantCulture
            )
        with
        | true, number -> fromDecimal number
        | _ -> None
    | _ -> None

let diagnosticsAssignments snapshot request conditionNumber =
    match request with
    | StatementInformation assignments ->
        assignments
        |> List.choose (fun (target, item) ->
            statementDiagnosticsValue snapshot item |> Option.map (fun value -> target, value))
        |> Some
    | ConditionInformation(_, assignments) ->
        conditionNumber
        |> Option.bind (fun number -> tryConditionDiagnostics number snapshot)
        |> Option.map (fun condition ->
            assignments
            |> List.map (fun (target, item) -> target, conditionDiagnosticsValue condition item))

let private restoreOuterDeclarations declared statements (before: Map<string, 'value>) after =
    let shadowed = declared statements

    before
    |> Map.map (fun name value ->
        if Set.contains name shadowed then
            value
        else
            after |> Map.tryFind name |> Option.defaultValue value)

let restoreOuterScope statements before after =
    restoreOuterDeclarations declaredNames statements before after

let restoreOuterCursors statements before after =
    restoreOuterDeclarations declaredCursorNames statements before after

let cursor query =
    { Query = query
      Rows = None
      ColumnCount = 0
      Position = 0 }

let private cursorError code message = SqlState.create code message

let tryOpenCursor name cursors =
    match Map.tryFind name cursors with
    | None -> Result.Error(cursorError 1324 (sprintf "Undefined CURSOR: %s" name))
    | Some { Rows = Some _ } -> Result.Error(cursorError 1325 "Cursor is already open")
    | Some cursor -> Ok cursor

let setCursorRows name columnCount rows cursors =
    cursors
    |> Map.change name (Option.map (fun cursor ->
        { cursor with
            Rows = Some rows
            ColumnCount = columnCount
            Position = 0 }))

let tryFetchCursorRow name targetCount cursors =
    match Map.tryFind name cursors with
    | None -> Result.Error(cursorError 1324 (sprintf "Undefined CURSOR: %s" name))
    | Some { Rows = None } -> Result.Error(cursorError 1326 "Cursor is not open")
    | Some cursor when cursor.ColumnCount <> targetCount ->
        Result.Error(cursorError 1328 "Incorrect number of FETCH variables")
    | Some cursor ->
        match cursor.Rows |> Option.bind (Array.tryItem cursor.Position) with
        | None ->
            Result.Error(
                SqlState.createWithState 1329 "02000" "No data - zero rows fetched, selected, or processed"
            )
        | Some row ->
            let cursors = Map.add name { cursor with Position = cursor.Position + 1 } cursors
            Ok(row, cursors)

let tryCloseCursor name cursors =
    match Map.tryFind name cursors with
    | None -> Result.Error(cursorError 1324 (sprintf "Undefined CURSOR: %s" name))
    | Some { Rows = None } -> Result.Error(cursorError 1326 "Cursor is not open")
    | Some cursor -> Ok(Map.add name { cursor with Rows = None; Position = 0 } cursors)

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
    | Into
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

type private ValidationScope =
    { Names: Set<string>
      DeclaredVariables: Set<string>
      Conditions: Map<string, ConditionValue>
      DeclaredConditions: Set<string>
      Cursors: Set<string>
      DeclaredCursors: Set<string>
      HasCursorDeclaration: bool
      HasHandlerDeclaration: bool
      HasExecutableStatement: bool
      HandlerConditions: Set<ConditionValue>
      Labels: (string * LabelKind) list }

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
      Until, "UNTIL"
      Into, "INTO" ]

let private parameterPattern =
    Regex(
        @"^(?:(?<mode>INOUT|IN|OUT)\s+)?(?<name>`(?:``|[^`])+`|[A-Za-z_$][A-Za-z0-9_$]*)\s+(?<type>[\s\S]+)$",
        RegexOptions.IgnoreCase
    )

let private labelPattern = @"`(?:``|[^`])+`|[A-Za-z_][A-Za-z0-9_$]*"

let private triviaAtom =
    @"(?:\s|/\*(?>[\s\S]*?\*/)|#[^\r\n]*(?:\r\n|\r|\n|$)|--(?=\s)[^\r\n]*(?:\r\n|\r|\n|$))"

let private triviaPattern = sprintf "(?:%s)*" triviaAtom
let private separatorPattern = sprintf "(?:%s)+" triviaAtom

let private sqlStateValuePattern =
    sprintf "SQLSTATE%s(?:VALUE%s)?'[0-9A-Za-z]{5}'" separatorPattern separatorPattern

let private declaredConditionValuePattern = sprintf "(?:%s|[0-9]+)" sqlStateValuePattern
let private signalConditionValuePattern = sprintf "(?:%s|%s)" sqlStateValuePattern labelPattern

let private handlerConditionValuePattern =
    sprintf
        "(?:%s|SQLWARNING|NOT%sFOUND|SQLEXCEPTION|[0-9]+|%s)"
        sqlStateValuePattern
        separatorPattern
        labelPattern

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

let private conditionDeclarationPattern =
    Regex(
        sprintf @"^DECLARE%s(?<name>%s)%sCONDITION%sFOR%s(?<condition>%s)$" separatorPattern labelPattern separatorPattern separatorPattern separatorPattern declaredConditionValuePattern,
        RegexOptions.IgnoreCase
    )

let private handlerPrefixPattern =
    Regex(
        sprintf
            @"^DECLARE%s(?<action>CONTINUE|EXIT)%sHANDLER%sFOR%s(?<conditions>%s(?:%s,%s%s)*)%s"
            separatorPattern
            separatorPattern
            separatorPattern
            separatorPattern
            handlerConditionValuePattern
            triviaPattern
            triviaPattern
            handlerConditionValuePattern
            separatorPattern,
        RegexOptions.IgnoreCase
    )

let private cursorDeclarationPattern =
    Regex(
        sprintf
            @"^DECLARE%s(?<name>%s)%sCURSOR%sFOR%s(?<query>[\s\S]+)$"
            separatorPattern
            labelPattern
            separatorPattern
            separatorPattern
            separatorPattern,
        RegexOptions.IgnoreCase
    )

let private openCursorPattern =
    Regex(sprintf @"^OPEN%s(?<name>%s)$" separatorPattern labelPattern, RegexOptions.IgnoreCase)

let private fetchCursorPattern =
    Regex(
        sprintf
            @"^FETCH%s(?:(?:NEXT%s)?FROM%s)?(?<name>%s)%sINTO%s(?<targets>(?:%s)(?:%s,%s(?:%s))*)$"
            separatorPattern
            separatorPattern
            separatorPattern
            labelPattern
            separatorPattern
            separatorPattern
            labelPattern
            triviaPattern
            triviaPattern
            labelPattern,
        RegexOptions.IgnoreCase
    )

let private closeCursorPattern =
    Regex(sprintf @"^CLOSE%s(?<name>%s)$" separatorPattern labelPattern, RegexOptions.IgnoreCase)

let private returnPattern =
    Regex(sprintf @"^RETURN%s(?<value>[\s\S]+)$" separatorPattern, RegexOptions.IgnoreCase)

let private signalPattern =
    Regex(
        sprintf @"^SIGNAL%s(?<condition>%s)(?:%sSET%s(?<information>[\s\S]+))?$" separatorPattern signalConditionValuePattern separatorPattern separatorPattern,
        RegexOptions.IgnoreCase
    )

let private resignalPattern =
    Regex(
        sprintf @"^RESIGNAL(?:%s(?<condition>%s))?(?:%sSET%s(?<information>[\s\S]+))?$" separatorPattern signalConditionValuePattern separatorPattern separatorPattern,
        RegexOptions.IgnoreCase
    )

let private quotedUserVariablePattern =
    @"(?:`(?:``|[^`])+`|'(?:''|\\.|[^'])*'|""(?:""""|\\.|[^""])*""|[A-Za-z0-9_.$]+)"

let private diagnosticsTargetPattern =
    sprintf "(?:@%s|%s)" quotedUserVariablePattern labelPattern

let private conditionNumberPattern =
    let numeric = @"(?:0[xX][0-9A-Fa-f]+|(?:[0-9]+(?:\.[0-9]*)?|\.[0-9]+)(?:[eE][+-]?[0-9]+)?)"
    let text = @"(?:_[A-Za-z0-9_]+)?'(?:''|\\.|[^'])*'"

    sprintf
        "(?:@@(?:GLOBAL\\.|SESSION\\.)?[A-Za-z_][A-Za-z0-9_$]*|@%s|%s|%s|%s)"
        quotedUserVariablePattern
        labelPattern
        numeric
        text

let private diagnosticsPattern =
    Regex(
        sprintf
            @"^GET%s(?:(?<area>CURRENT|STACKED)%s)?DIAGNOSTICS%s(?<request>[\s\S]+)$"
            separatorPattern
            separatorPattern
            separatorPattern,
        RegexOptions.IgnoreCase
    )

let private diagnosticsConditionPattern =
    Regex(
        sprintf
            @"^CONDITION%s(?<number>%s)%s(?<assignments>[\s\S]+)$"
            separatorPattern
            conditionNumberPattern
            separatorPattern,
        RegexOptions.IgnoreCase
    )

let private diagnosticsAssignmentPattern =
    Regex(
        sprintf
            @"^(?<target>%s)%s=%s(?<item>[A-Za-z_]+)$"
            diagnosticsTargetPattern
            triviaPattern
            triviaPattern,
        RegexOptions.IgnoreCase
    )

let private signalInformationPattern =
    Regex(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>[\s\S]+)$", RegexOptions.IgnoreCase)

let private signalInformationNames =
    set
        [ "catalog_name"
          "class_origin"
          "column_name"
          "constraint_catalog"
          "constraint_name"
          "constraint_schema"
          "cursor_name"
          "message_text"
          "mysql_errno"
          "schema_name"
          "subclass_origin"
          "table_name" ]

let private leadingTriviaPattern = Regex(sprintf "^%s" triviaPattern)

let private sqlStatePattern =
    Regex(
        sprintf "^SQLSTATE%s(?:VALUE%s)?'(?<state>[0-9A-Za-z]{5})'$" separatorPattern separatorPattern,
        RegexOptions.IgnoreCase
    )

let private leavePattern = Regex(sprintf @"^LEAVE\s+(?<label>%s)$" labelPattern, RegexOptions.IgnoreCase)
let private iteratePattern = Regex(sprintf @"^ITERATE\s+(?<label>%s)$" labelPattern, RegexOptions.IgnoreCase)

let private normalizeLabel (label: string) =
    label.Trim('`').Replace("``", "`").ToLowerInvariant()

let private trimLeadingTrivia text = leadingTriviaPattern.Replace(text, "", 1).Trim()

let private parseConditionValue (text: string) =
    let text = trimLeadingTrivia text
    let state = sqlStatePattern.Match text

    match text.ToUpperInvariant() with
    | "SQLWARNING" -> Ok SqlWarning
    | "NOT FOUND" -> Ok NotFound
    | "SQLEXCEPTION" -> Ok SqlException
    | _ when state.Success -> Ok(SqlState(state.Groups.["state"].Value.ToUpperInvariant()))
    | _ ->
        match Int32.TryParse text with
        | true, code -> Ok(ErrorCode code)
        | _ when Regex.IsMatch(text, sprintf "^(?:%s)$" labelPattern) -> Ok(NamedCondition(normalizeLabel text))
        | _ -> Error(sprintf "Invalid condition value: %s" text)

let private parseSignalInformation options text =
    if String.IsNullOrWhiteSpace text then
        Ok []
    else
        Parser.splitTopLevelCommaSeparatedWithOptions options text
        |> traverse (fun item ->
            let matched = signalInformationPattern.Match(trimLeadingTrivia item)

            if not matched.Success then
                Error(sprintf "Invalid condition information item: %s" item)
            else
                let name = matched.Groups.["name"].Value.ToLowerInvariant()

                if not (Set.contains name signalInformationNames) then
                    Error(sprintf "Unknown condition information item: %s" name)
                else
                    Parser.parseExpressionWithOptions options matched.Groups.["value"].Value
                    |> Result.map (fun value -> name, value))
        |> Result.bind (fun information ->
            match information |> List.countBy fst |> List.tryFind (fun (_, count) -> count > 1) with
            | Some(name, _) -> Error(sprintf "Duplicate condition information item '%s'" name)
            | None -> Ok information)

let private parseDiagnosticsTarget (text: string) =
    let text = trimLeadingTrivia text

    if text.StartsWith('@') then
        Parser.parseUserVariableTarget text |> Result.map UserVariable
    elif Regex.IsMatch(text, sprintf "^(?:%s)$" labelPattern) then
        Ok(LocalVariable(normalizeLabel text))
    else
        Error(sprintf "Invalid diagnostics target: %s" text)

let private parseDiagnosticsItem (text: string) =
    match text.ToUpperInvariant() with
    | "NUMBER" -> Ok Number
    | "ROW_COUNT" -> Ok RowCount
    | "CLASS_ORIGIN" -> Ok ClassOrigin
    | "SUBCLASS_ORIGIN" -> Ok SubclassOrigin
    | "RETURNED_SQLSTATE" -> Ok ReturnedSqlState
    | "MESSAGE_TEXT" -> Ok MessageText
    | "MYSQL_ERRNO" -> Ok MySqlErrorNumber
    | "CONSTRAINT_CATALOG" -> Ok ConstraintCatalog
    | "CONSTRAINT_SCHEMA" -> Ok ConstraintSchema
    | "CONSTRAINT_NAME" -> Ok ConstraintName
    | "CATALOG_NAME" -> Ok CatalogName
    | "SCHEMA_NAME" -> Ok SchemaName
    | "TABLE_NAME" -> Ok TableName
    | "COLUMN_NAME" -> Ok ColumnName
    | "CURSOR_NAME" -> Ok CursorName
    | _ -> Error(sprintf "Unknown diagnostics item: %s" text)

let private parseDiagnosticsAssignments options text =
    Parser.splitTopLevelCommaSeparatedWithOptions options text
    |> traverse (fun assignment ->
        let matched = diagnosticsAssignmentPattern.Match(trimLeadingTrivia assignment)

        if not matched.Success then
            Error(sprintf "Invalid diagnostics assignment: %s" assignment)
        else
            parseDiagnosticsTarget matched.Groups.["target"].Value
            |> Result.bind (fun target ->
                parseDiagnosticsItem matched.Groups.["item"].Value
                |> Result.map (fun item -> target, item)))

let parseDiagnostics options (text: string) =
    let matched = diagnosticsPattern.Match(text.Trim())

    if not matched.Success then
        Ok None
    else
        let area =
            if matched.Groups.["area"].Value.Equals("STACKED", StringComparison.OrdinalIgnoreCase) then
                Stacked
            else
                Current

        let request = trimLeadingTrivia matched.Groups.["request"].Value
        let condition = diagnosticsConditionPattern.Match request

        if condition.Success then
            Parser.parseExpressionWithOptions options condition.Groups.["number"].Value
            |> Result.bind (fun conditionNumber ->
                parseDiagnosticsAssignments options condition.Groups.["assignments"].Value
                |> Result.bind (fun assignments ->
                    if assignments |> List.exists (snd >> function Number | RowCount -> true | _ -> false) then
                        Error "Statement diagnostics items are not valid in CONDITION"
                    else
                        Ok(
                            Some
                                { Area = area
                                  Request = ConditionInformation(conditionNumber, assignments) }
                        )))
        else
            parseDiagnosticsAssignments options request
            |> Result.bind (fun assignments ->
                if assignments |> List.forall (snd >> function Number | RowCount -> true | _ -> false) then
                    Ok(Some { Area = area; Request = StatementInformation assignments })
                else
                    Error "Condition diagnostics items require CONDITION")

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

let private selectIntoTargets =
    Regex(
        sprintf @"\G(?<target>%s)(?:%s,%s(?<target>%s))*" labelPattern triviaPattern triviaPattern labelPattern,
        RegexOptions.IgnoreCase
    )

let private tryParseSelectInto (options: Parser.ParserOptions) (text: string) =
    if not (wordAt text (skipTrivia text 0) "SELECT") then
        Ok None
    else
        match findBoundary (Set.singleton Into) text 0 with
        | None -> Ok None
        | Some(intoStart, afterInto, _) ->
            let targetStart = skipTrivia text afterInto
            let matched = selectIntoTargets.Match(text, targetStart)

            if not matched.Success || matched.Index <> targetStart then
                Ok None
            else
                let targets =
                    matched.Groups.["target"].Captures
                    |> Seq.cast<Capture>
                    |> Seq.map (_.Value >> normalizeLabel)
                    |> List.ofSeq

                let queryText = text.Remove(intoStart, matched.Index + matched.Length - intoStart)

                Parser.parseStoredStatementWithOptions options queryText
                |> Result.bind (function
                    | (Ast.Select _ | Ast.Union _) as query -> Ok(Some(SelectInto(query, targets)))
                    | _ -> Error "SELECT INTO requires a SELECT statement")

let parseParameters (options: Parser.ParserOptions) (text: string) : Result<Parameter list, string> =
    let parseOne value =
        let matched = parameterPattern.Match value

        if not matched.Success then
            Error(sprintf "Invalid routine parameter: %s" value)
        else
            let displayName = matched.Groups.["name"].Value.Trim('`').Replace("``", "`")
            let name = displayName.ToLowerInvariant()
            let mode =
                match matched.Groups.["mode"].Value.ToUpperInvariant() with
                | "OUT" -> Out
                | "INOUT" -> InOut
                | _ -> In

            Parser.parseRoutineParameterTypeWithOptions options matched.Groups.["type"].Value
            |> Result.map (fun (columnType, charset, collation) ->
                { Name = name
                  DisplayName = displayName
                  ColumnType = columnType
                  Charset = charset
                  Collation = collation
                  Mode = mode })

    if String.IsNullOrWhiteSpace text then
        Ok []
    else
        Parser.splitNonEmptyTopLevelCommaSeparatedWithOptions options text
        |> Result.bind (traverse parseOne)

let parseArguments (options: Parser.ParserOptions) (text: string) : Result<Expr list, string> =
    if String.IsNullOrWhiteSpace text then
        Ok []
    else
        Parser.splitNonEmptyTopLevelCommaSeparatedWithOptions options text
        |> Result.bind (traverse (Parser.parseExpressionWithOptions options))

let private parseWithFallback
    (options: Parser.ParserOptions)
    (allowLocalSelectInto: bool)
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
            let handler = handlerPrefixPattern.Match(inner.Substring(offset))

            match label with
            | None when handler.Success -> parseHandler offset handler
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

        and parseHandler offset (matched: Match) =
            let action = if matched.Groups.["action"].Value.Equals("EXIT", StringComparison.OrdinalIgnoreCase) then Exit else Continue

            Parser.splitTopLevelCommaSeparatedWithOptions options matched.Groups.["conditions"].Value
            |> traverse parseConditionValue
            |> Result.bind (fun conditions ->
                parseStructured None (offset + matched.Length)
                |> Result.map (fun (body, finish) -> DeclareHandler(action, conditions, body), finish))

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
            let conditionDeclaration = conditionDeclarationPattern.Match text
            let cursorDeclaration = cursorDeclarationPattern.Match text
            let openCursor = openCursorPattern.Match text
            let fetchCursor = fetchCursorPattern.Match text
            let closeCursor = closeCursorPattern.Match text
            let returnStatement = returnPattern.Match text
            let assignment = assignmentPattern.Match text
            let leave = leavePattern.Match text
            let iterate = iteratePattern.Match text
            let signal = signalPattern.Match text
            let resignal = resignalPattern.Match text

            match parseDiagnostics options text with
            | Error error -> Error error
            | Ok(Some diagnostics) -> Ok(GetDiagnostics diagnostics)
            | Ok None when conditionDeclaration.Success ->
                parseConditionValue conditionDeclaration.Groups.["condition"].Value
                |> Result.map (fun condition ->
                    DeclareCondition(normalizeLabel conditionDeclaration.Groups.["name"].Value, condition))
            | Ok None when cursorDeclaration.Success ->
                Parser.parseStoredStatementWithOptions options cursorDeclaration.Groups.["query"].Value
                |> Result.bind (function
                    | (Ast.Select _ | Ast.Union _) as query ->
                        Ok(DeclareCursor(normalizeLabel cursorDeclaration.Groups.["name"].Value, query))
                    | _ -> Error "Cursor declaration requires a SELECT statement")
            | Ok None when declaration.Success ->
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
            | Ok None when assignment.Success ->
                Parser.parseExpressionWithOptions options assignment.Groups.["value"].Value
                |> Result.map (fun value -> SetLocal(assignment.Groups.["name"].Value.ToLowerInvariant(), value))
            | Ok None when openCursor.Success ->
                Ok(OpenCursor(normalizeLabel openCursor.Groups.["name"].Value))
            | Ok None when fetchCursor.Success ->
                let targets =
                    Parser.splitTopLevelCommaSeparatedWithOptions options fetchCursor.Groups.["targets"].Value
                    |> List.map normalizeLabel

                Ok(FetchCursor(normalizeLabel fetchCursor.Groups.["name"].Value, targets))
            | Ok None when closeCursor.Success ->
                Ok(CloseCursor(normalizeLabel closeCursor.Groups.["name"].Value))
            | Ok None when returnStatement.Success ->
                Parser.parseExpressionWithOptions options returnStatement.Groups.["value"].Value
                |> Result.map Return
            | Ok None when leave.Success ->
                Ok(Leave(normalizeLabel leave.Groups.["label"].Value))
            | Ok None when iterate.Success ->
                Ok(Iterate(normalizeLabel iterate.Groups.["label"].Value))
            | Ok None when signal.Success ->
                parseConditionValue signal.Groups.["condition"].Value
                |> Result.bind (fun condition ->
                    parseSignalInformation options signal.Groups.["information"].Value
                    |> Result.map (fun information -> Signal(condition, information)))
            | Ok None when resignal.Success ->
                let condition =
                    if resignal.Groups.["condition"].Success then
                        parseConditionValue resignal.Groups.["condition"].Value |> Result.map Some
                    else
                        Ok None

                condition
                |> Result.bind (fun condition ->
                    parseSignalInformation options resignal.Groups.["information"].Value
                    |> Result.map (fun information -> Resignal(condition, information)))
            | Ok None ->
                match if allowLocalSelectInto then tryParseSelectInto options text else Ok None with
                | Ok(Some statement) -> Ok statement
                | Error error -> Error error
                | Ok None ->
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
                    match rootLabel with
                    | Some label -> Ok [ Block(Some label, statements) ]
                    | None -> Ok statements)
    else
        let body = body.Trim()
        let returned = returnPattern.Match body
        let assignment = assignmentPattern.Match body

        if returned.Success then
            Parser.parseExpressionWithOptions options returned.Groups.["value"].Value
            |> Result.map (fun value -> [ Return value ])
        elif assignment.Success then
            Parser.parseExpressionWithOptions options assignment.Groups.["value"].Value
            |> Result.map (fun value ->
                [ SetLocal(assignment.Groups.["name"].Value.ToLowerInvariant(), value) ])
        else
            match if allowLocalSelectInto then tryParseSelectInto options body else Ok None with
            | Ok(Some statement) -> Ok [ statement ]
            | Error error -> Error error
            | Ok None ->
                match Parser.parseStoredStatementWithOptions options body with
                | Ok statement -> Ok [ Sql statement ]
                | Error _
                    when not (body.TrimStart().StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase))
                         && isSupportedText body ->
                    Ok [ TextSql body ]
                | Error error -> Error error

let parse (options: Parser.ParserOptions) (body: string) : Result<Statement list, string> =
    parseWithFallback options false (fun _ -> false) body

let private callIdentifier = @"(?:`(?:``|[^`])+`|[A-Za-z_$][A-Za-z0-9_$]*)"

let private callStatement =
    Regex(
        sprintf @"^\s*CALL\s+(?<name>%s(?:\.%s)?)(?:\s*\((?<arguments>.*)\))?\s*$" callIdentifier callIdentifier,
        RegexOptions.IgnoreCase ||| RegexOptions.Singleline
    )

let tryCall (options: Parser.ParserOptions) (sql: string) =
    let matched = callStatement.Match sql

    if
        matched.Success
        && (not matched.Groups.["arguments"].Success
            || (parseArguments options matched.Groups.["arguments"].Value |> Result.isOk))
    then
        Some(matched.Groups.["name"].Value)
    else
        None

let parseTrigger (options: Parser.ParserOptions) (body: string) : Result<Statement list, string> =
    parseWithFallback options false (tryCall options >> Option.isSome) body

let parseRoutine (options: Parser.ParserOptions) isSupportedText body =
    parseWithFallback options true isSupportedText body

let private validateProgram allowReturn (parameters: Parameter list) (statements: Statement list) =
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

    let validSqlState (state: string) =
        state.Length = 5
        && not (state.StartsWith("00", StringComparison.Ordinal))
        && state |> Seq.forall Char.IsLetterOrDigit

    let resolve scope condition =
        match condition with
        | SqlState state when not (validSqlState state) -> Error(InvalidSqlState state)
        | NamedCondition name ->
            match Map.tryFind name scope.Conditions with
            | None -> Error(UnknownCondition name)
            | Some condition ->
                match condition with
                | SqlState state when not (validSqlState state) -> Error(InvalidSqlState state)
                | resolved -> Ok resolved
        | resolved -> Ok resolved

    let rec validateStatements scope =
        let afterStatement =
            { scope with
                HasExecutableStatement = true }

        function
        | [] -> Ok scope
        | Sql _ :: rest
        | TextSql _ :: rest -> validateStatements afterStatement rest
        | SelectInto(_, targets) :: _ when targets |> List.exists (fun name -> not (Set.contains name scope.Names)) ->
            targets
            |> List.find (fun name -> not (Set.contains name scope.Names))
            |> UndeclaredVariable
            |> Error
        | SelectInto _ :: rest -> validateStatements afterStatement rest
        | Declare _ :: _ when scope.HasExecutableStatement -> Error DeclarationAfterStatement
        | Declare _ :: _ when scope.HasCursorDeclaration || scope.HasHandlerDeclaration ->
            Error VariableAfterCursorOrHandler
        | Declare declaration :: _ when Set.contains declaration.Name scope.DeclaredVariables ->
            Error(DuplicateVariable declaration.Name)
        | Declare declaration :: rest ->
            validateStatements
                { scope with
                    Names = Set.add declaration.Name scope.Names
                    DeclaredVariables = Set.add declaration.Name scope.DeclaredVariables }
                rest
        | DeclareCondition _ :: _ when scope.HasExecutableStatement -> Error DeclarationAfterStatement
        | DeclareCondition _ :: _ when scope.HasCursorDeclaration || scope.HasHandlerDeclaration ->
            Error VariableAfterCursorOrHandler
        | DeclareCondition(name, _) :: _ when Set.contains name scope.DeclaredConditions ->
            Error(DuplicateCondition name)
        | DeclareCondition(name, condition) :: rest ->
            resolve scope condition
            |> Result.bind (fun condition ->
                match condition with
                | ErrorCode _
                | SqlState _ ->
                    validateStatements
                        { scope with
                            Conditions = Map.add name condition scope.Conditions
                            DeclaredConditions = Set.add name scope.DeclaredConditions }
                        rest
                | _ -> Error(UnknownCondition name))
        | DeclareCursor _ :: _ when scope.HasExecutableStatement -> Error DeclarationAfterStatement
        | DeclareCursor _ :: _ when scope.HasHandlerDeclaration -> Error CursorAfterHandler
        | DeclareCursor(name, _) :: _ when Set.contains name scope.DeclaredCursors -> Error(DuplicateCursor name)
        | DeclareCursor(name, _) :: rest ->
            validateStatements
                { scope with
                    Cursors = Set.add name scope.Cursors
                    DeclaredCursors = Set.add name scope.DeclaredCursors
                    HasCursorDeclaration = true }
                rest
        | DeclareHandler _ :: _ when scope.HasExecutableStatement -> Error DeclarationAfterStatement
        | DeclareHandler(action, conditions, body) :: rest ->
            conditions
            |> traverse (resolve scope)
            |> Result.bind (fun resolved ->
                if resolved |> List.exists (fun condition -> Set.contains condition scope.HandlerConditions) then
                    Error DuplicateHandler
                else
                    validateStatements afterStatement [ body ]
                    |> Result.bind (fun _ ->
                        validateStatements
                            { scope with
                                HasHandlerDeclaration = true
                                HandlerConditions = Set.union scope.HandlerConditions (Set.ofList resolved) }
                            rest))
        | OpenCursor name :: _ when not (Set.contains name scope.Cursors) -> Error(UnknownCursor name)
        | CloseCursor name :: _ when not (Set.contains name scope.Cursors) -> Error(UnknownCursor name)
        | FetchCursor(name, _) :: _ when not (Set.contains name scope.Cursors) -> Error(UnknownCursor name)
        | FetchCursor(_, targets) :: rest ->
            match targets |> List.tryFind (fun name -> not (Set.contains name scope.Names)) with
            | Some name -> Error(UndeclaredVariable name)
            | None -> validateStatements afterStatement rest
        | OpenCursor _ :: rest
        | CloseCursor _ :: rest -> validateStatements afterStatement rest
        | SetLocal(name, _) :: _ when not (Set.contains name scope.Names) -> Error(UnknownVariable name)
        | SetLocal _ :: rest -> validateStatements afterStatement rest
        | GetDiagnostics diagnostics :: rest ->
            let assignments =
                match diagnostics.Request with
                | StatementInformation assignments
                | ConditionInformation(_, assignments) -> assignments

            assignments
            |> traverse (fun (target, _) ->
                match target with
                | LocalVariable name when not (Set.contains name scope.Names) -> Error(UnknownVariable name)
                | UserVariable variable ->
                    match UserVariableRef.validationError variable with
                    | Some message -> Error(InvalidUserVariable message)
                    | None -> Ok()
                | LocalVariable _ -> Ok())
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Return _ :: _ when not allowReturn -> Error ReturnOutsideFunction
        | Return _ :: rest -> validateStatements afterStatement rest
        | If(_, whenTrue, whenFalse) :: rest ->
            validateStatements afterStatement whenTrue
            |> Result.bind (fun _ -> validateStatements afterStatement whenFalse)
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Case(_, branches, otherwise) :: rest ->
            branches
            |> traverse (snd >> validateStatements afterStatement)
            |> Result.bind (fun _ -> validateStatements afterStatement (otherwise |> Option.defaultValue []))
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Block(label, body) :: rest ->
            addLabel label LabelKind.Block scope.Labels
            |> Result.bind (fun labels ->
                validateStatements
                    { scope with
                        DeclaredVariables = Set.empty
                        DeclaredConditions = Set.empty
                        DeclaredCursors = Set.empty
                        HasCursorDeclaration = false
                        HasHandlerDeclaration = false
                        HasExecutableStatement = false
                        HandlerConditions = Set.empty
                        Labels = labels }
                    body)
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | While(label, _, body) :: rest
        | Repeat(label, body, _) :: rest
        | Loop(label, body) :: rest ->
            addLabel label LabelKind.Loop scope.Labels
            |> Result.bind (fun labels -> validateStatements { afterStatement with Labels = labels } body)
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Signal(condition, _) :: rest ->
            resolve scope condition |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Resignal(condition, _) :: rest ->
            condition
            |> Option.map (resolve scope)
            |> Option.defaultValue (Ok(ErrorCode 0))
            |> Result.bind (fun _ -> validateStatements afterStatement rest)
        | Leave label :: _ when scope.Labels |> List.exists (fun (name, _) -> name = label) |> not ->
            Error(UnknownLabel("LEAVE", label))
        | Iterate label :: _
            when scope.Labels |> List.exists (fun (name, kind) -> name = label && kind = LabelKind.Loop) |> not ->
            Error(UnknownLabel("ITERATE", label))
        | Leave _ :: rest
        | Iterate _ :: rest -> validateStatements afterStatement rest

    addParameters Set.empty parameters
    |> Result.bind (fun names ->
        validateStatements
            { Names = names
              DeclaredVariables = names
              Conditions = Map.empty
              DeclaredConditions = Set.empty
              Cursors = Set.empty
              DeclaredCursors = Set.empty
              HasCursorDeclaration = false
              HasHandlerDeclaration = false
              HasExecutableStatement = false
              HandlerConditions = Set.empty
              Labels = [] }
            statements)
    |> Result.map ignore

let validate parameters statements = validateProgram false parameters statements

let rec private containsReturn =
    function
    | Return _ -> true
    | Block(_, body)
    | Loop(_, body)
    | While(_, _, body)
    | Repeat(_, body, _) -> List.exists containsReturn body
    | If(_, whenTrue, whenFalse) -> List.exists containsReturn whenTrue || List.exists containsReturn whenFalse
    | Case(_, branches, otherwise) ->
        (branches |> List.exists (snd >> List.exists containsReturn))
        || (otherwise |> Option.exists (List.exists containsReturn))
    | DeclareHandler(_, _, body) -> containsReturn body
    | _ -> false

let validateFunction parameters statements =
    validateProgram true parameters statements
    |> Result.bind (fun () ->
        if List.exists containsReturn statements then Ok() else Error MissingReturn)
