module internal Fsdb.Sql.SqlText

open System
open Fsdb.Ast
open Fsdb.Value

let columnType =
    let quotedList values = values |> List.map (sprintf "'%s'") |> String.concat ","
    let unsigned enabled = if enabled then " unsigned" else ""

    function
    | TTinyInt value -> "tinyint" + unsigned value
    | TBool -> "tinyint(1)"
    | TSmallInt value -> "smallint" + unsigned value
    | TMediumInt value -> "mediumint" + unsigned value
    | TInt value -> "int" + unsigned value
    | TBigInt value -> "bigint" + unsigned value
    | TBit width -> sprintf "bit(%d)" width
    | TChar length -> sprintf "char(%d)" length
    | TVarchar length -> sprintf "varchar(%d)" length
    | TTinyText -> "tinytext"
    | TText -> "text"
    | TMediumText -> "mediumtext"
    | TLongText -> "longtext"
    | TBinary length -> sprintf "binary(%d)" length
    | TVarBinary length -> sprintf "varbinary(%d)" length
    | TTinyBlob -> "tinyblob"
    | TBlob -> "blob"
    | TMediumBlob -> "mediumblob"
    | TLongBlob -> "longblob"
    | TEnum values -> sprintf "enum(%s)" (quotedList values)
    | TSet values -> sprintf "set(%s)" (quotedList values)
    | TDecimal(precision, scale, isUnsigned) ->
        sprintf "decimal(%d,%d)%s" precision scale (unsigned isUnsigned)
    | TDouble value -> "double" + unsigned value
    | TFloat value -> "float" + unsigned value
    | TDate -> "date"
    | TDateTime precision -> if precision > 0 then sprintf "datetime(%d)" precision else "datetime"
    | TTimestamp precision -> if precision > 0 then sprintf "timestamp(%d)" precision else "timestamp"
    | TTime precision -> if precision > 0 then sprintf "time(%d)" precision else "time"
    | TYear -> "year(4)"
    | TJson -> "json"
    | TGeometry GeometryCollection -> "geomcollection"
    | TGeometry kind -> geometryTypeName kind |> _.ToLowerInvariant()
    | TVector dimensions -> sprintf "vector(%d)" dimensions

let private operatorText =
    function
    | And -> "and"
    | Or -> "or"
    | Xor -> "xor"
    | Eq -> "="
    | Neq -> "<>"
    | Lt -> "<"
    | Lte -> "<="
    | Gt -> ">"
    | Gte -> ">="
    | Add -> "+"
    | Sub
    | SignedSub -> "-"
    | Mul -> "*"
    | Div -> "/"
    | IntDiv -> "DIV"
    | NullSafeEq -> "<=>"

let private literal =
    function
    | VNull -> "NULL"
    | VString value -> "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'"
    | value ->
        let text = value |> toText |> Option.defaultValue "NULL"
        let negative =
            match value with
            | VInt number -> number < 0L
            | VDecimal number -> number < 0M
            | VDouble number -> number < 0.0
            | _ -> false

        if negative then "-(" + text[1..] + ")" else text

type ViewRenderOptions =
    { DefaultSchema: string
      IncludeSchema: bool
      RelationColumns: string -> string -> string list option }

type private Source =
    { Qualifier: string
      Reference: string
      Columns: string list }

type private ViewContext =
    { Sources: Source list
      OuterSources: Source list list
      Ctes: (string * string list) list }

let private sameName (left: string) (right: string) = String.Equals(left, right, StringComparison.OrdinalIgnoreCase)
let private identifier (value: string) = "`" + value.Replace("`", "``") + "`"
let private identifiers (values: string list) = values |> List.map identifier |> String.concat ","

let private directionSuffix =
    function
    | Asc -> ""
    | Desc -> " desc"

let private expressionName (expr: Expr) (text: string) =
    match expr with
    | Col name
    | QualifiedCol(_, name) -> name
    | _ -> text

let private trySource (qualifier: string) (sources: Source list) =
    sources |> List.tryFind (fun source -> sameName source.Qualifier qualifier)

let private tryNamedSource qualifier (context: ViewContext) =
    context.Sources :: context.OuterSources
    |> List.tryPick (trySource qualifier)

