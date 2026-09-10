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

type private ParameterTreatment =
    | DescribeOnly
    | CoerceNumeric

type private ParameterAnalysis =
    { Definitions: ColumnMetadata option list
      Coercions: ColumnMetadata option list }

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
          "ST_DIFFERENCE"; "ST_DISJOINT"; "ST_DISTANCE"; "ST_DISTANCE_SPHERE"; "ST_ENVELOPE"; "ST_EQUALS"; "ST_GEOMETRYTYPE"; "ST_INTERSECTION"; "ST_INTERSECTS"
          "ST_ISEMPTY"; "ST_ISVALID"; "ST_ISCLOSED"; "ST_SYMDIFFERENCE"; "ST_UNION"
          "ST_LENGTH"; "ST_SRID"; "ST_TOUCHES"; "ST_WITHIN"; "ST_X"; "ST_Y"; "X"; "Y"
          "ST_NUMPOINTS"; "ST_STARTPOINT"; "ST_ENDPOINT"; "ST_POINTN"
          "ST_NUMINTERIORRING"; "ST_NUMINTERIORRINGS"; "ST_EXTERIORRING"; "ST_INTERIORRINGN"
          "ST_NUMGEOMETRIES"; "ST_GEOMETRYN" ]

let private binaryGeometryFunctions =
    set [ "MBRCONTAINS"; "MBRINTERSECTS"; "MBRWITHIN"; "ST_CONTAINS"; "ST_DIFFERENCE"; "ST_DISJOINT"
          "ST_DISTANCE"; "ST_DISTANCE_SPHERE"; "ST_EQUALS"; "ST_INTERSECTION"; "ST_INTERSECTS"; "ST_SYMDIFFERENCE"; "ST_TOUCHES"
          "ST_UNION"; "ST_WITHIN" ]

let private indexedGeometryFunctions = set [ "ST_GEOMETRYN"; "ST_INTERIORRINGN"; "ST_POINTN" ]

let private jsonFirstArgument =
    set [ "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"; "JSON_CONTAINS"; "JSON_CONTAINS_PATH"; "JSON_DEPTH"; "JSON_EXTRACT"; "JSON_INSERT"
          "JSON_KEYS"; "JSON_LENGTH"; "JSON_PRETTY"; "JSON_REMOVE"; "JSON_REPLACE"; "JSON_SEARCH"; "JSON_SET"; "JSON_STORAGE_FREE"
          "JSON_STORAGE_SIZE"; "JSON_TYPE"; "JSON_VALID"; "JSON_VALUE" ]

let private jsonMutationFunctions =
    set [ "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"; "JSON_INSERT"; "JSON_REPLACE"; "JSON_SET" ]

let private integerFunctions =
    set [ "FROM_DAYS"; "MAKEDATE"; "PERIOD_ADD"; "PERIOD_DIFF" ]

