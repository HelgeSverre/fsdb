module Fsdb.PreparedMetadata

open System
open Fsdb.Ast
open Fsdb.Engine
open Fsdb.Functions
open Fsdb.Storage
open Fsdb.Value

type private BoundColumn =
    { Qualifier: string
      Column: ColumnDef }

let private sameName (left: string) (right: string) =
    left.Equals(right, StringComparison.OrdinalIgnoreCase)

let private generic = ColumnWire.parameterMetadataOfType(TVarchar 16383)
let private signedInteger = ColumnWire.parameterMetadataOfType(TBigInt false)
let private decimalNumber = ColumnWire.parameterMetadataOfType(TDecimal(65, 30, false))
let private floatingPoint = ColumnWire.parameterMetadataOfType (TDouble false)
let private date = ColumnWire.parameterMetadataOfType TDate
let private dateTime = ColumnWire.parameterMetadataOfType(TDateTime 6)
let private time = ColumnWire.parameterMetadataOfType(TTime 6)
let private json = ColumnWire.parameterMetadataOfType TJson
let private geometry = ColumnWire.parameterMetadataOfType(TGeometry Geometry)
let private binary = ColumnWire.parameterMetadataOfType TLongBlob

let private floatingPointFunctions =
    set [ "ABS"; "ACOS"; "ASIN"; "ATAN"; "ATAN2"; "CEIL"; "CEILING"; "COS"; "COT"; "DEGREES"; "EXP"; "FLOOR"
          "LN"; "LOG"; "LOG10"; "LOG2"; "POW"; "POWER"; "RADIANS"; "SIGN"; "SIN"; "SQRT"; "TAN" ]

let private dateFunctions = set [ "DATE"; "DATEDIFF"; "LAST_DAY"; "TO_DAYS" ]

let private dateTimeFunctions =
    set [ "DAY"; "DAYNAME"; "DAYOFMONTH"; "DAYOFWEEK"; "DAYOFYEAR"; "HOUR"; "MICROSECOND"; "MINUTE"; "MONTH"; "MONTHNAME"
          "QUARTER"; "SECOND"; "UNIX_TIMESTAMP"; "WEEKDAY"; "WEEKOFYEAR"; "YEAR" ]

let private geometryFunctions =
    set [ "ASTEXT"; "ASBINARY"; "DIMENSION"; "GEOMETRYTYPE"; "ISEMPTY"; "MBRCONTAINS"; "MBRINTERSECTS"; "MBRWITHIN"
          "ST_ASBINARY"; "ST_ASTEXT"; "ST_ASWKB"; "ST_ASWKT"; "ST_BUFFER"; "ST_CONTAINS"; "ST_CONVEXHULL"; "ST_DIMENSION"
          "ST_DISJOINT"; "ST_DISTANCE"; "ST_ENVELOPE"; "ST_EQUALS"; "ST_GEOMETRYTYPE"; "ST_INTERSECTS"; "ST_ISEMPTY"; "ST_ISVALID"
          "ST_SRID"; "ST_TOUCHES"; "ST_WITHIN"; "ST_X"; "ST_Y"; "X"; "Y" ]

let private wkbConstructors =
    set [ "GEOMFROMWKB"; "ST_GEOMETRYFROMWKB"; "ST_GEOMFROMWKB"; "ST_POINTFROMWKB" ]

let private wktConstructors =
    set [ "GEOMETRYFROMTEXT"; "GEOMFROMTEXT"; "ST_GEOMETRYFROMTEXT"; "ST_GEOMFROMTEXT"; "ST_LINESTRINGFROMTEXT"
          "ST_POINTFROMTEXT"; "ST_POLYGONFROMTEXT" ]

let private jsonFirstArgument =
    set [ "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"; "JSON_CONTAINS"; "JSON_CONTAINS_PATH"; "JSON_DEPTH"; "JSON_EXTRACT"; "JSON_INSERT"
          "JSON_KEYS"; "JSON_LENGTH"; "JSON_PRETTY"; "JSON_REMOVE"; "JSON_REPLACE"; "JSON_SEARCH"; "JSON_SET"; "JSON_STORAGE_FREE"
          "JSON_STORAGE_SIZE"; "JSON_TYPE"; "JSON_VALID"; "JSON_VALUE" ]

let private jsonMutationFunctions =
    set [ "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"; "JSON_INSERT"; "JSON_REPLACE"; "JSON_SET" ]

let private integerFunctions =
    set [ "FROM_DAYS"; "MAKEDATE"; "PERIOD_ADD"; "PERIOD_DIFF" ]