let private tryColumnSource (name: string) (context: ViewContext) =
    context.Sources :: context.OuterSources
    |> List.tryPick (fun sources ->
        match sources |> List.filter (fun source -> source.Columns |> List.exists (sameName name)) with
        | [ source ] -> Some source
        | _ -> None)

let private tryCte (name: string) (context: ViewContext) =
    context.Ctes
    |> List.tryPick (fun (candidate, columns) -> if sameName candidate name then Some columns else None)

let private nestedContext (context: ViewContext) =
    { context with
        Sources = []
        OuterSources = context.Sources :: context.OuterSources }

let private jsonString (value: string) = "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'"

let rec private renderViewExpression (options: ViewRenderOptions) (context: ViewContext) (expr: Expr) : string =
    let render = renderViewExpression options context

    match expr with
    | Lit value -> literal value
    | Placeholder _ -> "?"
    | UserVariable variable -> variable.Sql
    | SystemVariable(scope, name) ->
        "@@"
        + (scope |> Option.map (fun value -> value.ToLowerInvariant() + ".") |> Option.defaultValue "")
        + name
    | AssignUserVariable(variable, value) -> sprintf "%s := %s" variable.Sql (render value)
    | Col name ->
        tryColumnSource name context
        |> Option.map (fun source -> sprintf "%s.%s" source.Reference (identifier name))
        |> Option.defaultWith (fun () -> identifier name)
    | QualifiedCol(table, column) ->
        let qualifier = tryNamedSource table context |> Option.map _.Reference |> Option.defaultWith (fun () -> identifier table)
        sprintf "%s.%s" qualifier (identifier column)
    | Row values -> sprintf "(%s)" (values |> List.map render |> String.concat ",")
    | BinOp(operator, left, right) -> sprintf "(%s %s %s)" (render left) (operatorText operator) (render right)
    | Not value -> sprintf "(not(%s))" (render value)
    | IsNull value -> sprintf "(%s is null)" (render value)
    | IsNotNull value -> sprintf "(%s is not null)" (render value)
    | IsTrue value -> sprintf "(%s is true)" (render value)
    | IsFalse value -> sprintf "(%s is false)" (render value)
    | Like(value, pattern, caseSensitive, escape) ->
        let binary = if caseSensitive then " binary" else ""
        let escapeText = escape |> Option.map (fun value -> " escape " + jsonString (string value)) |> Option.defaultValue ""
        sprintf "(%s like%s %s%s)" (render value) binary (render pattern) escapeText
    | Regexp(value, pattern) -> sprintf "(%s regexp %s)" (render value) (render pattern)
    | In(value, candidates) -> sprintf "(%s in (%s))" (render value) (candidates |> List.map render |> String.concat ",")
    | InSubquery(value, select) ->
        let sql, _ = renderSelect options (nestedContext context) select
        sprintf "(%s in (%s))" (render value) sql
    | Between(value, lower, upper) -> sprintf "(%s between %s and %s)" (render value) (render lower) (render upper)
    | FuncCall(name, [ Cast(value, TChar length) ]) when name.Equals("WEIGHT_STRING", StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as char(%d))" (render value) length
    | FuncCall(name, [ Cast(value, TBinary length) ]) when name.Equals("WEIGHT_STRING", StringComparison.OrdinalIgnoreCase) ->
        sprintf "weight_string(%s as binary(%d))" (render value) length
    | FuncCall(name, arguments) ->
        sprintf "%s(%s)" (name.ToLowerInvariant()) (arguments |> List.map render |> String.concat ",")
    | MatchAgainst(columns, query, mode) ->
        let columnText (column: MatchColumn) =
            column.Qualifier
            |> Option.map (fun qualifier -> render (QualifiedCol(qualifier, column.Name)))
            |> Option.defaultWith (fun () -> render (Col column.Name))

        let modeText =
            match mode with
            | NaturalLanguage -> ""
            | BooleanMode -> " in boolean mode"
            | QueryExpansion -> " with query expansion"

        sprintf "match (%s) against (%s%s)" (columns |> List.map columnText |> String.concat ",") (render query) modeText
    | WindowOver(window, over) -> sprintf "%s over %s" (renderWindowFunction options context window) (renderOverClause options context over)
    | Distinct value -> "distinct " + render value
    | OrderBy(value, direction) -> render value + directionSuffix direction
    | Cast(value, TBigInt true) -> sprintf "cast(%s as unsigned)" (render value)
    | Cast(value, TBigInt false) -> sprintf "cast(%s as signed)" (render value)
    | Cast(value, target) -> sprintf "cast(%s as %s)" (render value) (columnType target)
    | Collate(value, collation) -> sprintf "(%s collate %s)" (render value) collation
    | Star qualifier -> (qualifier |> Option.map (identifier >> fun value -> value + ".") |> Option.defaultValue "") + "*"
    | Exists select ->
        let sql, _ = renderSelect options (nestedContext context) select
        sprintf "exists(%s)" sql
    | Subquery select ->
        let sql, _ = renderSelect options (nestedContext context) select
        sprintf "(%s)" sql
    | QuantifiedComparison(left, operator, quantifier, select) ->
        let sql, _ = renderSelect options (nestedContext context) select
        let quantifierText = if quantifier = Any then "any" else "all"
        sprintf "(%s %s %s (%s))" (render left) (operatorText operator) quantifierText sql
    | Case(subject, branches, fallback) ->
        let subjectText = subject |> Option.map (render >> sprintf " %s") |> Option.defaultValue ""

        let branchText =
            branches
            |> List.map (fun (condition, result) -> sprintf " when %s then %s" (render condition) (render result))
            |> String.concat ""

        let fallbackText = fallback |> Option.map (render >> sprintf " else %s") |> Option.defaultValue ""
        sprintf "(case%s%s%s end)" subjectText branchText fallbackText