let private functionParameterMetadata (registry: Registry) (name: string) index =
    match Functions.lookupScalarParametersNormalized name registry with
    | Some parameters -> parameters |> List.tryItem index |> Option.orElse (Some generic)
    | None when Set.contains name floatingPointFunctions ->
        Some floatingPoint
    | None when name = "ROUND" || name = "TRUNCATE" ->
        Some(if index = 0 then decimalNumber else signedInteger)
    | None when name = "MOD" || name = "BIT_COUNT" || Set.contains name integerFunctions ->
        Some signedInteger
    | None when Set.contains name dateFunctions ->
        Some date
    | None when (name = "WEEK" || name = "YEARWEEK") && index > 0 ->
        Some signedInteger
    | None when name = "WEEK" || name = "YEARWEEK" ->
        Some dateTime
    | None when Set.contains name dateTimeFunctions || name = "TIME" ->
        Some dateTime
    | None when name = "ADDTIME" || name = "SUBTIME" ->
        Some time
    | None when name = "SEC_TO_TIME" ->
        Some decimalNumber
    | None when name = "MAKETIME" ->
        Some(if index < 2 then signedInteger else decimalNumber)
    | None when name = "FROM_UNIXTIME" ->
        Some decimalNumber
    | None when name = "FORMAT" ->
        Some(if index = 0 then decimalNumber else if index = 1 then signedInteger else generic)
    | None when (name = "SUBSTRING" || name = "SUBSTR" || name = "MID") && index > 0 ->
        Some signedInteger
    | None when name = "SHA2" && index = 1 ->
        Some signedInteger
    | None when name = "ST_BUFFER_STRATEGY" ->
        Some(if index = 0 then generic else floatingPoint)
    | None when Set.contains name jsonMutationFunctions && index % 2 = 0 ->
        Some json
    | None when Set.contains name jsonFirstArgument && index = 0 ->
        Some json
    | None when Set.contains name geometryFunctions && index = 0 ->
        Some geometry
    | None when Set.contains name binaryGeometryFunctions && index = 1 ->
        Some geometry
    | None when Set.contains name indexedGeometryFunctions && index = 1 ->
        Some signedInteger
    | None when name = "ST_BUFFER" && index = 1 ->
        Some floatingPoint
    | None when name = "ST_BUFFER" && index > 1 ->
        Some binary
    | None when name = "ST_DISTANCE_SPHERE" && index = 2 ->
        Some floatingPoint
    | None when name = "ST_SRID" && index = 1 ->
        Some signedInteger
    | None when Functions.isWkbGeometryConstructor name && index = 0 ->
        Some binary
    | None when Functions.isGeometryConstructor name && index = 1 ->
        Some signedInteger
    | None -> None

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
    | VTimestamp _ -> ColumnWire.parameterMetadataOfType(TTimestamp 6)
    | VTime _ -> ColumnWire.parameterMetadataOfType(TTime 6)
    | VBit(width, _) -> ColumnWire.parameterMetadataOfType(TBit width)
    | VJson _ -> ColumnWire.parameterMetadataOfType TJson
    | VGeometry geometry -> ColumnWire.parameterMetadataOfType(TGeometry(geometryKind geometry.Shape))
    | VNull -> generic

/// Infers each parameter's expression context without evaluating the statement.
/// `None` distinguishes a genuinely unconstrained marker from a marker whose
/// context is MySQL's generic string type.
let private inferParameters
    (store: Store)
    (registry: Registry)
    (schema: string)
    (statement: Statement)
    (parameterCount: int)
    : ParameterAnalysis =
    let parameters = Array.create parameterCount None
    let coercions = Array.create parameterCount None

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

    let setParameter index treatment metadata =
        if index >= 0 && index < parameters.Length then
            parameters.[index] <- Some metadata

            if treatment = CoerceNumeric then
                coercions.[index] <- Some metadata

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

    let rec inferExpression scope expected treatment expression =
        let inferUnknown child = inferExpression scope None DescribeOnly child
        let inferExpected metadata child = inferExpression scope metadata DescribeOnly child
        let inferConverted metadata child = inferExpression scope metadata CoerceNumeric child
        let inferred child = metadataOfExpression scope child

        match expression with
        | Placeholder index -> expected |> Option.iter (setParameter index treatment)
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
        | Cast(inner, TTime _) -> inferConverted (Some(ColumnWire.parameterMetadataOfType(TDateTime 6))) inner
        | Cast(inner, ty) -> inferConverted (Some(ColumnWire.parameterMetadataOfType ty)) inner
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
            let name = name.ToUpperInvariant()

            values
            |> List.iteri (fun index value ->
                inferConverted (functionParameterMetadata registry name index) value)
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
                inferExpression scope None DescribeOnly source
                []

        let mutable scope = outerScope

        from
        |> Option.iter (fun item -> scope <- scope @ columnsOfItem scope item)

        for join in joins do
            scope <- scope @ columnsOfItem scope join.Table
            inferExpression scope None DescribeOnly join.On

        scope

    and inferSelect outerScope select =
        let scope = inferScope outerScope select.Ctes select.From select.Joins

        let infer = inferExpression scope None DescribeOnly
        select.Projections |> List.iter (fst >> infer)
        select.Where |> Option.iter infer
        select.GroupBy |> List.iter infer
        select.Having |> Option.iter infer
        select.OrderBy |> List.iter (fst >> infer)
        select.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)
        select.Offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)

    and inferBody scope =
        function
        | PlainSelect select -> inferSelect scope select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            inferSelect scope first
            rest |> List.iter (snd >> inferSelect scope)
            orderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
            limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)
            offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)

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
                inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) DescribeOnly expression))

    let inferAssignments table assignments =
        let columns = tableColumns None table

        assignments
        |> List.iter (fun (name, expression) ->
            let expected =
                columns
                |> List.tryFind (fun column -> sameName column.Name name)
                |> Option.map (fun column -> ColumnWire.parameterMetadataOfType column.Type)

            inferExpression (withQualifier table columns) expected DescribeOnly expression)

    match statement with
    | Select select -> inferSelect [] select
    | Union(first, rest, orderBy, limit, offset) -> inferBody [] (UnionSelect(first, rest, orderBy, limit, offset))
    | Do expressions -> expressions |> List.iter (inferExpression [] None DescribeOnly)
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
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) DescribeOnly expression)

        inferAssignments table onDuplicate
    | ReplaceSelect(table, columns, select) ->
        inferSelect [] select
        let targets = targetColumns table columns

        Seq.zip select.Projections targets
        |> Seq.iter (fun ((expression, _), column) ->
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) DescribeOnly expression)
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

            inferExpression scope expected DescribeOnly assignment.Value)

        update.Where |> Option.iter (inferExpression scope None DescribeOnly)
        update.OrderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
        update.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)
    | Delete delete ->
        let scope = inferScope [] delete.Ctes (Some(FromTable delete.From)) delete.Joins
        delete.Where |> Option.iter (inferExpression scope None DescribeOnly)
        delete.OrderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
        delete.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) DescribeOnly)
    | _ -> ()

    { Definitions = List.ofArray parameters
      Coercions = List.ofArray coercions }

