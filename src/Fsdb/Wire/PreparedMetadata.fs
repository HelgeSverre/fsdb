module Fsdb.PreparedMetadata

open System
open System.Globalization
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
    | InheritType
    | ValidateUnsignedInteger
    | AssignToColumn

type private ParameterBinding =
    | CoerceNumericAs of ColumnMetadata
    | InheritedType of ColumnMetadata
    | UnsignedIntegerOnly
    | ColumnAssignment

/// SQL-derived types retained by one prepared statement, independently of wire encodings.
type ParameterTypes =
    private
        { Context: ColumnMetadata list
          Derived: ColumnMetadata list }

type internal BindingSource =
    | ProtocolValues
    | UserVariables

type private ParameterAnalysis =
    { Definitions: ColumnMetadata option list
      Bindings: ParameterBinding option list
      ProjectedParameters: Set<int> }

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
    | None when name = "BIT_COUNT" || Set.contains name integerFunctions ->
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
    let bindings = Array.create parameterCount None
    let mutable projectedParameters = Set.empty

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
            | UserVariable variable -> variable.PreparedType |> Option.map PreparedVariables.metadata
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

            bindings.[index] <-
                match treatment with
                | DescribeOnly -> bindings.[index]
                | CoerceNumeric -> Some(CoerceNumericAs metadata)
                | InheritType -> Some(InheritedType metadata)
                | ValidateUnsignedInteger -> Some UnsignedIntegerOnly
                | AssignToColumn -> Some ColumnAssignment

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
        | Cast(inner, TTime _) -> inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TDateTime 6))) InheritType inner
        | Cast(inner, ty) -> inferExpression scope (Some(ColumnWire.parameterMetadataOfType ty)) InheritType inner
        | FuncCall(name, values) when
            (name.Equals("COALESCE", StringComparison.OrdinalIgnoreCase)
             || name.Equals("IFNULL", StringComparison.OrdinalIgnoreCase)
             || name.Equals("GREATEST", StringComparison.OrdinalIgnoreCase)
             || name.Equals("LEAST", StringComparison.OrdinalIgnoreCase))
            && Functions.isUnmodifiedBuiltinScalar name registry
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
        | FuncCall(name, [ left; right ]) when
            name.Equals("MOD", StringComparison.OrdinalIgnoreCase)
            && Functions.isUnmodifiedBuiltinScalar name registry ->
            inferExpected (inferred right |> Option.orElse (Some floatingPoint)) left
            inferExpected (inferred left |> Option.orElse (Some floatingPoint)) right
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
        select.Projections
        |> List.iter (fun (expression, _) ->
            projectedParameters <-
                Fsdb.Sql.Expression.fold
                    (fun parameters expression ->
                        match expression with
                        | Placeholder index -> Fsdb.Sql.Expression.Descend(Set.add index parameters)
                        | _ -> Fsdb.Sql.Expression.Descend parameters)
                    projectedParameters
                    expression
            infer expression)
        select.Where |> Option.iter infer
        select.GroupBy |> List.iter infer
        select.Having |> Option.iter infer
        select.OrderBy |> List.iter (fst >> infer)
        select.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)
        select.Offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)

    and inferBody scope =
        function
        | PlainSelect select -> inferSelect scope select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            inferSelect scope first
            rest |> List.iter (snd >> inferSelect scope)
            orderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
            limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)
            offset |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)

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
                inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) AssignToColumn expression))

    let inferAssignments table assignments =
        let columns = tableColumns None table

        assignments
        |> List.iter (fun (name, expression) ->
            let expected =
                columns
                |> List.tryFind (fun column -> sameName column.Name name)
                |> Option.map (fun column -> ColumnWire.parameterMetadataOfType column.Type)

            inferExpression (withQualifier table columns) expected AssignToColumn expression)

    match statement with
    | Select select -> inferSelect [] select
    | Union(first, rest, orderBy, limit, offset) -> inferBody [] (UnionSelect(first, rest, orderBy, limit, offset))
    | Do expressions -> expressions |> List.iter (inferExpression [] None DescribeOnly)
    | SetVariables assignments -> assignments |> List.choose SetClause.expression |> List.iter (inferExpression [] None DescribeOnly)
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
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) AssignToColumn expression)

        inferAssignments table onDuplicate
    | ReplaceSelect(table, columns, select) ->
        inferSelect [] select
        let targets = targetColumns table columns

        Seq.zip select.Projections targets
        |> Seq.iter (fun ((expression, _), column) ->
            inferExpression [] (Some(ColumnWire.parameterMetadataOfType column.Type)) AssignToColumn expression)
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

            inferExpression scope expected AssignToColumn assignment.Value)

        update.Where |> Option.iter (inferExpression scope None DescribeOnly)
        update.OrderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
        update.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)
    | Delete delete ->
        let scope = inferScope [] delete.Ctes (Some(FromTable delete.From)) delete.Joins
        delete.Where |> Option.iter (inferExpression scope None DescribeOnly)
        delete.OrderBy |> List.iter (fst >> inferExpression scope None DescribeOnly)
        delete.Limit |> Option.iter (inferExpression scope (Some(ColumnWire.parameterMetadataOfType(TBigInt true))) ValidateUnsignedInteger)
    | _ -> ()

    { Definitions = List.ofArray parameters
      Bindings = List.ofArray bindings
      ProjectedParameters = projectedParameters }