and private renderWindowFunction (options: ViewRenderOptions) (context: ViewContext) (window: WindowFn) : string =
    let render = renderViewExpression options context
    let arguments = List.map render >> String.concat ","

    match window with
    | WinRowNumber -> "row_number()"
    | WinRank false -> "rank()"
    | WinRank true -> "dense_rank()"
    | WinPercentRank -> "percent_rank()"
    | WinCumeDist -> "cume_dist()"
    | WinNTile buckets -> sprintf "ntile(%s)" (render buckets)
    | WinLagLead(lead, value, offset, fallback) ->
        [ Some(render value); Option.map render offset; Option.map render fallback ]
        |> List.choose id
        |> String.concat ","
        |> sprintf "%s(%s)" (if lead then "lead" else "lag")
    | WinFirstValue value -> sprintf "first_value(%s)" (render value)
    | WinLastValue value -> sprintf "last_value(%s)" (render value)
    | WinNthValue(value, index) -> sprintf "nth_value(%s,%s)" (render value) (render index)
    | WinAggregate(name, values) -> sprintf "%s(%s)" (name.ToLowerInvariant()) (arguments values)

and private renderOverClause (options: ViewRenderOptions) (context: ViewContext) (over: OverClause) : string =
    match over with
    | OverName name -> identifier name
    | OverSpec spec -> sprintf "(%s)" (renderWindowSpec options context spec)

and private renderWindowSpec (options: ViewRenderOptions) (context: ViewContext) (spec: WindowSpec) : string =
    let render = renderViewExpression options context

    let frameBound =
        function
        | UnboundedPreceding -> "unbounded preceding"
        | BoundPreceding value -> render value + " preceding"
        | CurrentRow -> "current row"
        | BoundFollowing value -> render value + " following"
        | UnboundedFollowing -> "unbounded following"

    [ spec.Inherit |> Option.map identifier
      if spec.PartitionBy.IsEmpty then None else Some("partition by " + (spec.PartitionBy |> List.map render |> String.concat ","))
      if spec.OrderBy.IsEmpty then None else Some("order by " + (spec.OrderBy |> List.map (fun (value, direction) -> render value + directionSuffix direction) |> String.concat ","))
      spec.Frame
      |> Option.map (fun frame ->
          let unitText = if frame.Unit = FrameRows then "rows" else "range"
          sprintf "%s between %s and %s" unitText (frameBound frame.Start) (frameBound frame.End)) ]
    |> List.choose id
    |> String.concat " "