let private functionParameterMetadata (name: string) index =
    let name = name.ToUpperInvariant()

    if Set.contains name floatingPointFunctions then
        Some floatingPoint
    elif name = "ROUND" || name = "TRUNCATE" then
        Some(if index = 0 then decimalNumber else signedInteger)
    elif name = "MOD" || name = "BIT_COUNT" || Set.contains name integerFunctions then
        Some signedInteger
    elif Set.contains name dateFunctions then
        Some date
    elif (name = "WEEK" || name = "YEARWEEK") && index > 0 then
        Some signedInteger
    elif name = "WEEK" || name = "YEARWEEK" then
        Some dateTime
    elif Set.contains name dateTimeFunctions || name = "TIME" then
        Some dateTime
    elif name = "ADDTIME" || name = "SUBTIME" then
        Some time
    elif name = "SEC_TO_TIME" then
        Some decimalNumber
    elif name = "MAKETIME" then
        Some(if index < 2 then signedInteger else decimalNumber)
    elif name = "FROM_UNIXTIME" then
        Some decimalNumber
    elif name = "FORMAT" then
        Some(if index = 0 then decimalNumber else if index = 1 then signedInteger else generic)
    elif (name = "SUBSTRING" || name = "SUBSTR" || name = "MID") && index > 0 then
        Some signedInteger
    elif name = "SHA2" && index = 1 then
        Some signedInteger
    elif Set.contains name jsonMutationFunctions && index % 2 = 0 then
        Some json
    elif Set.contains name jsonFirstArgument && index = 0 then
        Some json
    elif Set.contains name geometryFunctions && index = 0 then
        Some geometry
    elif Set.contains name geometryFunctions && index = 1 && name <> "ST_BUFFER" && name <> "ST_SRID" then
        Some geometry
    elif name = "ST_BUFFER" && index = 1 then
        Some floatingPoint
    elif name = "ST_SRID" && index = 1 then
        Some signedInteger
    elif Set.contains name wkbConstructors && index = 0 then
        Some binary
    elif (Set.contains name wkbConstructors || Set.contains name wktConstructors) && index = 1 then
        Some signedInteger
    else
        None

let private metadataOfValue =
    function
    | VInt _ -> ColumnWire.parameterMetadataOfType(TBigInt false)
    | VUInt _ -> ColumnWire.parameterMetadataOfType(TBigInt true)
    | VDouble _ -> ColumnWire.parameterMetadataOfType (TDouble false)
    | VDecimal _ -> ColumnWire.parameterMetadataOfType(TDecimal(65, 30, false))
    | VString _ -> generic
    | VBytes _ -> ColumnWire.parameterMetadataOfType TLongBlob
    | VDate _
    | VZeroDate _ -> ColumnWire.parameterMetadataOfType TDate
    | VDateTime _
    | VZeroDateTime _ -> ColumnWire.parameterMetadataOfType(TDateTime 6)
    | VTime _ -> ColumnWire.parameterMetadataOfType(TTime 6)
    | VBit(width, _) -> ColumnWire.parameterMetadataOfType(TBit width)
    | VJson _ -> ColumnWire.parameterMetadataOfType TJson
    | VGeometry geometry -> ColumnWire.parameterMetadataOfType(TGeometry(geometryKind geometry.Shape))
    | VNull -> generic