let private parameterExpectations store registry schema statement parameterCount =
    (inferParameters store registry schema statement parameterCount).Definitions

/// Infers the parameter descriptors advertised by COM_STMT_PREPARE.
let parameterDefinitions store registry schema statement parameterCount : ColumnMetadata list =
    parameterExpectations store registry schema statement parameterCount
    |> List.map (Option.defaultValue generic)

let initialTypes store registry schema statement parameterCount =
    let types = parameterDefinitions store registry schema statement parameterCount
    { Context = types; Derived = types }

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

type private ParameterFamily =
    | Integral of unsigned: bool
    | ExactNumeric
    | ApproximateNumeric
    | CalendarDate
    | ClockTime
    | CalendarTime
    | Other

let private family (metadata: ColumnMetadata) =
    match numericParameterType metadata with
    | Some(TDouble _ | TFloat _) -> ApproximateNumeric
    | Some(TDecimal _) -> ExactNumeric
    | Some _ -> Integral(metadata.Flags &&& UnsignedFlag <> 0us)
    | None when metadata.TypeId = TypeDate -> CalendarDate
    | None when metadata.TypeId = TypeTime -> ClockTime
    | None when metadata.TypeId = TypeDateTime || metadata.TypeId = TypeTimestamp -> CalendarTime
    | None -> Other

let private inherited = function
    | Some(InheritedType _ | UnsignedIntegerOnly) -> true
    | _ -> false

let private accepts (expected: ColumnMetadata) value =
    match value with
    | VNull | VString _ | VBytes _ -> true
    | _ ->
        match family expected, family (metadataOfValue value) with
        | Integral expectedSign, Integral actualSign -> expectedSign = actualSign || expected.TypeId = TypeYear
        | ExactNumeric, (Integral _ | ExactNumeric)
        | ApproximateNumeric, (Integral _ | ExactNumeric | ApproximateNumeric)
        | (CalendarDate | ClockTime | CalendarTime), (Integral _ | ExactNumeric | ApproximateNumeric)
        | CalendarDate, CalendarDate
        | ClockTime, (ClockTime | CalendarTime)
        | CalendarTime, (CalendarDate | ClockTime | CalendarTime)
        | Other, Other -> true
        | _ -> false

let private convertedNumericString expected (text: string) =
    let integer =
        match family expected with
        | Integral false ->
            match Int64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value when value >= 0L -> Some(VInt value)
            | _ -> None
        | Integral true ->
            match UInt64.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, value -> Some(VUInt value)
            | _ -> None
        | _ -> None

    let exact () =
        match family expected with
        | Integral _ | ExactNumeric ->
            match Decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
            | true, value -> Some(VDecimal value)
            | _ -> None
        | _ -> None

    let approximate () =
        match Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture) with
        | true, value when Double.IsFinite value -> Some(VDouble value)
        | _ -> None

    integer |> Option.orElseWith exact |> Option.orElseWith approximate

let private parameterType metadata =
    numericParameterType metadata
    |> Option.defaultWith (fun () ->
        match family metadata with
        | CalendarDate -> TDate
        | ClockTime -> TTime 6
        | CalendarTime -> TDateTime 6
        | _ when metadata.TypeId = TypeGeometry -> TGeometry Geometry
        | _ when metadata.TypeId = TypeJson -> TJson
        | _ when metadata.TypeId = TypeBlob -> TLongBlob
        | _ -> TVarchar 16383)