and private renderJsonColumns (_options: ViewRenderOptions) (_context: ViewContext) (columns: JsonTableColumn list) : string =
    let actionText =
        function
        | JsonNull -> "null"
        | JsonDefault value -> "default " + literal value
        | JsonError -> "error"

    let rec renderColumn =
        function
        | ForOrdinality name -> sprintf "%s for ordinality" (identifier name)
        | PathColumn(name, target, path, onEmpty, onError) ->
            sprintf
                "%s %s path %s %s on empty %s on error"
                (identifier name)
                (columnType target)
                (jsonString path)
                (actionText onEmpty)
                (actionText onError)
        | ExistsColumn(name, target, path) ->
            sprintf "%s %s exists path %s" (identifier name) (columnType target) (jsonString path)
        | NestedColumns(path, nested) ->
            sprintf "nested path %s columns (%s)" (jsonString path) (nested |> List.map renderColumn |> String.concat ",")

    columns |> List.map renderColumn |> String.concat ","

and private jsonColumnNames (columns: JsonTableColumn list) : string list =
    let rec names =
        function
        | ForOrdinality name
        | PathColumn(name, _, _, _, _)
        | ExistsColumn(name, _, _) -> [ name ]
        | NestedColumns(_, nested) -> nested |> List.collect names

    columns |> List.collect names

and private renderFromItem (options: ViewRenderOptions) (context: ViewContext) (item: FromItem) : string * Source =
    match item with
    | FromTable table ->
        let cteColumns =
            if table.Database.IsNone then tryCte table.Table context else None

        let schema = table.Database |> Option.defaultValue options.DefaultSchema
        let columns = cteColumns |> Option.orElseWith (fun () -> options.RelationColumns schema table.Table) |> Option.defaultValue []
        let qualifier = table.Alias |> Option.defaultValue table.Table

        let tableText =
            match cteColumns, options.IncludeSchema, table.Database with
            | Some _, _, _ -> identifier table.Table
            | None, true, _ -> sprintf "%s.%s" (identifier schema) (identifier table.Table)
            | None, false, Some explicitSchema when not (sameName explicitSchema options.DefaultSchema) ->
                sprintf "%s.%s" (identifier explicitSchema) (identifier table.Table)
            | _ -> identifier table.Table

        let aliasText = table.Alias |> Option.map (fun alias -> " " + identifier alias) |> Option.defaultValue ""

        let partitionText =
            if table.Partitions.IsEmpty then "" else sprintf " partition (%s)" (identifiers table.Partitions)

        tableText + aliasText + partitionText,
        { Qualifier = qualifier
          Reference = table.Alias |> Option.map identifier |> Option.defaultValue tableText
          Columns = columns }
    | FromSubquery(query, alias) ->
        let sql, columns = renderSelectOrUnion options (nestedContext context) query
        sprintf "(%s) %s" sql (identifier alias),
        { Qualifier = alias
          Reference = identifier alias
          Columns = columns }
    | FromLateral(query, alias) ->
        let sql, columns = renderSelectOrUnion options (nestedContext context) query
        sprintf "lateral (%s) %s" sql (identifier alias),
        { Qualifier = alias
          Reference = identifier alias
          Columns = columns }
    | FromJsonTable(source, path, columns, alias) ->
        let sql =
            sprintf
                "json_table(%s,%s columns (%s)) %s"
                (renderViewExpression options context source)
                (jsonString path)
                (renderJsonColumns options context columns)
                (identifier alias)

        sql,
        { Qualifier = alias
          Reference = identifier alias
          Columns = jsonColumnNames columns }

and private renderProjection (options: ViewRenderOptions) (context: ViewContext) ((expr, alias): Projection) : string * string =
    let rendered = renderViewExpression options context expr

    match expr, alias with
    | Star _, None -> rendered, "*"
    | _ ->
        let name = alias |> Option.defaultValue (expressionName expr rendered)
        rendered + " AS " + identifier name, name

and private expandProjections (context: ViewContext) (projections: Projection list) : Projection list =
    let expand qualifier =
        let sources =
            match qualifier with
            | None -> context.Sources
            | Some value -> trySource value context.Sources |> Option.toList

        sources
        |> List.collect (fun source -> source.Columns |> List.map (fun column -> QualifiedCol(source.Qualifier, column), None))

    projections
    |> List.collect (fun projection ->
        match projection with
        | Star qualifier, _ ->
            match expand qualifier with
            | [] -> [ projection ]
            | expanded -> expanded
        | _ -> [ projection ])

and private renderOrderKeys (options: ViewRenderOptions) (context: ViewContext) (orderBy: OrderKey list) : string =
    orderBy
    |> List.map (fun (value, direction) -> renderViewExpression options context value + directionSuffix direction)
    |> String.concat ","