let private parameterExpectations store registry schema statement parameterCount =
    (inferParameters store registry schema statement parameterCount).Definitions

let private parameterCoercions store registry schema statement parameterCount =
    (inferParameters store registry schema statement parameterCount).Coercions

/// Infers the parameter descriptors advertised by COM_STMT_PREPARE.
let parameterDefinitions store registry schema statement parameterCount : ColumnMetadata list =
    parameterExpectations store registry schema statement parameterCount
    |> List.map (Option.defaultValue generic)

let private numericParameterType (metadata: ColumnMetadata) =
    let unsigned = metadata.Flags &&& UnsignedFlag <> 0us

    match metadata.TypeId with
    | typeId when typeId = TypeTiny -> Some(TTinyInt unsigned)
    | typeId when typeId = TypeShort -> Some(TSmallInt unsigned)
    | typeId when typeId = TypeLong -> Some(TInt unsigned)
    | typeId when typeId = TypeLongLong -> Some(TBigInt unsigned)
    | typeId when typeId = TypeFloat -> Some(TFloat unsigned)
    | typeId when typeId = TypeDouble -> Some(TDouble unsigned)
    | typeId when typeId = TypeNewDecimal -> Some(TDecimal(65, int metadata.Decimals, unsigned))
    | typeId when typeId = TypeYear -> Some TYear
    | typeId when typeId = TypeBit -> Some(TBit(int metadata.ColumnLength))
    | _ -> None

let private parameterColumn columnType : ColumnDef =
    { Name = "parameter"
      Type = columnType
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

/// Converts bound values in typed numeric function and cast contexts.
/// The protocol's supplied type only describes the bytes on the wire;
/// non-numeric contexts already consume those values through their own SQL
/// coercion rules and must retain the dynamic type of an unconstrained marker.
let coerceParameters store registry schema statement values =
    let mode =
        { Storage.temporalCoercionMode store with
            Strict = false }

    let coerce expectation value =
        match expectation |> Option.bind numericParameterType with
        | None -> value
        | Some columnType ->
            match
                Diagnostics.suppress (fun () ->
                    Storage.coerceValueWithMode mode (parameterColumn columnType) value)
            with
            | Ok coerced -> coerced
            | Error _ -> value

    let expectations = parameterCoercions store registry schema statement (List.length values)
    List.map2 coerce expectations values