/// Infers COM_STMT_PREPARE parameter descriptors without evaluating the statement.
let parameterDefinitions
    (store: Store)
    (registry: Registry)
    (schema: string)
    (statement: Statement)
    (parameterCount: int)
    : ColumnMetadata list =
    let parameters = Array.create parameterCount generic

    let tryColumn scope expression =
        let matches =
            match expression with
            | Col name -> scope |> List.filter (fun bound -> sameName bound.Column.Name name)
            | QualifiedCol(qualifier, name) ->
                scope
                |> List.filter (fun bound -> sameName bound.Qualifier qualifier && sameName bound.Column.Name name)
            | _ -> []

        match matches with
        | [ bound ] -> Some bound.Column
        | _ -> None

    let metadataOfExpression scope =
        let rec loop =
            function
            | Placeholder _ -> None
            | Lit value -> Some(metadataOfValue value)
            | (Col _ | QualifiedCol _) as expression ->
                tryColumn scope expression
                |> Option.map (fun column -> ColumnWire.parameterMetadataOfType column.Type)
            | Cast(_, TTime _) -> Some(ColumnWire.parameterMetadataOfType(TDateTime 6))
            | Cast(_, ty) -> Some(ColumnWire.parameterMetadataOfType ty)
            | Collate(inner, _)
            | Distinct inner
            | OrderBy(inner, _) -> loop inner
            | _ -> None

        loop

    let setParameter index metadata =
        if index >= 0 && index < parameters.Length then
            parameters.[index] <- metadata

    let statementOfBody =
        function
        | PlainSelect select -> Select select
        | UnionSelect(first, rest, orderBy, limit, offset) -> Union(first, rest, orderBy, limit, offset)

    let describeBody body =
        Executor.statementColumns store registry schema (statementOfBody body)
        |> Option.defaultValue []

    let withQualifier qualifier columns =
        columns |> List.map (fun column -> { Qualifier = qualifier; Column = column })

    let tableColumns database table =
        let database = database |> Option.defaultValue schema

        match Storage.scan store database table with
        | Ok(columns, _) -> columns
        | Error _ -> Executor.viewColumns store registry database table |> Option.defaultValue []

    let rec inferExpression scope expected expression =
        let inferUnknown child = inferExpression scope None child
        let inferExpected metadata child = inferExpression scope metadata child
        let inferred child = metadataOfExpression scope child

        match expression with
        | Placeholder index -> expected |> Option.iter (setParameter index)
        | BinOp(operator, left, right) ->
            let leftMetadata = inferred left
            let rightMetadata = inferred right

            let fallback =
                match operator, expected with
                | (Add | Sub | SignedSub | Mul | Div | IntDiv), None when leftMetadata.IsNone && rightMetadata.IsNone ->
                    Some(ColumnWire.parameterMetadataOfType (TDouble false))
                | _ -> expected

            inferExpected (rightMetadata |> Option.orElse fallback) left
            inferExpected (leftMetadata |> Option.orElse fallback) right
        | Between(value, lower, upper) ->
            let valueMetadata = inferred value |> Option.orElse expected
            inferExpected (inferred lower |> Option.orElse (inferred upper) |> Option.orElse expected) value
            inferExpected valueMetadata lower
            inferExpected valueMetadata upper
        | In(value, candidates) ->
            let candidateMetadata = candidates |> List.tryPick inferred
            let valueMetadata = inferred value |> Option.orElse candidateMetadata |> Option.orElse expected
            inferExpected (candidateMetadata |> Option.orElse expected) value
            candidates |> List.iter (inferExpected valueMetadata)
        | Like(value, pattern, _, _)
        | Regexp(value, pattern) ->
            inferExpected (Some generic) value
            inferExpected (Some generic) pattern
        | Cast(inner, TTime _) -> inferExpected (Some(ColumnWire.parameterMetadataOfType(TDateTime 6))) inner
        | Cast(inner, ty) -> inferExpected (Some(ColumnWire.parameterMetadataOfType ty)) inner
        | FuncCall(name, values) when
            name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
            || name.Equals("IFNULL", StringComparison.OrdinalIgnoreCase)
            ->
            let output = values |> List.tryPick inferred |> Option.orElse expected |> Option.orElse (Some generic)
            values |> List.iter (inferExpected output)
        | FuncCall(name, [ condition; whenTrue; whenFalse ]) when name.Equals("IF", StringComparison.OrdinalIgnoreCase) ->
            inferUnknown condition
            let output = inferred whenTrue |> Option.orElse (inferred whenFalse) |> Option.orElse expected |> Option.orElse (Some generic)
            inferExpected output whenTrue
            inferExpected output whenFalse
        | FuncCall(name, [ first; second ]) when name.Equals("NULLIF", StringComparison.OrdinalIgnoreCase) ->
            inferExpected (inferred second |> Option.orElse expected |> Option.orElse (Some generic)) first
            inferExpected (inferred first |> Option.orElse expected |> Option.orElse (Some generic)) second
        | FuncCall(name, values) ->
            values
            |> List.iteri (fun index value ->
                inferExpected (functionParameterMetadata name index) value)
        | Case(subject, branches, fallback) ->
            subject |> Option.iter inferUnknown

            let output =
                (branches |> List.map snd) @ Option.toList fallback
                |> List.tryPick inferred
                |> Option.orElse expected
                |> Option.orElse (Some generic)

            branches
            |> List.iter (fun (condition, result) ->
                inferUnknown condition
                inferExpected output result)

            fallback |> Option.iter (inferExpected output)
        | _ -> Fsdb.Sql.Expression.children expression |> List.iter inferUnknown

        Fsdb.Sql.Expression.subqueries expression |> List.iter (inferSelect scope)

    and inferScope
        (outerScope: BoundColumn list)
        (ctes: CommonTableExpr list)
        (from: FromItem option)
        (joins: Join list)
        =
        let cteColumns =
            ctes
            |> List.map (fun cte ->
                inferBody outerScope cte.Body
                let columns = describeBody cte.Body

                let columns =
                    if cte.CteColumns.IsEmpty then
                        columns
                    else
                        Seq.zip columns cte.CteColumns
                        |> Seq.map (fun (column, name) -> { column with Name = name })
                        |> List.ofSeq

                cte.CteName, columns)

        let columnsOfItem scope =
            function
            | FromTable table ->
                let columns =
                    cteColumns
                    |> List.tryFind (fun (name, _) -> table.Database.IsNone && sameName name table.Table)
                    |> Option.map snd
                    |> Option.defaultWith (fun () -> tableColumns table.Database table.Table)

                withQualifier (table.Alias |> Option.defaultValue table.Table) columns
            | FromSubquery(body, alias) ->
                inferBody outerScope body
                describeBody body |> withQualifier alias
            | FromLateral(body, alias) ->
                inferBody scope body
                describeBody body |> withQualifier alias
            | FromJsonTable(source, _, _, _) ->
                inferExpression scope None source
                []

        let mutable scope = outerScope

        from
        |> Option.iter (fun item -> scope <- scope @ columnsOfItem scope item)

        for join in joins do
            scope <- scope @ columnsOfItem scope join.Table
            inferExpression scope None join.On

        scope

    and inferSelect outerScope select =
        let scope = inferScope outerScope select.Ctes select.From select.Joins

        let infer = inferExpression scope None
        select.Projections |> List.iter (fst >> infer)
        select.Where |> Option.iter infer
        select.GroupBy |> List.iter infer
        select.Having |> Option.iter infer
        select.OrderBy |> List.iter (fst >> infer)
        select.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))
        select.Offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))

    and inferBody scope =
        function
        | PlainSelect select -> inferSelect scope select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            inferSelect scope first
            rest |> List.iter (snd >> inferSelect scope)
            orderBy |> List.iter (fst >> inferExpression scope None)
            limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))
            offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))

    let targetColumns (table: string) (names: string list) =
        let columns = tableColumns None table

        if names.IsEmpty then
            columns
        else
            names
            |> List.choose (fun name -> columns |> List.tryFind (fun column -> sameName column.Name name))

    let inferRows table columns rows =
        let columns = targetColumns table columns

        rows
        |> List.iter (fun row ->
            Seq.zip row columns
            |> Seq.iter (fun (expression, column) ->
                inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) expression))

    let inferAssignments table assignments =
        let columns = tableColumns None table

        assignments
        |> List.iter (fun (name, expression) ->
            let expected =
                columns
                |> List.tryFind (fun column -> sameName column.Name name)
                |> Option.map (fun column -> ColumnWire.parameterMetadataOfType column.Type)

            inferExpression (withQualifier table columns) expected expression)

    match statement with
    | Select select -> inferSelect [] select
    | Union(first, rest, orderBy, limit, offset) -> inferBody [] (UnionSelect(first, rest, orderBy, limit, offset))
    | Do expressions -> expressions |> List.iter (inferExpression [] None)
    | Insert(table, columns, rows, onDuplicate, _) ->
        inferRows table columns rows
        inferAssignments table onDuplicate
    | Replace(table, columns, rows) -> inferRows table columns rows
    | ReplaceSet(table, assignments) -> inferAssignments table assignments
    | InsertSelect(table, columns, select, onDuplicate, _) ->
        inferSelect [] select
        let targets = targetColumns table columns

        Seq.zip select.Projections targets
        |> Seq.iter (fun ((expression, _), column) ->
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) expression)

        inferAssignments table onDuplicate
    | ReplaceSelect(table, columns, select) ->
        inferSelect [] select
        let targets = targetColumns table columns

        Seq.zip select.Projections targets
        |> Seq.iter (fun ((expression, _), column) ->
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) expression)
    | Update update ->
        let scope = inferScope [] update.Ctes (Some(FromTable update.From)) update.Joins

        update.Assignments
        |> List.iter (fun assignment ->
            let target =
                match assignment.Table with
                | Some qualifier -> QualifiedCol(qualifier, assignment.Column)
                | None -> Col assignment.Column

            let expected =
                target |> tryColumn scope
                |> Option.map (fun column -> ColumnWire.parameterMetadataOfType column.Type)

            inferExpression scope expected assignment.Value)

        update.Where |> Option.iter (inferExpression scope None)
        update.OrderBy |> List.iter (fst >> inferExpression scope None)
        update.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))
    | Delete delete ->
        let scope = inferScope [] delete.Ctes (Some(FromTable delete.From)) delete.Joins
        delete.Where |> Option.iter (inferExpression scope None)
        delete.OrderBy |> List.iter (fst >> inferExpression scope None)
        delete.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))))
    | _ -> ()

    List.ofArray parameters