and private renderLimit (options: ViewRenderOptions) (context: ViewContext) (limit: Expr option) (offset: Expr option) : string =
    match limit, offset with
    | None, _ -> ""
    | Some count, None -> " limit " + renderViewExpression options context count
    | Some count, Some skipped ->
        sprintf " limit %s,%s" (renderViewExpression options context skipped) (renderViewExpression options context count)

and private renderLocking (locking: LockingRead list) : string =
    let renderOne clause =
        let strength = if clause.Strength = UpdateLock then "for update" else "for share"
        let tables = if clause.Tables.IsEmpty then "" else " of " + identifiers clause.Tables

        let wait =
            match clause.Wait with
            | WaitForLocks -> ""
            | NoWait -> " nowait"
            | SkipLocked -> " skip locked"

        strength + tables + wait

    locking |> List.map renderOne |> String.concat " "

and private renderSelect (options: ViewRenderOptions) (parentContext: ViewContext) (select: SelectStmt) : string * string list =
    let cteContext, cteText =
        select.Ctes
        |> List.fold
            (fun (context, rendered) cte ->
                let provisionalColumns =
                    if cte.CteColumns.IsEmpty then
                        match cte.Body with
                        | PlainSelect body ->
                            body.Projections
                            |> List.map (fun (expr, alias) ->
                                alias
                                |> Option.defaultValue (expressionName expr (renderViewExpression options context expr)))
                        | UnionSelect(first, _, _, _, _) ->
                            first.Projections
                            |> List.map (fun (expr, alias) ->
                                alias
                                |> Option.defaultValue (expressionName expr (renderViewExpression options context expr)))
                    else
                        cte.CteColumns

                let bodyContext: ViewContext =
                    if cte.Recursive then
                        { context with
                            Ctes = context.Ctes @ [ cte.CteName, provisionalColumns ] }
                    else
                        context

                let bodySql, inferredColumns = renderSelectOrUnion options bodyContext cte.Body
                let columns = if cte.CteColumns.IsEmpty then inferredColumns else cte.CteColumns
                let heading = if cte.CteColumns.IsEmpty then "" else sprintf " (%s)" (identifiers cte.CteColumns)
                let sql = sprintf "%s%s as (%s)" (identifier cte.CteName) heading bodySql

                { context with
                    Ctes = context.Ctes @ [ cte.CteName, columns ] },
                rendered @ [ sql ])
            (parentContext, [])

    let context, fromText =
        match select.From with
        | None -> { cteContext with Sources = [] }, ""
        | Some source ->
            let sourceSql, firstSource = renderFromItem options cteContext source

            let sources, joins =
                select.Joins
                |> List.fold
                    (fun (sources, rendered) join ->
                        let joinContext = { cteContext with Sources = sources }
                        let tableSql, source = renderFromItem options joinContext join.Table

                        let joinText =
                            match join.Kind with
                            | InnerJoin -> "join"
                            | LeftJoin -> "left join"
                            | RightJoin -> "right join"
                            | CrossJoin -> "cross join"
                            | NaturalJoin -> "natural join"
                            | NaturalLeftJoin -> "natural left join"
                            | NaturalRightJoin -> "natural right join"

                        let condition =
                            if not join.Using.IsEmpty then
                                sprintf " using (%s)" (identifiers join.Using)
                            else
                                match join.Kind with
                                | CrossJoin
                                | NaturalJoin
                                | NaturalLeftJoin
                                | NaturalRightJoin -> ""
                                | _ -> sprintf " on(%s)" (renderViewExpression options { joinContext with Sources = sources @ [ source ] } join.On)

                        sources @ [ source ], rendered @ [ sprintf "%s %s%s" joinText tableSql condition ])
                    ([ firstSource ], [])

            let joined =
                match joins with
                | [] -> sourceSql
                | _ -> sprintf "(%s %s)" sourceSql (String.concat " " joins)

            { cteContext with Sources = sources }, " from " + joined

    let projections = expandProjections context select.Projections
    let projectionText, outputNames = projections |> List.map (renderProjection options context) |> List.unzip

    let modifiers =
        [ if select.Distinct then Some "distinct" else None
          if select.CalculateFoundRows then Some "sql_calc_found_rows" else None
          if select.StraightJoin then Some "straight_join" else None ]
        |> List.choose id
        |> String.concat " "

    let selectPrefix = if modifiers = "" then "select " else "select " + modifiers + " "

    let intoText =
        match select.IntoFile with
        | Some(Dumpfile fileName) -> " into dumpfile " + jsonString fileName
        | Some(Outfile(fileName, export)) ->
            let charset = export.CharacterSet |> Option.map (fun value -> " character set " + identifier value) |> Option.defaultValue ""
            let enclosed =
                export.EnclosedBy
                |> Option.map (fun value ->
                    " " + (if export.OptionallyEnclosed then "optionally " else "") + "enclosed by " + jsonString value)
                |> Option.defaultValue ""
            let escaped =
                export.Escape
                |> Option.map (fun value -> " escaped by " + jsonString value)
                |> Option.defaultValue " escaped by ''"

            " into outfile "
            + jsonString fileName
            + charset
            + " fields terminated by "
            + jsonString export.FieldTerminator
            + enclosed
            + escaped
            + " lines starting by "
            + jsonString export.LinePrefix
            + " terminated by "
            + jsonString export.LineTerminator
        | None when not select.IntoVariables.IsEmpty ->
            " into " + (select.IntoVariables |> List.map _.Sql |> String.concat ",")
        | None -> ""
    let whereText = select.Where |> Option.map (renderViewExpression options context >> fun value -> " where " + value) |> Option.defaultValue ""

    let groupText =
        if select.GroupBy.IsEmpty then
            ""
        else
            " group by "
            + (select.GroupBy |> List.map (renderViewExpression options context) |> String.concat ",")
            + (if select.Rollup then " with rollup" else "")

    let havingText = select.Having |> Option.map (renderViewExpression options context >> fun value -> " having " + value) |> Option.defaultValue ""

    let windowText =
        if select.Windows.IsEmpty then
            ""
        else
            " window "
            + (select.Windows
               |> List.map (fun (name, spec) -> sprintf "%s as (%s)" (identifier name) (renderWindowSpec options context spec))
               |> String.concat ",")

    let orderText = if select.OrderBy.IsEmpty then "" else " order by " + renderOrderKeys options context select.OrderBy
    let lockingText = if select.Locking.IsEmpty then "" else " " + renderLocking select.Locking

    let ctes =
        match cteText with
        | [] -> ""
        | values -> "with " + (if select.Ctes |> List.exists _.Recursive then "recursive " else "") + String.concat "," values + " "

    ctes
    + selectPrefix
    + String.concat "," projectionText
    + intoText
    + fromText
    + whereText
    + groupText
    + havingText
    + windowText
    + orderText
    + renderLimit options context select.Limit select.Offset
    + lockingText,
    outputNames