/// Converts supplied values before deciding whether the entire statement must be reprepared.
let internal bindParameters source store registry schema schemaChanged refreshVariables statement retained (values: Value list) =
    let analysis = inferParameters store registry schema statement values.Length
    let original = analysis.Definitions |> List.map (Option.defaultValue generic)
    let retained = retained |> Option.defaultValue { Context = original; Derived = original }
    let expected = retained.Derived
    let mode = { Storage.temporalCoercionMode store with Strict = false }

    let coerce columnType value =
        Diagnostics.suppress (fun () -> Storage.coerceValueWithMode mode (parameterColumn columnType) value)
        |> Result.defaultValue value

    let convert expected value =
        match family expected, value with
        | (Integral _ | ExactNumeric | ApproximateNumeric), VString text ->
            convertedNumericString expected text |> Option.defaultValue value
        | (CalendarDate | ClockTime | CalendarTime), (VString _ | VInt _ | VUInt _ | VDecimal _ | VDouble _) ->
            let strict = { mode with Strict = true }
            let value =
                match family expected, value with
                | (CalendarDate | CalendarTime), (VInt _ | VUInt _ | VDecimal _ | VDouble _) ->
                    Functions.tryDateTimeValue value
                    |> Option.map (fun dateTime ->
                        if dateTime.TimeOfDay = TimeSpan.Zero then VDate(DateOnly.FromDateTime dateTime)
                        else VDateTime dateTime)
                    |> Option.defaultValue value
                | _ -> value
            let target =
                match value with
                | VDate _ -> TDate
                | VDateTime _ -> TDateTime 6
                | VString text when Fsdb.Temporal.tryParseDateTimeComponents text |> Option.isSome -> TDateTime 6
                | VString text when Fsdb.Temporal.tryParseDateComponents text |> Option.isSome -> TDate
                | _ -> parameterType expected
            Diagnostics.suppress (fun () -> Storage.coerceValueWithMode strict (parameterColumn target) value)
            |> Result.defaultValue value
        | _ -> value

    let actual =
        List.zip3 analysis.Bindings expected values
        |> List.map (fun (binding, expected, value) ->
            match binding with
            | Some UnsignedIntegerOnly -> value
            | _ -> convert expected value)
    let reprepare =
        schemaChanged
        || retained.Context <> original
        || (List.zip3 analysis.Bindings expected actual
            |> List.exists (fun (binding, expected, value) -> not (inherited binding) && not (accepts expected value)))

    let statement, analysis =
        if reprepare then
            let statement = refreshVariables statement
            statement, inferParameters store registry schema statement values.Length
        else
            statement, analysis
    let original = analysis.Definitions |> List.map (Option.defaultValue generic)

    let types =
        if reprepare then
            List.zip3 analysis.Bindings original actual
            |> List.map (fun (binding, original, value) ->
                if inherited binding || value = VNull then original else metadataOfValue value)
        else
            expected

    let bind binding definition expected value =
        match binding with
        | Some ColumnAssignment -> Ok value
        | Some UnsignedIntegerOnly ->
            match value with
            | VInt number when number >= 0L -> Ok value
            | VInt _ -> Error(1690, "signed integer value is out of range in 'EXECUTE'")
            | VUInt _ | VNull -> Ok value
            | VString _ when source = ProtocolValues -> Ok value
            | _ -> Error(1210, "Incorrect arguments to EXECUTE")
        | _ ->
            let derivedCoercion =
                match family expected with
                | CalendarDate | ClockTime | CalendarTime -> Some(parameterType expected)
                | _ -> numericParameterType expected

            let coercion =
                match binding with
                | Some(InheritedType metadata) -> numericParameterType metadata
                | Some(CoerceNumericAs metadata) ->
                    match family metadata with
                    | Integral _ -> numericParameterType metadata
                    | _ -> derivedCoercion
                | _ -> derivedCoercion

            match coercion with
            | None -> Ok value
            | Some columnType ->
                let value =
                    match family (ColumnWire.parameterMetadataOfType columnType), value with
                    | Integral _, VDouble number -> VDouble(Math.Round(number, MidpointRounding.ToEven))
                    | _ -> value
                let coerced = coerce columnType value
                // A bare marker returns its supplied decimal scale; arithmetic uses the derived scale.
                match definition, family expected, value, coerced with
                | None, ExactNumeric, VDecimal _, _ -> Ok value
                | None, ExactNumeric, _, VDecimal number ->
                    Ok(VDecimal(Decimal.Parse(number.ToString("G29", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)))
                | _ -> Ok coerced

    let rec bindAll parameterIndex bindings definitions types values bound =
        match bindings, definitions, types, values with
        | [], [], [], [] -> Ok(List.rev bound)
        | binding :: bindings, definition :: definitions, expected :: types, value :: values ->
            bind binding definition expected value
            |> Result.bind (fun value ->
                let expression =
                    match binding, value, family expected with
                    | Some(UnsignedIntegerOnly | ColumnAssignment), _, _ -> Lit value
                    | _, VNull, _ | _, _, Integral _ when Set.contains parameterIndex analysis.ProjectedParameters ->
                        Cast(Lit value, parameterType expected)
                    | _ -> Lit value
                bindAll (parameterIndex + 1) bindings definitions types values (expression :: bound))
        | _ -> Error(1210, "Incorrect arguments to EXECUTE")

    bindAll 0 analysis.Bindings analysis.Definitions types actual []
    |> Result.map (fun expressions -> { Context = original; Derived = types }, statement, expressions)