and private renderSelectOrUnion (options: ViewRenderOptions) (context: ViewContext) (query: SelectOrUnion) : string * string list =
    match query with
    | PlainSelect select -> renderSelect options context select
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        let firstSql, outputNames = renderSelect options context first

        let restSql =
            rest
            |> List.map (fun (operator, select) ->
                let operatorText =
                    match operator with
                    | OpUnion false -> "union"
                    | OpUnion true -> "union all"
                    | OpIntersect false -> "intersect"
                    | OpIntersect true -> "intersect all"
                    | OpExcept false -> "except"
                    | OpExcept true -> "except all"

                let sql, _ = renderSelect options context select
                operatorText + " " + sql)

        let orderContext =
            { context with
                Sources = [] }

        let orderText = if orderBy.IsEmpty then "" else " order by " + renderOrderKeys options orderContext orderBy

        String.concat " " (firstSql :: restSql)
        + orderText
        + renderLimit options orderContext limit offset,
        outputNames

let private emptyContext =
    { Sources = []
      OuterSources = []
      Ctes = [] }

let expression expr =
    renderViewExpression
        { DefaultSchema = ""
          IncludeSchema = false
          RelationColumns = fun _ _ -> None }
        emptyContext
        expr

let viewDefinition options =
    function
    | Select select -> renderSelect options emptyContext select |> fst |> Some
    | Union(first, rest, orderBy, limit, offset) ->
        renderSelectOrUnion
            options
            emptyContext
            (UnionSelect(first, rest, orderBy, limit, offset))
        |> fst
        |> Some
    | _ -> None
