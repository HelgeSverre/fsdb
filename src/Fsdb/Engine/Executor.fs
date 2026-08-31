/// Executes SQL statements against Storage and returns typed result rows.
module Fsdb.Executor

open System.Collections.Generic
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.RegularExpressions
open Fsdb.Ast
open Fsdb.Value
open Fsdb.Storage
open Fsdb.Functions
open Fsdb.Sql
open Fsdb.Engine

/// Mirrors the wire layer's text-resultset shape (columns as names, rows as
/// text-protocol option strings) so `QueryHandler` can hand a parsed
/// statement straight through without a translation layer of its own.
type QueryResult =
    private
    | Rows of columns: string list * rows: (string option list) list
    | RowCount of affectedRows: uint64
    | Failure of SqlState.Error
    | ResultCollection of results: (QueryResult * ColumnMetadata list) list

let ResultSet(columns, rows) = Rows(columns, rows)
let Affected affectedRows = RowCount affectedRows
let Err(code, message) = Failure(SqlState.create code message)
let ErrState(code, state, message) = Failure(SqlState.createWithState code state message)
let ErrInfo error = Failure error
let MultipleResults results = ResultCollection results

let (|ResultSet|Affected|Err|MultipleResults|) =
    function
    | Rows(columns, rows) -> ResultSet(columns, rows)
    | RowCount affectedRows -> Affected affectedRows
    | Failure error -> Err(error.Code, error.Message)
    | ResultCollection results -> MultipleResults results

let errorInfo =
    function
    | Failure error -> Some error
    | _ -> None

let private nestedResultsError context =
    Err(1105, sprintf "Multiple resultsets are not valid in %s" context)

let private nestedSubqueryResultsError = 1105, "Multiple resultsets are not valid in a subquery"

/// An expression-evaluation failure: a MySQL error code and message, the
/// same shape `Storage.toMySqlError` produces, so both error sources funnel
/// into `Err` the same way.
type private EvalError = int * string

type VariableContext =
    { UserVariables: Map<string, Value> ref
      ReadSystemVariable: string -> string -> Result<Value option, int * string>
      MaxUserVariables: int }

type RoutineVariable =
    { Column: ColumnDef
      Value: Value }

type internal TriggerTextExecution =
    { TriggerStore: Store
      TriggerRegistry: Registry
      TriggerDatabase: string
      TriggerAccount: Auth.Account
      TriggerProtectedTables: Set<string * string>
      TriggerUserVariables: Map<string, Value> ref }

type private TriggerRowScope =
    { Columns: ColumnDef list
      Old: Value[] option
      New: Value[] option }

type private ViewCheckScope =
    { Database: string
      Table: string
      View: string
      Predicate: Expr option }

type private RangeLookupBounds =
    { Column: string
      Lower: (Value * bool) option
      Upper: (Value * bool) option }

type private RangeColumnScope =
    | BareOrQualifiedRange
    | QualifiedRange

type private EqualityAccessPlan =
    { KeyName: string
      ColumnIndices: int list
      PrefixLengths: int option list
      Columns: ColumnDef list
      Unique: bool
      Rows: (RowId * Value[]) list }

type private PointEquality =
    { Column: string
      Transform: IndexTransform option
      Value: Value }

type private LiteralInProbe =
    { Columns: (string * IndexTransform option) list
      Values: Value list list }

type private IndexOrderPlan =
    { KeyName: string
      ColumnIndices: int list
      Columns: ColumnDef list
      EstimatedRows: int
      Rows: Value[] seq }

type private IndexedJoinPlan =
    { Table: Table
      KeyName: string
      ColumnIndices: int list
      PrefixLengths: int option list
      Unique: bool
      References: string list
      HasResidual: bool }

type private IndexedJoinProbe =
    { Table: Table
      Index: EqualityIndex
      LeftIndices: int list }

type private FullTextPredicatePlan =
    { Rows: (RowId * Value[]) list
      PredicateFor: RowId -> Expr
      ProbePredicate: Expr }

type private FullTextPhysicalSource =
    { Qualifier: string
      Item: FromItem
      Table: Table }

type private FullTextSourcePlan =
    { Source: FullTextPhysicalSource
      Scores: (Expr * MatchMode * Map<RowId, float>) list
      Synthetic: (Expr * string) list
      Columns: ColumnDef list
      Rows: Value[] seq }

type private MutationSource =
    { Qualifier: string
      PhysicalTable: TableRef option
      Columns: ColumnDef list }

type private LockingReadSource =
    { Qualifier: string
      Reference: TableRef
      Table: Table }

let private insertSelectSourceAliasPrefix = "\u0000fsdb_odku_source_"

type private EqualityMembershipDomain =
    | SignedIntegerMembership
    | DecimalMembership
    | TextMembership of Collation.Collation

type private EqualityMembership =
    { Values: Set<Value>
      ContainsNull: bool
      Domain: EqualityMembershipDomain }

type private RowEqualityMembership =
    { Values: Set<Value list>
      ContainsNullableRows: bool
      Domains: EqualityMembershipDomain list }

type private ExpressionSubqueryResult =
    { Result: QueryResult
      Rows: Value[] list
      EqualityMembership: EqualityMembership option
      RowEqualityMembership: RowEqualityMembership option }

type private MemoizedSubquery =
    | MemoizedSubquery of ExpressionSubqueryResult
    | UnmemoizedSubquery

let private equalityMembershipKey domain value =
    match domain, value with
    | SignedIntegerMembership, VInt _
    | DecimalMembership, VDecimal _ -> Some value
    | TextMembership collation, VString text -> Some(VString(collation.KeyOf text))
    | _ -> None

let private equalityMembershipDomain (store: Store) (column: ColumnDef) =
    match column.Type with
    | TTinyInt _
    | TBool
    | TSmallInt _
    | TMediumInt _
    | TInt _
    | TBigInt false -> Some SignedIntegerMembership
    | TDecimal _ -> Some DecimalMembership
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText ->
        column.Collation
        |> Option.bind Collation.tryFind
        |> Option.orElseWith (fun () -> Some store.ExecutionSettings.ConnectionCollation)
        |> Option.map TextMembership
    | _ -> None

type private StatementMemo =
    { FromSubqueries: Dictionary<FromItem, Result<ColumnDef list * Value[] list, QueryResult>>
      ExpressionSubqueries: Dictionary<SelectStmt, MemoizedSubquery>
      CorrelatedEqualities: Dictionary<string * string * string, Storage.TransientEqualityLookup option>
      Views: Dictionary<string * string, Result<ColumnDef list * Value[] list, QueryResult>> }

let private statementMemo = System.Threading.AsyncLocal<StatementMemo>()

let private freshStatementMemo () =
    { FromSubqueries = Dictionary<FromItem, Result<ColumnDef list * Value[] list, QueryResult>>(HashIdentity.Reference)
      ExpressionSubqueries = Dictionary<SelectStmt, MemoizedSubquery>(HashIdentity.Reference)
      CorrelatedEqualities = Dictionary<string * string * string, Storage.TransientEqualityLookup option>()
      Views = Dictionary<string * string, Result<ColumnDef list * Value[] list, QueryResult>>() }

let private resetStatementMemo () = statementMemo.Value <- freshStatementMemo ()

let private currentStatementMemo () = DynamicScope.getOrCreate freshStatementMemo statementMemo

/// Statement-local materialized CTE bindings, keyed by normalized name.
let private cteScope = System.Threading.AsyncLocal<Map<string, ColumnDef list * Value[] list>>()
let private cteRecursionDepth = System.Threading.AsyncLocal<int64 option>()
let private groupConcatMaxLen = System.Threading.AsyncLocal<int option>()
let private viewStack = System.Threading.AsyncLocal<Set<string * string>>()
let private variableContext = System.Threading.AsyncLocal<VariableContext option>()
let private routineVariables = System.Threading.AsyncLocal<Map<string, RoutineVariable> ref option>()
let private triggerTextExecutor = System.Threading.AsyncLocal<(TriggerTextExecution -> string -> QueryResult) option>()
let private suppressVariableAssignments = System.Threading.AsyncLocal<bool>()
let private metadataProbe = System.Threading.AsyncLocal<bool>()
let private triggerRowScope = System.Threading.AsyncLocal<TriggerRowScope option>()
let private viewCheckScope = System.Threading.AsyncLocal<ViewCheckScope option>()
let private lockingReadRows = System.Threading.AsyncLocal<Map<string, Set<RowId>>>()
let private lockingReadStore = System.Threading.AsyncLocal<(unit -> Store) option>()
let private lockingReadTimeout = System.Threading.AsyncLocal<System.TimeSpan option>()

let withVariableContext (variables: VariableContext) (body: unit -> 'a) : 'a =
    DynamicScope.withValue variableContext (Some variables) body

let withRoutineVariables (variables: Map<string, RoutineVariable>) (body: unit -> 'a) : 'a =
    DynamicScope.withValue routineVariables (Some(ref variables)) body

let withRoutineVariableState (variables: Map<string, RoutineVariable> ref) (body: unit -> 'a) : 'a =
    DynamicScope.withValue routineVariables (Some variables) body

let currentRoutineVariables () = routineVariables.Value |> Option.map _.Value

let replaceRoutineVariables variables =
    routineVariables.Value |> Option.iter (fun current -> current.Value <- variables)

let internal withTriggerTextExecutor executor body =
    DynamicScope.withValue triggerTextExecutor (Some executor) body

let private currentVariableContext () = variableContext.Value

let private tryRoutineVariable (name: string) =
    routineVariables.Value
    |> Option.bind (fun variables -> Map.tryFind (name.ToLowerInvariant()) variables.Value)

let private withSuppressedVariableAssignments (body: unit -> 'a) : 'a =
    DynamicScope.withValue suppressVariableAssignments true body

let private withMetadataProbe (body: unit -> 'a) : 'a =
    DynamicScope.withValue metadataProbe true body

let internal isMetadataProbe () = metadataProbe.Value

let private withTriggerRowScope (scope: TriggerRowScope) (body: unit -> 'a) : 'a =
    DynamicScope.withValue triggerRowScope (Some scope) body

let private currentLockingReadRows () =
    DynamicScope.valueOrDefault Map.empty lockingReadRows

let withLockingReadStore (store: unit -> Store) (timeout: System.TimeSpan) (body: unit -> 'a) : 'a =
    DynamicScope.withValue lockingReadStore (Some store) (fun () ->
        DynamicScope.withValue lockingReadTimeout (Some timeout) body)

type private StoredView =
    { Name: string
      Schema: string
      Definition: string
      Columns: string list
      Definer: string
      CheckOption: string
      SecurityType: string
      Algorithm: string }

type private ViewAccess =
    { SecurityType: string
      Definer: string
      Database: string
      Table: string }

type private ViewColumnTarget =
    { Database: string
      Table: string
      Qualifier: string
      Column: string }

let private sameViewTarget (left: ViewColumnTarget) (right: ViewColumnTarget) =
    left.Database.Equals(right.Database, System.StringComparison.OrdinalIgnoreCase)
    && left.Table.Equals(right.Table, System.StringComparison.OrdinalIgnoreCase)
    && left.Qualifier.Equals(right.Qualifier, System.StringComparison.OrdinalIgnoreCase)

let private viewTargetKey (target: ViewColumnTarget) =
    target.Database, target.Table, target.Qualifier

type private ViewTargetKey = string * string * string

type private WritableViewSource =
    { Reference: TableRef
      Qualifier: string
      Columns: string list
      Expressions: Map<string, Expr>
      Targets: Map<string, ViewColumnTarget>
      Predicate: Expr option
      CheckPredicates: Map<ViewTargetKey, Expr>
      InsertableTargets: Set<ViewTargetKey>
      Mergeable: bool }

type private UpdatableView =
    { ViewDatabase: string
      ViewName: string
      Database: string
      Table: string
      Columns: Map<string, string>
      Targets: Map<string, ViewColumnTarget>
      Expressions: Map<string, Expr>
      OrderedColumns: string list
      Predicate: Expr option
      CheckPredicates: Map<ViewTargetKey, Expr>
      Insertable: bool
      InsertableTargets: Set<string * string * string>
      UpdateFrom: TableRef
      UpdateJoins: Join list
      AccessPath: ViewAccess list
      Definer: string
      SecurityType: string }

type private StoredTrigger =
    { Name: string
      Body: string
      Definer: string
      Order: int64
      SqlMode: string
      CharacterSetClient: string
      CollationConnection: string }

let private triggerExecutionSettings (trigger: StoredTrigger) : ExecutionSettings =
    { SqlModeText = trigger.SqlMode
      SqlMode = SqlMode.settingsFor trigger.SqlMode
      ConnectionCharset = trigger.CharacterSetClient
      ConnectionCollation =
        trigger.CollationConnection
        |> Collation.tryFind
        |> Option.defaultValue Collation.defaultCollation }

type private StoredCheck = SystemCatalog.Check.Entry

type private ViewColumnDescriptor =
    { Column: ColumnDef
      NumericParts: (int * int) option }

type private ColumnDescriptionSource =
    | StoredRelation of string
    | QueryBody of SelectOrUnion

let private currentViewStack () = DynamicScope.valueOrDefault Set.empty viewStack

let private checkStoredDefiner (store: Store) (definer: string) (db: string) (statement: Statement) =
    match Auth.tryParseAccount definer with
    | Some account when Auth.tryUserRowForAccount store account |> Option.isNone ->
        Error(1449, sprintf "The user specified as a definer ('%s') does not exist" definer)
    | Some account -> Auth.checkForAccount store account (Auth.requiredPrivilegesInStore store db statement)
    | None -> Error(1449, "The user specified as a definer ('') does not exist")

let private registryForDefiner (account: Auth.Account) (registry: Registry) =
    registry
    |> Functions.registerScalar "CURRENT_USER" (fun _ -> VString(Auth.formatAccount account))

let private registryAccount (registry: Registry) =
    Functions.lookup "CURRENT_USER" registry
    |> Option.bind (fun currentUser -> currentUser [] |> toText |> Option.bind Auth.tryParseAccount)

let private scalarExecutionAccount = System.Threading.AsyncLocal<Auth.Account option>()

let currentScalarExecutionAccount () : Auth.Account option =
    scalarExecutionAccount.Value

let private registryForViewSecurity
    (store: Store)
    (registry: Registry)
    (securityType: string)
    (definer: string)
    (schema: string)
    (statement: Statement)
    =
    if securityType.Equals("INVOKER", System.StringComparison.OrdinalIgnoreCase) then
        match registryAccount registry with
        | Some account ->
            Auth.checkForAccount store account (Auth.requiredPrivilegesInStore store schema statement)
            |> Result.map (fun () -> registry)
        | None -> Error(1449, "The current invoker account does not exist")
    else
        checkStoredDefiner store definer schema statement
        |> Result.bind (fun () ->
            match Auth.tryParseAccount definer with
            | Some account -> Ok(registryForDefiner account registry)
            | None -> Error(1449, "The user specified as a definer ('') does not exist"))

let private registryForView (store: Store) (registry: Registry) (view: StoredView) (statement: Statement) =
    registryForViewSecurity store registry view.SecurityType view.Definer view.Schema statement

/// Reads one view definition from the row-backed catalog. The effective output
/// names are JSON so every legal quoted identifier round-trips without inventing
/// a second escaping convention. Invalid catalog text is treated as an absent
/// list so direct catalog damage cannot crash the query worker.
let private tryStoredView (store: Store) (dbName: string) (viewName: string) : StoredView option =
    let eqI (left: string) (right: string) = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)
    let columns (value: string) =
        if value = "" then
            []
        else
            try
                match JsonSerializer.Deserialize<string[]>(value) with
                | null -> []
                | columns -> List.ofArray columns
            with :? JsonException ->
                []

    match scan store "mysql" "views" with
    | Error _ -> None
    | Ok(_, rows) ->
        rows
        |> Seq.choose SystemCatalog.View.tryRead
        |> Seq.tryFind (fun view -> eqI view.Name viewName && eqI view.Schema dbName)
        |> Option.map (fun view ->
            { Name = view.Name
              Schema = view.Schema
              Definition = view.Definition
              Columns = view.ColumnNames |> columns
              Definer = view.Definer
              CheckOption = view.CheckOption
              SecurityType = view.SecurityType
              Algorithm = view.Algorithm })

let private updatableViewOfSelect (store: Store) (view: StoredView) (select: SelectStmt) : UpdatableView option =
    let combine left right =
        match left, right with
        | Some left, Some right -> Some(BinOp(And, left, right))
        | Some predicate, None
        | None, Some predicate -> Some predicate
        | None, None -> None

    let hasWritableShape (select: SelectStmt) =
        not (view.Algorithm.Equals("TEMPTABLE", System.StringComparison.OrdinalIgnoreCase))
        && not select.Distinct
        && not select.CalculateFoundRows
        && select.GroupBy.IsEmpty
        && not select.Rollup
        && select.Windows.IsEmpty
        && select.Ctes.IsEmpty
        && select.Having.IsNone
        && select.OrderBy.IsEmpty
        && select.Limit.IsNone
        && select.Offset.IsNone
        && select.Locking.IsEmpty

    let exposesRequiredColumns database table columns =
        match InformationSchema.findTable store.Catalog database table with
        | Error _ -> true
        | Ok target ->
            target.Columns
            |> List.filter (fun column ->
                not column.Nullable
                && column.Default.IsNone
                && not column.AutoIncrement
                && column.Generated.IsNone)
            |> List.forall (fun column -> Set.contains (column.Name.ToLowerInvariant()) columns)

    let selectExpressions (select: SelectStmt) =
        (select.Projections |> List.map fst)
        @ (select.Where |> Option.toList)
        @ (select.Having |> Option.toList)
        @ select.GroupBy
        @ (select.OrderBy |> List.map fst)
        @ (select.Joins |> List.map _.On)

    let hasBareOuterReference outerColumns (select: SelectStmt) =
        let tableRefs =
            (match select.From with Some(FromTable table) -> [ table ] | None -> [] | _ -> [])
            @ (select.Joins |> List.choose (fun join -> match join.Table with FromTable table -> Some table | _ -> None))

        let physicalSourcesOnly =
            (select.From |> Option.forall (function FromTable _ -> true | _ -> false))
            && (select.Joins |> List.forall (fun join -> match join.Table with FromTable _ -> true | _ -> false))

        if not physicalSourcesOnly then
            false
        else
            let localColumns =
                tableRefs
                |> List.collect (fun tableRef ->
                    let database = tableRef.Database |> Option.defaultValue view.Schema

                    InformationSchema.findTable store.Catalog database tableRef.Table
                    |> Result.toOption
                    |> Option.map (fun table -> table.Columns |> List.map (fun column -> column.Name.ToLowerInvariant()))
                    |> Option.defaultValue [])
                |> Set.ofList

            let referencesOuterColumn =
                Expression.exists (function
                    | Col column ->
                        let name = column.ToLowerInvariant()
                        Set.contains name outerColumns && not (Set.contains name localColumns)
                    | _ -> false)

            selectExpressions select |> List.exists referencesOuterColumn

    let hasDependentSubquery outerColumns expression =
        Expression.fold
            (fun found node ->
                let dependent =
                    Expression.subqueries node
                    |> List.exists (fun select ->
                        Expression.hasQualifiedOuterReference select
                        || hasBareOuterReference outerColumns select)

                if found || dependent then
                    Expression.Prune true
                else
                    Expression.Descend false)
            false
            expression

    let rec classify seen (view: StoredView) (select: SelectStmt) =
        let key = view.Schema.ToLowerInvariant(), view.Name.ToLowerInvariant()

        if Set.contains key seen then
            None
        else
            match select.From with
            | Some(FromTable source)
                when select.Joins.IsEmpty
                     && hasWritableShape select ->
                let sourceNames = [ source.Table; source.Alias |> Option.defaultValue source.Table ]
                let aggregateNames = set [ "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "GROUP_CONCAT" ]

                let rec simplePredicate =
                    function
                    | Exists _
                    | Subquery _ -> true
                    | InSubquery(value, _)
                    | QuantifiedComparison(value, _, _, _) -> simplePredicate value
                    | WindowOver _ -> false
                    | QualifiedCol(qualifier, _) ->
                        sourceNames
                        |> List.exists (fun name -> name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase))
                    | FuncCall(name, arguments) ->
                        not (aggregateNames.Contains(name.ToUpperInvariant()))
                        && arguments |> List.forall simplePredicate
                    | BinOp(_, left, right)
                    | Like(left, right, _, _)
                    | Regexp(left, right) -> simplePredicate left && simplePredicate right
                    | Not value
                    | IsNull value
                    | IsNotNull value
                    | IsTrue value
                    | IsFalse value
                    | Distinct value
                    | OrderBy(value, _)
                    | Cast(value, _)
                    | Collate(value, _)
                    | AssignUserVariable(_, value) -> simplePredicate value
                    | In(value, candidates) -> simplePredicate value && candidates |> List.forall simplePredicate
                    | Between(value, lower, upper) -> simplePredicate value && simplePredicate lower && simplePredicate upper
                    | Case(subject, branches, otherwise) ->
                        subject |> Option.forall simplePredicate
                        && branches |> List.forall (fun (condition, result) -> simplePredicate condition && simplePredicate result)
                        && otherwise |> Option.forall simplePredicate
                    | MatchAgainst(_, query, _) -> simplePredicate query
                    | _ -> true

                if select.Where |> Option.exists (simplePredicate >> not) then
                    None
                else
                    let sourceDb = source.Database |> Option.defaultValue view.Schema

                    let underlyingStored = tryStoredView store sourceDb source.Table

                    let underlying =
                        underlyingStored
                        |> Option.bind (fun stored ->
                            match Parser.parse stored.Definition with
                            | Ok(Select definition) -> classify (Set.add key seen) stored definition
                            | _ -> None)

                    let rewriteSource =
                        match underlying with
                        | None ->
                            Expression.rewrite (function
                                | QualifiedCol(qualifier, column)
                                    when sourceNames
                                         |> List.exists (fun name -> name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)) ->
                                    Some(Col column)
                                | _ -> None)
                        | Some nested ->
                            Expression.rewrite (function
                                | Col column -> nested.Expressions |> Map.tryFind (column.ToLowerInvariant())
                                | QualifiedCol(qualifier, column)
                                    when sourceNames
                                         |> List.exists (fun name -> name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)) ->
                                    nested.Expressions |> Map.tryFind (column.ToLowerInvariant())
                                | _ -> None)

                    let physicalQualifier = source.Alias |> Option.defaultValue source.Table

                    let directTarget =
                        function
                        | Col column ->
                            match underlying with
                            | None ->
                                Some
                                    { Database = sourceDb
                                      Table = source.Table
                                      Qualifier = physicalQualifier
                                      Column = column }
                            | Some nested -> nested.Targets |> Map.tryFind (column.ToLowerInvariant())
                        | QualifiedCol(qualifier, column)
                            when sourceNames
                                 |> List.exists (fun name -> name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)) ->
                            match underlying with
                            | None ->
                                Some
                                    { Database = sourceDb
                                      Table = source.Table
                                      Qualifier = physicalQualifier
                                      Column = column }
                            | Some nested -> nested.Targets |> Map.tryFind (column.ToLowerInvariant())
                        | _ -> None

                    let projectedColumns =
                        underlying
                        |> Option.map _.OrderedColumns
                        |> Option.orElseWith (fun () ->
                            InformationSchema.findTable store.Catalog sourceDb source.Table
                            |> Result.toOption
                            |> Option.map (fun table -> table.Columns |> List.map _.Name))
                        |> Option.defaultValue []

                    let expandedProjections =
                        select.Projections
                        |> List.collect (fun (expression, alias) ->
                            match expression with
                            | Star None -> projectedColumns |> List.map (fun column -> Col column, None)
                            | Star(Some qualifier)
                                when sourceNames
                                     |> List.exists (fun name -> name.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)) ->
                                projectedColumns |> List.map (fun column -> Col column, None)
                            | _ -> [ expression, alias ])

                    let unresolvedStar =
                        projectedColumns.IsEmpty
                        && (select.Projections |> List.exists (fun (expression, _) -> match expression with Star _ -> true | _ -> false))

                    let dependentProjection =
                        expandedProjections
                        |> List.exists (fst >> hasDependentSubquery (projectedColumns |> List.map _.ToLowerInvariant() |> Set.ofList))

                    let projected =
                        expandedProjections
                        |> List.map (fun (expression, alias) ->
                            let defaultName =
                                match expression with
                                | Col column
                                | QualifiedCol(_, column) -> column
                                | _ -> InformationSchema.exprToSql expression

                            alias |> Option.defaultValue defaultName, rewriteSource expression, directTarget expression)

                    let outputNames = if view.Columns.IsEmpty then projected |> List.map (fun (name, _, _) -> name) else view.Columns

                    if
                        unresolvedStar
                        || dependentProjection
                        || outputNames.Length <> projected.Length
                        || (outputNames |> List.map (fun name -> name.ToLowerInvariant()) |> Set.ofList).Count <> outputNames.Length
                        || (projected |> List.forall (fun (_, _, target) -> target.IsNone))
                        || (underlying
                            |> Option.exists (fun nested ->
                                not nested.UpdateJoins.IsEmpty
                                && not (view.CheckOption.Equals("NONE", System.StringComparison.OrdinalIgnoreCase))))
                        || (underlying
                            |> Option.exists (fun nested ->
                                not nested.UpdateJoins.IsEmpty
                                && (underlyingStored
                                    |> Option.exists (fun stored ->
                                        not (stored.Definer.Equals(view.Definer, System.StringComparison.OrdinalIgnoreCase))
                                        || not (stored.SecurityType.Equals(view.SecurityType, System.StringComparison.OrdinalIgnoreCase))))))
                    then
                        None
                    else
                        let expressions =
                            List.map2 (fun (output: string) (_, expression, _) -> output.ToLowerInvariant(), expression) outputNames projected
                            |> Map.ofList

                        let writableTargets =
                            List.map2 (fun (output: string) (_, _, target) -> target |> Option.map (fun target -> output.ToLowerInvariant(), target)) outputNames projected
                            |> List.choose id
                            |> Map.ofList

                        let ownPredicate = select.Where |> Option.map rewriteSource
                        let underlyingPredicate = underlying |> Option.bind _.Predicate
                        let underlyingCheck =
                            underlying
                            |> Option.bind (fun nested ->
                                match Map.toList nested.CheckPredicates with
                                | [ _, predicate ] -> Some predicate
                                | _ -> None)
                        let predicate = combine underlyingPredicate ownPredicate

                        let checkPredicate =
                            match view.CheckOption.ToUpperInvariant() with
                            | "CASCADED" -> combine underlyingPredicate ownPredicate
                            | "LOCAL" -> combine underlyingCheck ownPredicate
                            | _ -> underlyingCheck

                        let firstTarget = writableTargets |> Map.toSeq |> Seq.head |> snd
                        let database, table = firstTarget.Database, firstTarget.Table

                        let updateFrom =
                            underlying
                            |> Option.map _.UpdateFrom
                            |> Option.defaultValue
                                { Database = Some database
                                  Table = table
                                  Alias = source.Alias
                                  Partitions = [] }

                        let allowedInsertTargets =
                            underlying
                            |> Option.map _.InsertableTargets
                            |> Option.defaultValue (Set.singleton (sourceDb, source.Table, physicalQualifier))

                        let insertableTargets =
                            if projected |> List.exists (fun (_, _, target) -> target.IsNone) then
                                Set.empty
                            else
                                projected
                                |> List.choose (fun (_, _, target) -> target)
                                |> List.groupBy (fun target -> target.Database, target.Table, target.Qualifier)
                                |> List.choose (fun (targetKey, targetColumns) ->
                                    let database, table, _ = targetKey
                                    let columns = targetColumns |> List.map (fun target -> target.Column.ToLowerInvariant())
                                    let distinct = Set.ofList columns

                                    if
                                        Set.contains targetKey allowedInsertTargets
                                        && distinct.Count = columns.Length
                                        && exposesRequiredColumns database table distinct
                                    then
                                        Some targetKey
                                    else
                                        None)
                                |> Set.ofList

                        Some
                            { ViewDatabase = view.Schema
                              ViewName = view.Name
                              Database = database
                              Table = table
                              Columns = writableTargets |> Map.map (fun _ target -> target.Column)
                              Targets = writableTargets
                              Expressions = expressions
                              OrderedColumns = outputNames
                              Predicate = predicate
                              CheckPredicates =
                                match underlying with
                                | Some nested when not nested.UpdateJoins.IsEmpty -> nested.CheckPredicates
                                | _ ->
                                    match checkPredicate with
                                    | Some predicate ->
                                        let targetKey = firstTarget.Database, firstTarget.Table, firstTarget.Qualifier
                                        Map.ofList [ targetKey, predicate ]
                                    | None -> Map.empty
                              Insertable = not insertableTargets.IsEmpty
                              InsertableTargets = insertableTargets
                              UpdateFrom = updateFrom
                              UpdateJoins = underlying |> Option.map _.UpdateJoins |> Option.defaultValue []
                              AccessPath =
                                { SecurityType = view.SecurityType
                                  Definer = view.Definer
                                  Database = sourceDb
                                  Table = source.Table }
                                :: (underlying |> Option.map _.AccessPath |> Option.defaultValue [])
                              Definer = view.Definer
                              SecurityType = view.SecurityType }
            | Some(FromTable source)
                when not select.Joins.IsEmpty
                     && (select.Joins |> List.forall (fun join -> join.Kind = InnerJoin))
                     && (select.Joins |> List.forall (fun join -> match join.Table with FromTable _ -> true | _ -> false))
                     && hasWritableShape select
                     && view.CheckOption.Equals("NONE", System.StringComparison.OrdinalIgnoreCase) ->
                let tableRefs =
                    source
                    :: (select.Joins
                        |> List.choose (fun join ->
                            match join.Table with
                            | FromTable table -> Some table
                            | _ -> None))

                let resolveSource (tableRef: TableRef) : WritableViewSource option =
                    let database = tableRef.Database |> Option.defaultValue view.Schema
                    let qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table

                    let readOnlySource (stored: StoredView) projections =
                        let projectedNames =
                            projections
                            |> List.map (fun (expression, alias) ->
                                alias
                                |> Option.orElseWith (fun () ->
                                    match expression with
                                    | Col column
                                    | QualifiedCol(_, column) -> Some column
                                    | Star _ -> None
                                    | _ -> Some(InformationSchema.exprToSql expression)))

                        if projectedNames |> List.exists Option.isNone then
                            None
                        else
                            let inferred = projectedNames |> List.choose id
                            let columns = if stored.Columns.IsEmpty then inferred else stored.Columns

                            if columns.Length <> inferred.Length then
                                None
                            else
                                let expressions =
                                    columns
                                    |> List.map (fun column -> column.ToLowerInvariant(), QualifiedCol(qualifier, column))
                                    |> Map.ofList

                                Some
                                    { Reference = { tableRef with Database = Some database }
                                      Qualifier = qualifier
                                      Columns = columns
                                      Expressions = expressions
                                      Targets = Map.empty
                                      Predicate = None
                                      CheckPredicates = Map.empty
                                      InsertableTargets = Set.empty
                                      Mergeable = false }

                    match tryStoredView store database tableRef.Table with
                    | Some stored ->
                        match Parser.parse stored.Definition with
                        | Ok(Select definition) ->
                            let nested =
                                if
                                    stored.Definer.Equals(view.Definer, System.StringComparison.OrdinalIgnoreCase)
                                    && stored.SecurityType.Equals(view.SecurityType, System.StringComparison.OrdinalIgnoreCase)
                                then
                                    classify (Set.add key seen) stored definition
                                else
                                    None

                            nested
                            |> Option.bind (fun nested ->
                                if not nested.UpdateJoins.IsEmpty then
                                    None
                                else
                                    let qualify =
                                        Expression.rewrite (function
                                            | Col column
                                            | QualifiedCol(_, column) -> Some(QualifiedCol(qualifier, column))
                                            | _ -> None)

                                    let targets =
                                        nested.Targets
                                        |> Map.map (fun _ target -> { target with Qualifier = qualifier })

                                    let checkPredicates =
                                        nested.CheckPredicates
                                        |> Map.toList
                                        |> List.map (fun ((targetDatabase, targetTable, _), predicate) ->
                                            (targetDatabase, targetTable, qualifier), predicate)
                                        |> Map.ofList

                                    let insertableTargets =
                                        nested.InsertableTargets
                                        |> Set.map (fun (targetDatabase, targetTable, _) -> targetDatabase, targetTable, qualifier)

                                    Some
                                        { Reference =
                                            { nested.UpdateFrom with
                                                Alias = Some qualifier }
                                          Qualifier = qualifier
                                          Columns = nested.OrderedColumns
                                          Expressions = nested.Expressions |> Map.map (fun _ expression -> qualify expression)
                                          Targets = targets
                                          Predicate = nested.Predicate |> Option.map qualify
                                          CheckPredicates = checkPredicates
                                          InsertableTargets = insertableTargets
                                          Mergeable = true })
                            |> Option.orElseWith (fun () -> readOnlySource stored definition.Projections)
                        | Ok(Union(first, _, _, _, _)) -> readOnlySource stored first.Projections
                        | _ -> None
                    | None ->
                        InformationSchema.findTable store.Catalog database tableRef.Table
                        |> Result.toOption
                        |> Option.map (fun table ->
                            let expressions =
                                table.Columns
                                |> List.map (fun column -> column.Name.ToLowerInvariant(), QualifiedCol(qualifier, column.Name))
                                |> Map.ofList

                            let targets =
                                table.Columns
                                |> List.map (fun column ->
                                    column.Name.ToLowerInvariant(),
                                    { Database = database
                                      Table = tableRef.Table
                                      Qualifier = qualifier
                                      Column = column.Name })
                                |> Map.ofList

                            { Reference = { tableRef with Database = Some database }
                              Qualifier = qualifier
                              Columns = table.Columns |> List.map _.Name
                              Expressions = expressions
                              Targets = targets
                              Predicate = None
                              CheckPredicates = Map.empty
                              InsertableTargets = Set.singleton (database, tableRef.Table, qualifier)
                              Mergeable = true })

                let sources =
                    tableRefs
                    |> List.choose resolveSource

                if sources.Length <> tableRefs.Length then
                    None
                else
                    let directTarget =
                        function
                        | QualifiedCol(qualifier, column) ->
                            sources
                            |> List.tryFind (fun source -> source.Qualifier.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase))
                            |> Option.bind (fun source -> source.Targets |> Map.tryFind (column.ToLowerInvariant()))
                        | Col column ->
                            sources
                            |> List.choose (fun source -> source.Targets |> Map.tryFind (column.ToLowerInvariant()))
                            |> function
                                | [ target ] -> Some target
                                | _ -> None
                        | _ -> None

                    let expandedProjections =
                        select.Projections
                        |> List.collect (fun (expression, alias) ->
                            match expression with
                            | Star None ->
                                sources
                                |> List.collect (fun source ->
                                    source.Columns |> List.map (fun column -> QualifiedCol(source.Qualifier, column), None))
                            | Star(Some qualifier) ->
                                sources
                                |> List.tryFind (fun source -> source.Qualifier.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase))
                                |> Option.map (fun source ->
                                    source.Columns |> List.map (fun column -> QualifiedCol(source.Qualifier, column), None))
                                |> Option.defaultValue [ expression, alias ]
                            | _ -> [ expression, alias ])

                    let rewriteSources =
                        Expression.rewrite (function
                            | QualifiedCol(qualifier, column) ->
                                sources
                                |> List.tryFind (fun source -> source.Qualifier.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase))
                                |> Option.bind (fun source -> source.Expressions |> Map.tryFind (column.ToLowerInvariant()))
                            | Col column ->
                                sources
                                |> List.choose (fun source -> source.Expressions |> Map.tryFind (column.ToLowerInvariant()))
                                |> function
                                    | [ expression ] -> Some expression
                                    | _ -> None
                            | _ -> None)

                    let projected =
                        expandedProjections
                        |> List.map (fun (expression, alias) ->
                            let defaultName =
                                match expression with
                                | Col column
                                | QualifiedCol(_, column) -> column
                                | _ -> InformationSchema.exprToSql expression

                            alias |> Option.defaultValue defaultName, rewriteSources expression, directTarget expression)

                    let outputNames = if view.Columns.IsEmpty then projected |> List.map (fun (name, _, _) -> name) else view.Columns

                    if
                        outputNames.Length <> projected.Length
                        || (outputNames |> List.map _.ToLowerInvariant() |> Set.ofList).Count <> outputNames.Length
                        || (projected |> List.forall (fun (_, _, target) -> target.IsNone))
                    then
                        None
                    else
                        let expressions =
                            List.map2 (fun (output: string) (_, expression, _) -> output.ToLowerInvariant(), expression) outputNames projected
                            |> Map.ofList

                        let targets =
                            List.map2 (fun (output: string) (_, _, target) -> target |> Option.map (fun target -> output.ToLowerInvariant(), target)) outputNames projected
                            |> List.choose id
                            |> Map.ofList

                        let directTargets = projected |> List.choose (fun (_, _, target) -> target)

                        let insertableTargets =
                            if
                                (sources |> List.exists (fun source -> not source.Mergeable))
                                || (projected |> List.exists (fun (_, _, target) -> target.IsNone))
                            then
                                Set.empty
                            else
                                directTargets
                                |> List.groupBy (fun target -> target.Database, target.Table, target.Qualifier)
                                |> List.choose (fun (targetKey, targetColumns) ->
                                    let distinct = targetColumns |> List.map (fun target -> target.Column.ToLowerInvariant()) |> Set.ofList
                                    let targetTable = targetColumns.Head
                                    let sourceAllowsInsert =
                                        sources
                                        |> List.exists (fun source -> Set.contains targetKey source.InsertableTargets)

                                    match InformationSchema.findTable store.Catalog targetTable.Database targetTable.Table with
                                    | Error _ -> None
                                    | Ok _
                                        when sourceAllowsInsert
                                             && distinct.Count = targetColumns.Length
                                             && exposesRequiredColumns targetTable.Database targetTable.Table distinct ->
                                        Some targetKey
                                    | Ok _ -> None)
                                |> Set.ofList

                        let firstTarget = directTargets.Head
                        let rewrittenJoins =
                            List.map2
                                (fun join source ->
                                    { join with
                                        Table = FromTable source.Reference
                                        On = rewriteSources join.On })
                                select.Joins
                                (List.tail sources)

                        let sourcePredicate =
                            sources
                            |> List.choose _.Predicate
                            |> List.fold (fun combined predicate -> combine combined (Some predicate)) None

                        let predicate = combine sourcePredicate (select.Where |> Option.map rewriteSources)

                        let checkPredicates =
                            sources
                            |> List.collect (fun source -> Map.toList source.CheckPredicates)
                            |> Map.ofList

                        Some
                            { ViewDatabase = view.Schema
                              ViewName = view.Name
                              Database = firstTarget.Database
                              Table = firstTarget.Table
                              Columns = targets |> Map.map (fun _ target -> target.Column)
                              Targets = targets
                              Expressions = expressions
                              OrderedColumns = outputNames
                              Predicate = predicate
                              CheckPredicates = checkPredicates
                              Insertable = not insertableTargets.IsEmpty
                              InsertableTargets = insertableTargets
                              UpdateFrom = (List.head sources).Reference
                              UpdateJoins = rewrittenJoins
                              AccessPath = []
                              Definer = view.Definer
                              SecurityType = view.SecurityType }
            | _ -> None

    classify Set.empty view select

let private storedChecks (store: Store) (dbName: string) (tableName: string) : StoredCheck list =
    let eqI left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

    match scan store "mysql" "check_constraints" with
    | Error _ -> []
    | Ok(_, rows) ->
        rows
        |> Seq.choose SystemCatalog.Check.tryRead
        |> Seq.filter (fun check -> eqI check.Schema dbName && eqI check.Table tableName)
        |> Seq.sortBy _.Ordinal
        |> List.ofSeq

let withCteRecursionDepth (limit: int64) (body: unit -> 'a) : 'a =
    DynamicScope.withValue cteRecursionDepth (Some limit) body

let withGroupConcatMaxLen (limit: int) (body: unit -> 'a) : 'a =
    DynamicScope.withValue groupConcatMaxLen (Some limit) body

let private currentCteScope () : Map<string, ColumnDef list * Value[] list> =
    DynamicScope.valueOrDefault Map.empty cteScope

let private unknownColumn (name: string) : EvalError =
    1054, sprintf "Unknown column '%s' in 'field list'" name

let private unknownFunction (name: string) : EvalError =
    1305, sprintf "FUNCTION %s does not exist" name

let private storageErr (e: StorageError) : QueryResult =
    let code, message = toMySqlError e
    Err(code, message)

/// The leading numeric run of `s`, the way MySQL's numeric `CAST`/implicit
/// string-to-number conversion reads a string — `"12abc"` yields
/// `Some "12"`, `"abc"` yields `None`. Unlike `Storage.coerceValue`'s
/// `parseNumeric`, which requires the *whole* trimmed string to parse. An
/// integer target reads only an optional sign and digits and stops there —
/// `CAST('1e3' AS SIGNED)` is `1` in MySQL, not `1000` — while a
/// DECIMAL/float target also honors a fraction and exponent
/// (`CAST('1e3' AS DECIMAL(10,2))` is `1000`), so the two targets need
/// different grammars rather than one regex serving both.
let private leadingIntegerPrefixRegex = Regex(@"^\s*[+-]?\d+")
let private leadingFloatPrefixRegex = Regex(@"^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?")

let private leadingNumericPrefix (regex: Regex) (s: string) : string option =
    let m = regex.Match s
    if m.Success && m.Value.Trim() <> "" then Some(m.Value.Trim()) else None

/// Column name (case-insensitive) to *every* index it resolves to in a row
/// array — usually exactly one, but a `JOIN` can combine two tables that
/// both have a column of the same name (`SELECT id FROM u JOIN p ON
/// p.uid = u.id`). Keeping every match (rather than `Map.ofList`'s silent
/// "last one wins") is what lets `resolveCol` tell an ambiguous bare
/// reference apart from an unambiguous one and raise error 1052, the same
/// way real MySQL does, instead of silently binding to whichever table
/// happened to be listed last in the `JOIN`.
let private columnIndexOf (columns: ColumnDef list) : Map<string, int list> =
    columns
    |> List.mapi (fun i c -> c.Name.ToLowerInvariant(), i)
    |> List.groupBy fst
    |> List.map (fun (name, xs) -> name, xs |> List.map snd)
    |> Map.ofList

/// Which clause a bare `Col` is being resolved from — the only thing that
/// varies between an ambiguous-column 1052 in a field list, a `WHERE`, a
/// JOIN `ON`, an `ORDER BY`, or a `GROUP BY`/`HAVING` is the four words
/// MySQL puts in the error message, so `resolveCol` takes one of these
/// instead of five near-identical copies of itself.
type private Clause =
    | FieldList
    | WhereClause
    | OnClause
    | OrderClause
    | GroupStatement

let private clauseLabel =
    function
    | FieldList -> "field list"
    | WhereClause -> "where clause"
    | OnClause -> "on clause"
    | OrderClause -> "order clause"
    | GroupStatement -> "group statement"

/// Aggregate-call recognition: a `FuncCall` whose name is registered as an
/// aggregate on `registry` (see `Functions.Registry.Aggregates`) rather than
/// a hardcoded name set here — the `registerAggregate` extension point is
/// the same one `Functions` itself uses for COUNT/SUM/AVG/MIN/MAX — plus
/// `GROUP_CONCAT`, which is always recognized directly since its
/// `SEPARATOR`/multi-arg evaluation lives entirely in `evalAggregate` below
/// rather than the registry (see `Parser.groupConcatAtom`'s doc for why it's
/// not just another `registerAggregate` entry).
///
/// `JSON_ARRAYAGG`/`JSON_OBJECTAGG` are recognized the same direct way, for
/// the same reason: an `Aggregate` sees one already-NULL-filtered value per
/// row, and neither fits — `JSON_ARRAYAGG` must keep its NULL rows as JSON
/// `null`, and `JSON_OBJECTAGG` takes two arguments per row.
let private directAggregateNames =
    set [ "GROUP_CONCAT"; "JSON_ARRAYAGG"; "JSON_OBJECTAGG" ]

let private isAggregateCall (registry: Registry) (expr: Expr) : bool =
    match expr with
    | FuncCall(name, _) ->
        directAggregateNames.Contains(name.ToUpperInvariant())
        || Functions.lookupAggregate name registry |> Option.isSome
    | _ -> false

/// Whether `expr` contains an aggregate call *anywhere*, not just at the
/// top level — `SELECT COUNT(*) + 1 FROM t` or a `WHERE`-style predicate
/// nesting one inside a `HAVING`-shaped expression both need this to switch
/// `runSelect` onto `runGroupedSelect`'s path, the same walk
/// `substituteValuesFunc` already does for `VALUES(col)` rewriting.
let private containsAggregate (registry: Registry) (expr: Expr) : bool =
    Expression.fold
        (fun found node ->
            if found || isAggregateCall registry node then
                Expression.Prune true
            else
                match node with
                | WindowOver _ -> Expression.Prune false
                | _ -> Expression.Descend false)
        false
        expr

/// Every `RowNumberOver`/`LagOver` node inside `expr`, in encounter order —
/// pre-order, same walk shape as the later `collectSubqueries` for the
/// corresponding job. A window function can sit anywhere in a projection's
/// expression tree (`value - LAG(value) OVER (...)`), not just bare at the
/// top level, so both `runSelect`'s dispatch and `runWindowedSelect`'s
/// rewrite need every occurrence rather than only a top-level one.
let private collectWindowFuncs (expr: Expr) : Expr list =
    Expression.fold
        (fun found node ->
            match node with
            | WindowOver _ -> Expression.Prune(node :: found)
            | _ -> Expression.Descend found)
        []
        expr
    |> List.rev

/// Every topmost aggregate call inside `expr` (an aggregate nested in
/// another aggregate's arguments is never reached — MySQL rejects that
/// shape anyway) plus every aggregate inside a window function's own
/// arguments, which is where `SUM(COUNT(*)) OVER (...)` keeps its grouped
/// half. `runGroupedWindowSelect` projects each one from the grouped pass
/// so the window pass can read it back as a plain column.
let private collectAggregateCalls (registry: Registry) (expr: Expr) : Expr list =
    Expression.fold
        (fun found node ->
            if isAggregateCall registry node then
                Expression.Prune(node :: found)
            else
                Expression.Descend found)
        []
        expr
    |> List.rev

type private WindowRow = int * (Value list * (Value * Collation.Collation option) list * Value[])

/// Every call to the named function inside `expr` — one walker, since the
/// only caller (`GROUPING`, whose value depends on the ROLLUP level rather
/// than on any row) needs the nodes themselves to substitute, not a boolean.
let private collectCallsNamed (name: string) (expr: Expr) : Expr list =
    Expression.collect
        (function
        | FuncCall(called, _) as call
            when System.String.Equals(called, name, System.StringComparison.OrdinalIgnoreCase) ->
            Some call
        | _ -> None)
        expr

/// A synthetic pre-pass column (`__fsdb_window_N__`/`__fsdb_match_N__`).
/// Callers with expression metadata should prefer `deriveColumns`, since an
/// empty result has no runtime value from which to recover the wire type.
let private syntheticColumn (name: string) (ty: ColumnType) (nullable: bool) : ColumnDef =
    { Name = name
      Type = ty
      NumericDisplay = None
      Nullable = nullable
      Default = None
      AutoIncrement = false
      PrimaryKey = false
      Unique = false
      Generated = None
      Comment = ""
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

/// Every `MATCH ... AGAINST` node in an expression tree — the fulltext
/// pre-pass (`runFullTextSelect`) computes one owning-table score column per
/// distinct node, exactly like `collectWindowFuncs` feeds
/// `runWindowedSelect`.
let private collectMatchAgainst (expr: Expr) : Expr list =
    Expression.fold
        (fun found node ->
            match node with
            | MatchAgainst _ -> Expression.Prune(node :: found)
            | WindowOver _ -> Expression.Prune found
            | _ -> Expression.Descend found)
        []
        expr
    |> List.rev

/// Bare column names outside window expressions, including MATCH columns.
let private collectColRefs (expr: Expr) : string list =
    Expression.fold
        (fun found node ->
            match node with
            | Col name -> Expression.Descend(name :: found)
            | MatchAgainst(columns, _, _) ->
                columns
                |> List.fold (fun names column -> column.Name :: names) found
                |> Expression.Descend
            | WindowOver _ -> Expression.Prune found
            | _ -> Expression.Descend found)
        []
        expr
    |> List.rev

/// The first column reference — bare `Col` or table-qualified
/// `QualifiedCol` — anywhere in an expression, left to right.
/// `collectColRefs` above deliberately skips `QualifiedCol`; JSON_TABLE's
/// uncorrelated source needs both, because MySQL rejects each with a
/// *different* error (1109 vs 1054 — see `resolveFromItem`'s
/// `FromJsonTable` case). Subqueries stay opaque, same ceiling as
/// `collectColRefs`.
let private firstColumnRef (expr: Expr) : Expr option =
    Expression.fold
        (fun found node ->
            match found, node with
            | Some _, _ -> Expression.Prune found
            | None, (Col _ | QualifiedCol _) -> Expression.Prune(Some node)
            | None, MatchAgainst(column :: _, _, _) ->
                column.Qualifier
                |> Option.map (fun qualifier -> QualifiedCol(qualifier, column.Name))
                |> Option.defaultWith (fun () -> Col column.Name)
                |> Some
                |> Expression.Prune
            | None, WindowOver _ -> Expression.Prune None
            | None, _ -> Expression.Descend None)
        None
        expr

/// Replaces every window-function node `collectWindowFuncs` would find with
/// the plain `Col` reference `synthetic` maps it to (structural lookup — a
/// small association list rather than a `Map`, since `Expr` carries no
/// `Comparison` instance for a `Map` key to lean on). `runWindowedSelect`'s
/// rewrite step, generalized to substitute a window function nested inside
/// arithmetic/`CASE`/... in place, not just a bare top-level projection.
let private substituteExprs (replacements: (Expr * Expr) list) (expr: Expr) : Expr =
    Expression.rewrite
        (fun node ->
            replacements
            |> List.tryPick (fun (candidate, replacement) ->
                if candidate = node then Some replacement else None))
        expr

/// `substituteExprs` specialized to the window pre-pass: every window node
/// becomes its computed synthetic column.
let private substituteWindowFuncs (synthetic: (Expr * string) list) (expr: Expr) : Expr =
    substituteExprs (synthetic |> List.map (fun (e, name) -> e, Col name)) expr

let private opSymbol =
    function
    | And -> "AND"
    | Or -> "OR"
    | Xor -> "XOR"
    | Eq -> "="
    | Neq -> "<>"
    | Lt -> "<"
    | Lte -> "<="
    | Gt -> ">"
    | Gte -> ">="
    | Add -> "+"
    | Sub -> "-"
    | SignedSub -> "-"
    | Mul -> "*"
    | Div -> "/"
    | IntDiv -> "DIV"
    | NullSafeEq -> "<=>"

/// The column name MySQL gives an unaliased projection — exact for columns
/// and literals, a best-effort reconstruction of the source text for
/// everything else (real MySQL echoes the original expression text, which
/// the parser doesn't preserve).
let rec private exprLabel (expr: Expr) : string =
    match expr with
    | Lit v -> v |> toText |> Option.defaultValue "NULL"
    | MatchAgainst(cols, q, _) ->
        let columnLabel (column: MatchColumn) =
            column.Qualifier
            |> Option.map (fun qualifier -> qualifier + "." + column.Name)
            |> Option.defaultValue column.Name

        sprintf "match (%s) against (%s)" (cols |> List.map columnLabel |> String.concat ",") (exprLabel q)
    | Placeholder _ -> "?"
    | UserVariable variable -> variable.Sql
    | SystemVariable(scope, variable) -> "@@" + (scope |> Option.map (fun value -> value.ToLowerInvariant() + ".") |> Option.defaultValue "") + variable
    | AssignUserVariable(variable, value) -> variable.Sql + ":=" + exprLabel value
    | Col name -> name
    | QualifiedCol(_, col) -> col
    | FuncCall(name, [ Lit(VString label); _ ]) when name.Equals("NAME_CONST", System.StringComparison.OrdinalIgnoreCase) -> label
    | FuncCall(name, args) -> sprintf "%s(%s)" (name.ToUpperInvariant()) (args |> List.map exprLabel |> String.concat ", ")
    | Row values -> sprintf "(%s)" (values |> List.map exprLabel |> String.concat ", ")
    | BinOp(op, a, b) -> sprintf "%s %s %s" (exprLabel a) (opSymbol op) (exprLabel b)
    | Not e -> sprintf "not(%s)" (exprLabel e)
    | IsNull e -> sprintf "(%s is null)" (exprLabel e)
    | IsNotNull e -> sprintf "(%s is not null)" (exprLabel e)
    | IsTrue e -> sprintf "(%s is true)" (exprLabel e)
    | IsFalse e -> sprintf "(%s is false)" (exprLabel e)
    | Like(e, p, _, _) -> sprintf "(%s like %s)" (exprLabel e) (exprLabel p)
    | Regexp(e, p) -> sprintf "(%s regexp %s)" (exprLabel e) (exprLabel p)
    | In(e, xs) -> sprintf "(%s in (%s))" (exprLabel e) (xs |> List.map exprLabel |> String.concat ",")
    | InSubquery(e, _) -> sprintf "(%s in (...))" (exprLabel e)
    | QuantifiedComparison(e, op, quantifier, _) ->
        let quantifierName = match quantifier with Any -> "any" | All -> "all"
        sprintf "%s %s %s (...)" (exprLabel e) (opSymbol op) quantifierName
    | Between(e, lo, hi) -> sprintf "(%s between %s and %s)" (exprLabel e) (exprLabel lo) (exprLabel hi)
    | Cast(e, _) -> sprintf "cast(%s as ...)" (exprLabel e)
    | Collate(e, _) -> exprLabel e
    | Distinct e -> sprintf "distinct %s" (exprLabel e)
    | OrderBy(e, _) -> exprLabel e
    | Case _ -> "case"
    | Star None -> "*"
    | Star(Some q) -> sprintf "%s.*" q
    | WindowOver(fn, over) -> sprintf "%s over %s" (windowFnLabel fn) (overLabel over)
    | Exists _ -> "exists"
    | Subquery _ -> "(...)"

/// The `fn(args)` half of a window call's default column name — MySQL
/// echoes the query's own source text there, which the parser doesn't keep,
/// so this reconstructs it in MySQL's own lowercase spelling.
and private windowFnLabel (fn: WindowFn) : string =
    let args = List.map exprLabel >> String.concat ","

    match fn with
    | WinRowNumber -> "row_number()"
    | WinRank dense -> (if dense then "dense_rank" else "rank") + "()"
    | WinPercentRank -> "percent_rank()"
    | WinCumeDist -> "cume_dist()"
    | WinNTile n -> sprintf "ntile(%s)" (exprLabel n)
    | WinLagLead(lead, e, offset, deflt) ->
        sprintf "%s(%s)" (if lead then "lead" else "lag") (args (e :: (Option.toList offset @ Option.toList deflt)))
    | WinFirstValue e -> sprintf "first_value(%s)" (exprLabel e)
    | WinLastValue e -> sprintf "last_value(%s)" (exprLabel e)
    | WinNthValue(e, n) -> sprintf "nth_value(%s)" (args [ e; n ])
    | WinAggregate(name, args) -> exprLabel (FuncCall(name, args))

and private overLabel (over: OverClause) : string =
    match over with
    | OverName name -> name
    | OverSpec spec ->
        let boundLabel bound =
            match bound with
            | UnboundedPreceding -> "unbounded preceding"
            | UnboundedFollowing -> "unbounded following"
            | CurrentRow -> "current row"
            | BoundPreceding e -> sprintf "%s preceding" (exprLabel e)
            | BoundFollowing e -> sprintf "%s following" (exprLabel e)

        [ if not spec.PartitionBy.IsEmpty then
              yield "partition by " + (spec.PartitionBy |> List.map exprLabel |> String.concat ",")
          if not spec.OrderBy.IsEmpty then
              yield
                  "order by "
                  + (spec.OrderBy
                     |> List.map (fun (e, dir) -> exprLabel e + (if dir = Desc then " desc" else ""))
                     |> String.concat ",")
          match spec.Frame with
          | Some frame ->
              yield
                  sprintf
                      "%s between %s and %s"
                      (if frame.Unit = FrameRows then "rows" else "range")
                      (boundLabel frame.Start)
                      (boundLabel frame.End)
          | None -> () ]
        |> String.concat " "
        |> sprintf "(%s)"

/// Neither of these recurse into `evalExpr`, so they're plain top-level
/// `let`s rather than tied into its `rec ... and` group.
let private boolToValue (b: bool) : Value = VInt(if b then 1L else 0L)

/// LIKE under the engine's collation: case- and accent-insensitive per
/// character ('ä' LIKE 'a' is true), but never expanding — 'æ' LIKE 'ae' is
/// false while 'æ' = 'ae' is true, exactly as MySQL's per-character LIKE
/// behaves (both verified against 8.4). A small backtracking matcher
/// instead of `likeToRegex`: a regex can't fold per-character weight
/// classes without enumerating every accented variant.
/// `%` matches any run, `_` one character, and the escape character
/// (default backslash, as the parser leaves it unresolved in the pattern)
/// makes the following character literal. `LIKE BINARY` (caseSensitive)
/// compares characters byte-for-byte.
/// Every backtrack is guarded by `mark < slen`: once the last `%` has been
/// stretched over the whole subject there is nothing left to give it, and
/// resuming past the end would advance `mark` forever without the
/// pattern-consumed branch ever being reached (`'x' LIKE '%\%'` hung the
/// server that way).
/// Iterative glob matcher (two pointers with `%`-backtracking) — O(1) stack,
/// so a pattern with thousands of `%` or a long subject can't overflow it
/// the way a recursive matcher would. `escape` before a `%`/`_` makes it a
/// literal; `charEq` folds per the collation.
let private likeMatch (escape: char) (charEq: char -> char -> bool) (subject: string) (pattern: string) : bool =
    let slen, plen = subject.Length, pattern.Length
    let mutable si, pi = 0, 0
    // The last `%` position and the subject index to resume from if the
    // tail fails — the one backtrack point a glob match needs.
    let mutable star = -1
    let mutable mark = 0

    let escapedLiteralAt (p: int) =
        if p < plen && pattern.[p] = escape && p + 1 < plen then
            Some pattern.[p + 1]
        else
            None

    let mutable result = ValueNone

    while result.IsNone do
        if pi >= plen then
            // Pattern consumed: match iff the subject is too, else backtrack
            // to the last `%` if there was one.
            if si >= slen then result <- ValueSome true
            elif star >= 0 && mark < slen then
                pi <- star + 1
                mark <- mark + 1
                si <- mark
            else
                result <- ValueSome false
        else
            match escapedLiteralAt pi with
            | Some lit ->
                if si < slen && charEq subject.[si] lit then
                    si <- si + 1
                    pi <- pi + 2
                elif star >= 0 && mark < slen then
                    pi <- star + 1
                    mark <- mark + 1
                    si <- mark
                else
                    result <- ValueSome false
            | None ->
                match pattern.[pi] with
                | '%' ->
                    star <- pi
                    mark <- si
                    pi <- pi + 1
                | '_' when si < slen ->
                    si <- si + 1
                    pi <- pi + 1
                | p when si < slen && p <> '%' && p <> '_' && charEq subject.[si] p ->
                    si <- si + 1
                    pi <- pi + 1
                | _ ->
                    // Mismatch (or `_`/literal past the subject): backtrack.
                    if star >= 0 && mark < slen then
                        pi <- star + 1
                        mark <- mark + 1
                        si <- mark
                    else
                        result <- ValueSome false

    result |> ValueOption.defaultValue false

let private likeOp (coll: Collation.Collation option) (caseSensitive: bool) (escape: char option) (subject: Value) (pattern: Value) : Value =
    match subject, pattern with
    | VNull, _
    | _, VNull -> VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        let col = coll |> Option.defaultValue Collation.defaultCollation
        // MySQL's LIKE never trims trailing spaces, not even under a PAD
        // SPACE collation ('a ' LIKE 'a' is false under utf8mb4_unicode_ci,
        // MySQL-verified) — folding is per character only. LIKE BINARY is
        // byte-for-byte with no folding at all.
        let charEq = if caseSensitive then (=) else col.CharEquals
        boolToValue (likeMatch (escape |> Option.defaultValue '\\') charEq text pat)

/// `REGEXP`/`RLIKE` — case sensitivity follows the operand's collation
/// (case-insensitive under the usual `_ci` default, but case-sensitive
/// under `_bin`/`_cs`, same as `LIKE`/`=`); unlike `LIKE`'s translated
/// wildcard syntax, the pattern is
/// already a real (POSIX-flavored, close enough to .NET's for the common
/// cases Eloquent generates) regex, so it's handed to `Regex` directly
/// rather than through `likeToRegex`. Every match carries a `MatchTimeout`
/// so a catastrophically-backtracking pattern (`'(a+)+$'` against a long
/// non-matching subject) errors out instead of pinning a core forever.
/// No `Singleline`: MySQL's REGEXP `.` does not match a newline by default,
/// so .NET's default (also newline-excluding) `.` is the matching behavior.
let private regexpOp (coll: Collation.Collation option) (subject: Value) (pattern: Value) : Result<Value, EvalError> =
    match subject, pattern with
    | VNull, _
    | _, VNull -> Ok VNull
    | _ ->
        let text = subject |> toText |> Option.defaultValue ""
        let pat = pattern |> toText |> Option.defaultValue ""
        let col =
            if [ subject; pattern ] |> List.exists (tryRawBytes >> Option.isSome) then
                Collation.tryFind "utf8mb4_bin" |> Option.defaultValue Collation.defaultCollation
            else
                coll |> Option.defaultValue Collation.defaultCollation

        match Regexp.compile col None pat with
        | Error(Regexp.InvalidPattern _ as error) -> Error(Regexp.errorCode error, Regexp.errorMessage error)
        | Error Regexp.InvalidMatchType -> Error(1210, "Incorrect arguments to regexp function")
        | Ok regex ->
            try
                Ok(boolToValue (regex.IsMatch((Regexp.prepareInput None pat text).Text)))
            with :? RegexMatchTimeoutException ->
                Error(3699, "Timeout exceeded in regular expression match.")

/// The three pieces of context `evalExpr` needs to resolve a `Col`/`FuncCall`
/// against, bundled into one record rather than three loose parameters
/// threaded through every call site — aggregates add a fourth
/// (per-group accumulated results the outer expression binds against),
/// which becomes a field here instead of a fourth parameter at every one of
/// those call sites.
/// `Store`/`DbName` are only read by the `Exists` case (a nested `SELECT`
/// needs a whole store/database to run against, not just the current row),
/// but every `EvalContext` carries them rather than splitting into a second
/// "context with subquery support" type — `Exists` can appear inside any
/// expression (a `WHERE`, a projection, ...), so every call site would need
/// to know which one to build.
type private EvalContext =
    { Registry: Registry
      ColumnIndex: Map<string, int list>
      /// Per-source-table resolution for `QualifiedCol`: lowercased alias-
      /// or-table-name -> that source's own column list plus the offset its
      /// columns start at within `Row` (`Row` is one table's columns for a
      /// plain `FROM`, or every joined table's columns concatenated
      /// left-to-right for a JOIN — see `Executor.applyJoin`). Empty when
      /// there's no table in scope at all (e.g. a literal `INSERT ...
      /// VALUES` row), so any `QualifiedCol` there is a clean unknown-column
      /// error instead of an index-out-of-range.
      Qualifiers: Map<string, ColumnDef list * int>
      Row: Value[]
      Store: Store
      DbName: string
      /// The enclosing query's own context, if this one belongs to a
      /// subquery (`Exists`/`Subquery`/`InSubquery`) — `None` for every
      /// top-level statement. `Col`/`QualifiedCol` fall back to it when a
      /// name isn't in this context's own `ColumnIndex`/`Qualifiers`, which
      /// is what makes a *correlated* subquery (`WHERE EXISTS (SELECT 1
      /// FROM t2 WHERE t2.parent_id = t1.id)`) resolve `t1.id` at all: it
      /// isn't a column of `t2`, so it falls through to the outer row that
      /// was in scope when the subquery started running. `runSelectStmt`
      /// takes an `EvalContext option` for exactly this and passes it down
      /// to every context it builds while running that subquery, so the
      /// chain composes to any nesting depth.
      Outer: EvalContext option
      /// Which clause a bare `Col` lookup through this context is on behalf
      /// of — only wired up for the ambiguous-column 1052's message
      /// (`resolveCol`); `contextFactory` defaults every context to
      /// `FieldList`, and call sites override it with a record update
      /// (`{ ctx with Clause = WhereClause }`) for the handful of spots
      /// that resolve a `WHERE`/`ON`/`ORDER BY`/`GROUP BY` expression
      /// instead of a projection.
      Clause: Clause }

type private ResolvedJoinSource =
    { Columns: ColumnDef list
      Rows: Value[] seq
      PhysicalTable: Table option }

type private JoinSourceOverrides = Map<string, ResolvedJoinSource>

/// `EvalContext.Qualifiers` for a single unaliased/aliased table in scope —
/// every non-JOIN statement (`UPDATE`, `DELETE`, `INSERT ... ON DUPLICATE
/// KEY UPDATE`) builds it this way, one entry at offset 0.
let private singleQualifier (name: string) (columns: ColumnDef list) : Map<string, ColumnDef list * int> =
    Map.ofList [ name.ToLowerInvariant(), (columns, 0) ]

/// Everything constant for one statement, curried ahead of the row — every
/// `ctxFor`/`ctx` below collapses to one line instead of an eight-line
/// `EvalContext` record literal repeated at each call site.
let private contextFactory
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columnIndex: Map<string, int list>)
    (qualifiers: Map<string, ColumnDef list * int>)
    (outer: EvalContext option)
    : Value[] -> EvalContext =
    fun row ->
        { Registry = registry
          ColumnIndex = columnIndex
          Qualifiers = qualifiers
          Row = row
          Store = store
          DbName = dbName
          Outer = outer
          Clause = FieldList }

let private tryColumnDefAt (ctx: EvalContext) (index: int) : ColumnDef option =
    ctx.Qualifiers
    |> Map.toSeq
    |> Seq.tryPick (fun (_, (columns, offset)) ->
        let relative = index - offset
        if relative >= 0 && relative < columns.Length then Some columns.[relative] else None)

let private readColumnValue (store: Store) (column: ColumnDef) (value: Value) : Value =
    match store.ExecutionSettings.SqlMode.PadCharToFullLength, column.Type, value with
    | true, TChar length, VString text ->
        let padding = length - (text.EnumerateRunes() |> Seq.length)

        if padding > 0 then
            VString(text + System.String(' ', padding))
        else
            value
    | _ -> value

let private storedValuesMatchReadValues (store: Store) =
    not store.ExecutionSettings.SqlMode.PadCharToFullLength

/// Resolves a bare column against `ctx`, falling back to
/// `ctx.Outer`/its own outer/... on a miss — see `EvalContext.Outer`. Two or
/// more matches (a `JOIN` of tables that share a column name) is error 1052,
/// not a silent pick of whichever one `columnIndexOf` happened to see last.
let rec private resolveCol (ctx: EvalContext) (name: string) : Result<Value, EvalError> =
    match tryRoutineVariable name with
    | Some variable -> Ok variable.Value
    | None ->
        match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
        | Some [ i ] ->
            tryColumnDefAt ctx i
            |> Option.map (fun column -> readColumnValue ctx.Store column ctx.Row.[i])
            |> Option.defaultValue ctx.Row.[i]
            |> Ok
        | Some(_ :: _ :: _) -> Error(1052, sprintf "Column '%s' in %s is ambiguous" name (clauseLabel ctx.Clause))
        | Some [] | None ->
            match ctx.Outer with
            | Some parent -> resolveCol { parent with Clause = ctx.Clause } name
            | None -> Error(unknownColumn name)

/// The `QualifiedCol` counterpart of `resolveCol` — same outer-context
/// fallback, checked against `ctx.Qualifiers` instead of `ctx.ColumnIndex`.
let rec private resolveQualifiedCol (ctx: EvalContext) (table: string) (col: string) : Result<Value, EvalError> =
    let scope = triggerRowScope.Value
    match table.ToLowerInvariant(), scope with
    | ("old" | "new"), Some images ->
        let row =
            match table.ToLowerInvariant() with
            | "old" -> images.Old
            | _ -> images.New

        match row with
        | Some row ->
            match images.Columns |> List.tryFindIndex (fun column -> System.String.Equals(column.Name, col, System.StringComparison.OrdinalIgnoreCase)) with
            | Some index -> Ok(readColumnValue ctx.Store images.Columns.[index] row.[index])
            | None -> Error(unknownColumn (sprintf "%s.%s" table col))
        | None -> Error(unknownColumn (sprintf "%s.%s" table col))
    | _ ->
        match Map.tryFind (table.ToLowerInvariant()) ctx.Qualifiers with
        | Some(cols, offset) ->
            match cols |> List.tryFindIndex (fun c -> System.String.Equals(c.Name, col, System.StringComparison.OrdinalIgnoreCase)) with
            | Some idx -> Ok(readColumnValue ctx.Store cols.[idx] ctx.Row.[offset + idx])
            | None -> Error(unknownColumn (sprintf "%s.%s" table col))
        | None ->
            match ctx.Outer with
            | Some parent -> resolveQualifiedCol parent table col
            | None -> Error(unknownColumn (sprintf "%s.%s" table col))

/// Recovers the declared column behind a bare/qualified expression without
/// changing expression evaluation itself. This type context is needed only
/// by ORDER BY: ENUM values are stored as their labels for display and
/// equality, but MySQL sorts them by declaration ordinal.
let rec private tryColumnDefForExpr (ctx: EvalContext) (expr: Expr) : ColumnDef option =
    match expr with
    | Col name ->
        match tryRoutineVariable name with
        | Some variable -> Some variable.Column
        | None ->
            match Map.tryFind (name.ToLowerInvariant()) ctx.ColumnIndex with
            | Some [ index ] -> tryColumnDefAt ctx index
            | Some(_ :: _ :: _)
            | Some [] -> None
            | None -> ctx.Outer |> Option.bind (fun outer -> tryColumnDefForExpr outer expr)
    | QualifiedCol(table, col) ->
        match Map.tryFind (table.ToLowerInvariant()) ctx.Qualifiers with
        | Some(columns, _) ->
            columns
            |> List.tryFind (fun column ->
                System.String.Equals(column.Name, col, System.StringComparison.OrdinalIgnoreCase))
        | None -> ctx.Outer |> Option.bind (fun outer -> tryColumnDefForExpr outer expr)
    | _ -> None

/// The declared fractional-seconds precision (fsp) a temporal `ColumnType`
/// renders at — `Some 0..6` for the three fractional-second types, `None`
/// for anything else. `Some 0` (a bare `DATETIME`) still forces no fraction
/// at render, distinct from `None` (an expression/literal, which falls back
/// to `Value.toText`'s show-the-actual-digits behavior).
let private fspOfType (ty: ColumnType) : int option =
    match ty with
    | TDateTime fsp
    | TTimestamp fsp
    | TTime fsp -> Some fsp
    | TDecimal(_, scale, _) -> Some scale
    | _ -> None

/// The fsp an output *expression* renders at, so an explicit precision request
/// shows exactly its digits (an exact-second `.000000` and all): `CAST(x AS
/// DATETIME(6))` takes the cast's declared fsp, and `MAX`/`MIN` of a temporal
/// column inherit that column's fsp (they return one of the stored, already
/// rounded-to-fsp values unchanged). A plain column resolves through its
/// `ColumnDef`; everything else is `None` and falls back to `Value.toText`.
let rec private fspOfExpr (ctx: EvalContext) (expr: Expr) : int option =
    let fspOfValue value =
        match Value.toText value with
        | Some text ->
            let separator = text.LastIndexOf '.'

            if separator < 0 then
                Some 0
            else
                let fraction = text.Substring(separator + 1)
                if fraction.Length > 0 && fraction |> Seq.forall System.Char.IsDigit then Some(min 6 fraction.Length) else None
        | None -> None

    let greatestFsp expressions =
        expressions
        |> List.choose (fspOfExpr ctx)
        |> List.fold max 0
        |> Some

    match expr with
    | Cast(_, TDecimal(_, scale, _)) -> Some scale
    | Cast(_, TDouble _)
    | Cast(_, TFloat _) -> Some 6
    | Cast(source, TChar _)
    | Cast(source, TVarchar _) -> fspOfExpr ctx source
    | Cast(_, ty) -> fspOfType ty
    | Lit(VDouble _) -> Some 6
    | Lit value -> fspOfValue value
    | FuncCall(name, [ arg ]) when (let n = name.ToUpperInvariant() in n = "MAX" || n = "MIN") -> fspOfExpr ctx arg
    | FuncCall(name, [ arg ]) when (let n = name.ToUpperInvariant() in n = "TIME" || n = "SEC_TO_TIME") ->
        fspOfExpr ctx arg |> Option.defaultValue 0 |> Some
    | FuncCall(name, [ _; _; seconds ]) when name.Equals("MAKETIME", System.StringComparison.OrdinalIgnoreCase) ->
        fspOfExpr ctx seconds |> Option.defaultValue 0 |> Some
    | FuncCall(name, [ left; right ]) when name.Equals("TIMEDIFF", System.StringComparison.OrdinalIgnoreCase) ->
        greatestFsp [ left; right ]
    | FuncCall(name, args)
        when (let n = name.ToUpperInvariant() in n = "CURTIME" || n = "CURRENT_TIME" || n = "UTC_TIME") ->
        match args with
        | [ Lit value ] -> Some(Value.toDouble value |> int |> max 0 |> min 6)
        | _ -> Some 0
    | FuncCall(name, args) when (let n = name.ToUpperInvariant() in n = "NOW" || n = "CURRENT_TIMESTAMP") ->
        // `NOW(N)` renders exactly N digits (matching the precision `nowFn`
        // rounds the clock to); bare `NOW()` renders none (precision 0).
        match args with
        | [ Lit v ] -> Some(Value.toDouble v |> int |> max 0 |> min 6)
        | _ -> Some 0
    | _ -> tryColumnDefForExpr ctx expr |> Option.bind (fun c -> fspOfType c.Type)

let rec private sourceCharset (ctx: EvalContext) (expr: Expr) : string =
    match expr with
    | Collate(_, name) -> Collation.charsetOfCollation name
    | Cast(value, _) -> sourceCharset ctx value
    | _ ->
        tryColumnDefForExpr ctx expr
        |> Option.bind (fun column ->
            column.Charset
            |> Option.orElseWith (fun () -> column.Collation |> Option.map Collation.charsetOfCollation))
        |> Option.defaultValue "utf8mb4"

let private metadataCollationId name =
    Collation.idAndSortlen
    |> Map.tryFind name
    |> Option.map (fst >> uint16)

let rec private metadataOfExpr (ctx: EvalContext) (expr: Expr) : ColumnMetadata option =
    let simple typeId =
        let columnLength =
            if typeId = TypeTiny then 4u
            elif typeId = TypeShort then 6u
            elif typeId = TypeLong then 11u
            elif typeId = TypeLongLong then 20u
            elif typeId = TypeFloat then 12u
            elif typeId = TypeDouble then 22u
            elif typeId = TypeNewDecimal then 67u
            elif typeId = TypeDate then 10u
            elif typeId = TypeDateTime then 19u
            elif typeId = TypeTime then 10u
            elif typeId = TypeYear then 4u
            else 0u

        Some { Value.columnMetadata typeId with ColumnLength = columnLength }
    let typeIdOf expression = metadataOfExpr ctx expression |> Option.map _.TypeId

    let numeric left right =
        let isInteger typeId =
            typeId = TypeTiny || typeId = TypeShort || typeId = TypeLong || typeId = TypeLongLong || typeId = TypeYear

        let leftMetadata = metadataOfExpr ctx left
        let rightMetadata = metadataOfExpr ctx right

        let inferred =
            match leftMetadata, rightMetadata with
            | Some leftType, _ when leftType.TypeId = TypeDouble || leftType.TypeId = TypeFloat -> simple TypeDouble
            | _, Some rightType when rightType.TypeId = TypeDouble || rightType.TypeId = TypeFloat -> simple TypeDouble
            | Some leftType, _ when leftType.TypeId = TypeString || leftType.TypeId = TypeVarString || leftType.TypeId = TypeBlob -> simple TypeDouble
            | _, Some rightType when rightType.TypeId = TypeString || rightType.TypeId = TypeVarString || rightType.TypeId = TypeBlob -> simple TypeDouble
            | Some leftType, _ when leftType.TypeId = TypeNewDecimal -> simple TypeNewDecimal
            | _, Some rightType when rightType.TypeId = TypeNewDecimal -> simple TypeNewDecimal
            | Some leftType, Some rightType when isInteger leftType.TypeId && isInteger rightType.TypeId -> simple TypeLongLong
            | Some leftType, None when isInteger leftType.TypeId -> simple TypeDouble
            | None, Some rightType when isInteger rightType.TypeId -> simple TypeDouble
            | _ -> None

        match inferred, leftMetadata, rightMetadata with
        | Some result, Some leftType, Some rightType
            when leftType.Flags &&& NotNullFlag <> 0us && rightType.Flags &&& NotNullFlag <> 0us ->
            Some { result with Flags = result.Flags ||| NotNullFlag }
        | _ -> inferred

    let choose expressions =
        let metadata = expressions |> List.choose (metadataOfExpr ctx)

        if metadata |> List.exists (fun m -> m.TypeId = TypeDouble || m.TypeId = TypeFloat) then
            simple TypeDouble
        elif metadata |> List.exists (fun m -> m.TypeId = TypeNewDecimal) then
            simple TypeNewDecimal
        else
            List.tryHead metadata

    let rec characterBound expression =
        match expression with
        | Collate(value, _) -> characterBound value
        | Lit(VString text) -> text.EnumerateRunes() |> Seq.length
        | Lit(VBytes bytes) -> bytes.Length
        | Lit(VBit(width, _)) -> (width + 7) / 8
        | Col _
        | QualifiedCol _ ->
            tryColumnDefForExpr ctx expression
            |> Option.bind (fun column ->
                match column.Type with
                | TChar length
                | TVarchar length -> Some length
                | _ -> None)
            |> Option.defaultValue 1
        | _ -> 1

    let weightStringMetadata (source: Expr) (charLength: int option) =
        match charLength with
        | Some length when
            match source with
            | Cast(_, TBinary _) -> true
            | _ -> false
            ->
            Some { Value.columnMetadata TypeVarString with ColumnLength = uint32 (max 8 length); Flags = BinaryFlag }
        | _ ->
            let charset = sourceCharset ctx source
            let bytesPerCharacter = if charset.StartsWith("utf8", System.StringComparison.Ordinal) then 4 else 1
            let isBinaryCollation =
                match source with
                | Collate(_, name) -> name.EndsWith("_bin", System.StringComparison.Ordinal)
                | _ ->
                    tryColumnDefForExpr ctx source
                    |> Option.exists (fun column ->
                        column.Charset = Some "binary"
                        || (column.Collation |> Option.exists (fun name -> name.EndsWith("_bin", System.StringComparison.Ordinal))))
            let multiplier = if isBinaryCollation || not (charset.StartsWith("utf8", System.StringComparison.Ordinal)) then 1 else 16
            let sourceLength = int64 (characterBound source) * int64 bytesPerCharacter * int64 multiplier
            let charLength = charLength |> Option.map (fun length -> int64 length * int64 multiplier) |> Option.defaultValue 0L
            let length = max sourceLength charLength |> max 8L |> min (int64 System.UInt32.MaxValue)
            Some { Value.columnMetadata TypeVarString with ColumnLength = uint32 length; Flags = BinaryFlag }

    let (|RegisteredScalarResult|_|) name =
        match Functions.lookup name ctx.Registry with
        | Some _ -> Functions.lookupScalarMetadata name ctx.Registry
        | None when name.Contains('.', System.StringComparison.Ordinal) -> None
        | None -> Functions.lookupScalarMetadata (ctx.DbName + "." + name) ctx.Registry

    match expr with
    | Lit VNull -> None
    | Lit(VInt value) ->
        let typeId =
            if value >= int64 System.SByte.MinValue && value <= int64 System.SByte.MaxValue then TypeTiny
            elif value >= int64 System.Int16.MinValue && value <= int64 System.Int16.MaxValue then TypeShort
            elif value >= int64 System.Int32.MinValue && value <= int64 System.Int32.MaxValue then TypeLong
            else TypeLongLong

        simple typeId |> Option.map (fun metadata -> { metadata with Flags = metadata.Flags ||| NotNullFlag })
    | Lit(VUInt _) -> Some { Value.columnMetadata TypeLongLong with Flags = UnsignedFlag ||| NotNullFlag }
    | Lit(VBit(width, _)) ->
        Some { Value.columnMetadata TypeBit with ColumnLength = uint32 width; Flags = UnsignedFlag ||| NotNullFlag }
    | Lit(VDouble _) -> simple TypeDouble |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VDecimal _) -> simple TypeNewDecimal |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VString text) ->
        Some
            { Value.columnMetadata TypeVarString with
                ColumnLength = uint32 (System.Text.Encoding.UTF8.GetByteCount text)
                Flags = NotNullFlag
                CollationId = metadataCollationId ctx.Store.ExecutionSettings.ConnectionCollation.Name }
    | Lit(VBytes bytes) -> Some { Value.columnMetadata TypeBlob with ColumnLength = uint32 bytes.Length; Flags = BlobFlag ||| BinaryFlag ||| NotNullFlag }
    | Lit(VDate _) -> simple TypeDate |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VDateTime _) -> simple TypeDateTime |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VTime _) -> Some { ColumnWire.metadataOfType(TTime 0) with Flags = BinaryFlag ||| NotNullFlag }
    | Lit(VZeroDate _) -> simple TypeDate |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VZeroDateTime _) -> simple TypeDateTime |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | Lit(VJson _) -> Some { ColumnWire.metadataOfType TJson with Flags = BinaryFlag ||| NotNullFlag }
    | UserVariable variable when variable.Sql = "@" -> simple TypeVarString
    | UserVariable variable ->
        currentVariableContext ()
        |> Option.bind (fun bindings -> bindings.UserVariables.Value |> Map.tryFind variable.Name)
        |> Option.bind (fun value -> metadataOfExpr ctx (Lit value))
        |> Option.orElse (simple TypeVarString)
    | SystemVariable(scope, variable) ->
        currentVariableContext ()
        |> Option.bind (fun bindings ->
            bindings.ReadSystemVariable (scope |> Option.defaultValue "") variable
            |> Result.toOption
            |> Option.flatten)
        |> Option.bind (fun value -> metadataOfExpr ctx (Lit value))
        |> Option.orElse (simple TypeVarString)
    | AssignUserVariable(_, value) -> metadataOfExpr ctx value
    | Lit(VGeometry _) ->
        Some { Value.columnMetadata TypeGeometry with
                   ColumnLength = 4294967295u
                   Flags = BlobFlag ||| BinaryFlag ||| NotNullFlag }
    | Col _
    | QualifiedCol _ -> tryColumnDefForExpr ctx expr |> Option.map ColumnWire.metadataOfColumn
    | Row _ -> None
    | BinOp((And | Or | Xor | Eq | Neq | Lt | Lte | Gt | Gte | NullSafeEq), _, _)
    | Not _
    | IsNull _
    | IsNotNull _
    | IsTrue _
    | IsFalse _
    | Like _
    | Regexp _
    | In _
    | InSubquery _
    | QuantifiedComparison _
    | Between _
    | Exists _ -> simple TypeLongLong
    | BinOp((Add | Sub | SignedSub | Mul), left, right) -> numeric left right
    | BinOp(Div, left, right) ->
        match typeIdOf left, typeIdOf right with
        | Some leftType, _ when leftType = TypeDouble || leftType = TypeFloat -> simple TypeDouble
        | _, Some rightType when rightType = TypeDouble || rightType = TypeFloat -> simple TypeDouble
        | Some _, _
        | _, Some _ -> simple TypeNewDecimal
        | _ -> None
    | BinOp(IntDiv, _, _) -> simple TypeLongLong
    | Cast(_, ty) -> Some(ColumnWire.metadataOfType ty)
    | Collate(inner, collation) ->
        metadataOfExpr ctx inner
        |> Option.map (fun metadata ->
            { metadata with
                CollationId = metadataCollationId collation })
    | Distinct inner
    | OrderBy(inner, _) -> metadataOfExpr ctx inner
    | FuncCall(name, [ argument ]) when name.Equals("DEFAULT", System.StringComparison.OrdinalIgnoreCase) ->
        metadataOfExpr ctx argument
    | FuncCall(name, [ _ ]) when name.Equals("COERCIBILITY", System.StringComparison.OrdinalIgnoreCase) ->
        simple TypeLongLong |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | FuncCall(name, [ _ ]) when name.Equals("SLEEP", System.StringComparison.OrdinalIgnoreCase) ->
        simple TypeLongLong |> Option.map (fun metadata -> { metadata with Flags = NotNullFlag })
    | FuncCall(name, [ _; _ ]) when name.Equals("BENCHMARK", System.StringComparison.OrdinalIgnoreCase) ->
        simple TypeLongLong
    | FuncCall(name, [ Cast(source, TBinary length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        weightStringMetadata (Cast(source, TBinary length)) (Some length)
    | FuncCall(name, [ Cast(source, TChar length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        weightStringMetadata source (Some length)
    | FuncCall(name, [ source ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        weightStringMetadata source None
    | FuncCall(RegisteredScalarResult metadata, _) -> Some metadata
    | FuncCall(name, args) ->
        match name.ToUpperInvariant(), args with
        | "COUNT", _ -> simple TypeLongLong
        | ("SUM" | "AVG"), [ arg ] ->
            match metadataOfExpr ctx arg with
            | Some metadata when
                metadata.TypeId = TypeDouble
                || metadata.TypeId = TypeFloat
                || metadata.Flags &&& (EnumFlag ||| SetFlag) <> 0us
                ->
                simple TypeDouble
            | _ -> simple TypeNewDecimal
        | ("MIN" | "MAX"), [ arg ] ->
            metadataOfExpr ctx arg
            |> Option.map (fun metadata ->
                if metadata.Flags &&& (EnumFlag ||| SetFlag) <> 0us then
                    { metadata with Flags = metadata.Flags &&& ~~~(EnumFlag ||| SetFlag) }
                else
                    metadata)
        | ("COALESCE" | "IFNULL"), values -> choose values
        | "ANY_VALUE", [ value ] -> metadataOfExpr ctx value
        | "NAME_CONST", [ _; value ] -> metadataOfExpr ctx value
        | "NULLIF", first :: _ -> metadataOfExpr ctx first
        | "IF", [ _; whenTrue; whenFalse ] -> choose [ whenTrue; whenFalse ]
        | ("ROUND" | "TRUNCATE" | "FLOOR" | "CEILING" | "CEIL" | "ABS"), arg :: _ -> metadataOfExpr ctx arg
        | "MOD", _ -> simple TypeLongLong
        | "YEAR", [ _ ] -> Some(ColumnWire.metadataOfType TYear)
        | "TIME", [ _ ] -> Some(ColumnWire.metadataOfType(TTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | "DATE", [ _ ] -> Some(ColumnWire.metadataOfType TDate)
        | ("NOW" | "CURRENT_TIMESTAMP" | "LOCALTIME" | "LOCALTIMESTAMP" | "SYSDATE"), _ ->
            Some(ColumnWire.metadataOfType(TDateTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | "UTC_TIMESTAMP", _ -> Some(ColumnWire.metadataOfType(TDateTime 0))
        | "UTC_DATE", _ -> Some(ColumnWire.metadataOfType TDate)
        | ("UTC_TIME" | "CURRENT_TIME" | "CURTIME"), _ -> Some(ColumnWire.metadataOfType(TTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | ("ADDTIME" | "SUBTIME"), first :: second :: _ ->
            let fsp = [ first; second ] |> List.choose (fspOfExpr ctx) |> List.fold max 0

            match metadataOfExpr ctx first with
            | Some metadata when metadata.TypeId = TypeTime -> Some(ColumnWire.metadataOfType(TTime fsp))
            | Some metadata when metadata.TypeId = TypeDateTime || metadata.TypeId = TypeTimestamp -> Some(ColumnWire.metadataOfType(TDateTime fsp))
            | metadata -> metadata
        | "TIMEDIFF", [ _; _ ] -> Some(ColumnWire.metadataOfType(TTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | "SEC_TO_TIME", [ _ ] -> Some(ColumnWire.metadataOfType(TTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | "MAKETIME", [ _; _; _ ] -> Some(ColumnWire.metadataOfType(TTime(fspOfExpr ctx expr |> Option.defaultValue 0)))
        | "TIME_FORMAT", _ -> Some { Value.columnMetadata TypeVarString with ColumnLength = 1024u }
        | "GET_FORMAT", _ -> Some { Value.columnMetadata TypeVarString with ColumnLength = 64u }
        | ("PERIOD_ADD" | "PERIOD_DIFF" | "TO_DAYS"), _ -> simple TypeLongLong
        | "FROM_DAYS", _ -> Some(ColumnWire.metadataOfType TDate)
        | ("MONTH" | "DAY" | "DAYOFMONTH" | "DAYOFWEEK" | "DAYOFYEAR" | "HOUR" | "MINUTE" | "SECOND" | "QUARTER" | "WEEK" | "WEEKDAY"
          | "JSON_LENGTH" | "JSON_DEPTH" | "CHAR_LENGTH" | "CHARACTER_LENGTH" | "LENGTH" | "OCTET_LENGTH" | "BIT_LENGTH" | "BIT_COUNT" | "IS_IPV4"
          | "IS_IPV6" | "IS_IPV4_COMPAT" | "IS_IPV4_MAPPED" | "JSON_MEMBER_OF" | "JSON_CONTAINS_PATH" | "JSON_OVERLAPS" | "JSON_STORAGE_SIZE"
          | "JSON_STORAGE_FREE"), _ ->
            simple TypeLongLong
        | "JSON_SCHEMA_VALID", _ -> simple TypeLongLong
        | "JSON_SCHEMA_VALIDATION_REPORT", _ ->
            Some { Value.columnMetadata TypeVarString with ColumnLength = 4294967295u }
        | ("ST_GEOMFROMTEXT" | "ST_GEOMETRYFROMTEXT" | "GEOMFROMTEXT" | "GEOMETRYFROMTEXT" | "ST_POINTFROMTEXT"
          | "POINTFROMTEXT" | "ST_LINESTRINGFROMTEXT" | "ST_POLYGONFROMTEXT" | "ST_GEOMFROMWKB" | "ST_GEOMETRYFROMWKB"
          | "GEOMFROMWKB" | "ST_POINTFROMWKB"), _ ->
            Some { Value.columnMetadata TypeGeometry with ColumnLength = 4294967295u; Flags = BlobFlag ||| BinaryFlag }
        | ("ST_ASTEXT" | "ST_ASWKT" | "ASTEXT" | "ST_GEOMETRYTYPE" | "GEOMETRYTYPE"), _ ->
            Some { Value.columnMetadata TypeVarString with ColumnLength = 4294967295u }
        | ("ST_ASWKB" | "ST_ASBINARY" | "ASBINARY"), _ ->
            Some { Value.columnMetadata TypeBlob with ColumnLength = 4294967295u; Flags = BlobFlag ||| BinaryFlag }
        | ("ST_ENVELOPE" | "ST_CONVEXHULL" | "ST_BUFFER"), _ ->
            Some { Value.columnMetadata TypeGeometry with ColumnLength = 4294967295u; Flags = BlobFlag ||| BinaryFlag }
        | ("ST_SRID" | "ST_DIMENSION" | "DIMENSION" | "ST_ISEMPTY" | "ISEMPTY" | "ST_ISVALID"), _ -> simple TypeLongLong
        | ("ST_CONTAINS" | "ST_WITHIN" | "ST_INTERSECTS" | "ST_DISJOINT" | "ST_TOUCHES" | "ST_EQUALS" | "MBRCONTAINS" | "MBRWITHIN"
          | "MBRINTERSECTS"), _ -> simple TypeLongLong
        | ("ST_X" | "ST_Y" | "X" | "Y" | "ST_DISTANCE"), _ -> simple TypeDouble
        | ("JSON_QUOTE" | "JSON_PRETTY"), _ -> Some { Value.columnMetadata TypeVarString with ColumnLength = 4294967295u }
        | ("AES_ENCRYPT" | "AES_DECRYPT" | "COMPRESS" | "UNCOMPRESS" | "RANDOM_BYTES"), _ ->
            Some { Value.columnMetadata TypeBlob with ColumnLength = 4294967295u; Flags = BlobFlag ||| BinaryFlag }
        | ("UNCOMPRESSED_LENGTH" | "UUID_SHORT"), _ ->
            Some { Value.columnMetadata TypeLongLong with ColumnLength = 21u; Flags = UnsignedFlag }
        | ("BIT_AND" | "BIT_OR" | "BIT_XOR" | "BITWISE_NOT" | "BITWISE_AND" | "BITWISE_OR" | "BITWISE_XOR" | "BITWISE_SHIFT_LEFT"
          | "BITWISE_SHIFT_RIGHT"), _ ->
            Some { Value.columnMetadata TypeLongLong with ColumnLength = 21u; Flags = UnsignedFlag }
        | "INET6_ATON", _ -> Some { Value.columnMetadata TypeVarString with ColumnLength = 16u; Flags = BinaryFlag; Decimals = 31uy }
        | "INET6_NTOA", _ -> Some { Value.columnMetadata TypeVarString with ColumnLength = 156u; Decimals = 31uy }
        | ("SQRT" | "LOG" | "LN" | "LOG2" | "LOG10" | "EXP" | "POWER" | "POW" | "PI" | "SIN" | "COS" | "TAN" | "COT" | "ASIN" | "ACOS"
          | "ATAN" | "ATAN2" | "DEGREES" | "RADIANS"), _ ->
            simple TypeDouble
        | _ -> None
    | MatchAgainst _ -> simple TypeDouble
    | WindowOver(fn, _) ->
        match fn with
        | WinRowNumber
        | WinRank _
        | WinNTile _ -> simple TypeLongLong
        | WinPercentRank
        | WinCumeDist -> simple TypeDouble
        | WinLagLead(_, arg, _, _)
        | WinFirstValue arg
        | WinLastValue arg
        | WinNthValue(arg, _) -> metadataOfExpr ctx arg
        | WinAggregate(name, args) -> metadataOfExpr ctx (FuncCall(name, args))
    | Case(_, whens, elseBranch) ->
        (whens |> List.map snd) @ Option.toList elseBranch |> choose
    | Subquery _
    | Placeholder _
    | Star _ -> None

let rec private columnsForQualifier (ctx: EvalContext) (qualifier: string) : ColumnDef list =
    match Map.tryFind (qualifier.ToLowerInvariant()) ctx.Qualifiers with
    | Some(columns, _) -> columns
    | None -> ctx.Outer |> Option.map (fun outer -> columnsForQualifier outer qualifier) |> Option.defaultValue []

type private OutputColumnFormat =
    { Fsp: int option
      Column: ColumnDef option }

let private outputFormatOfColumn column =
    { Fsp = fspOfType column.Type
      Column = Some column }

let private displayColumnForExpr ctx =
    function
    | FuncCall(name, [ argument ]) when name.Equals("DEFAULT", System.StringComparison.OrdinalIgnoreCase) ->
        tryColumnDefForExpr ctx argument
    | expression -> tryColumnDefForExpr ctx expression

let private outputColumnFormats (ctx: EvalContext) (columns: ColumnDef list) (projections: Projection list) : OutputColumnFormat list =
    projections
    |> List.collect (fun proj ->
        match proj with
        | Star None, _ -> columns |> List.map outputFormatOfColumn
        | Star(Some qualifier), _ -> columnsForQualifier ctx qualifier |> List.map outputFormatOfColumn
        | expr, _ ->
            [ { Fsp = fspOfExpr ctx expr
                Column = displayColumnForExpr ctx expr } ])

/// The declared fsp list must mirror projection expansion because `VDateTime`
/// alone does not retain its declared display precision.
let private outputColumnFsps ctx columns projections =
    outputColumnFormats ctx columns projections |> List.map _.Fsp

type private OutputColumnSource =
    { Qualifier: string
      Schema: string
      Table: string
      Columns: ColumnDef list }

let private outputColumnOrigins
    (store: Store)
    (dbName: string)
    (qualifiers: Map<string, ColumnDef list * int>)
    (select: SelectStmt)
    : ColumnOrigin option list =
    let sameName (left: string) (right: string) =
        System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

    let qualifierColumns (qualifier: string) =
        qualifiers
        |> Map.tryFind (qualifier.ToLowerInvariant())
        |> Option.map fst
        |> Option.defaultValue []

    let sourceItems = (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)

    let sourceQualifier = function
        | FromTable table -> table.Alias |> Option.defaultValue table.Table
        | FromSubquery(_, alias)
        | FromLateral(_, alias)
        | FromJsonTable(_, _, _, alias) -> alias

    let cteNames =
        seq {
            yield! currentCteScope () |> Map.keys
            yield! select.Ctes |> Seq.map (fun cte -> cte.CteName.ToLowerInvariant())
        }
        |> Set.ofSeq

    let physicalSources =
        sourceItems
        |> List.choose (function
            | FromTable table ->
                let schema = table.Database |> Option.defaultValue dbName
                let qualifier = table.Alias |> Option.defaultValue table.Table
                let isCte = table.Database.IsNone && Set.contains (table.Table.ToLowerInvariant()) cteNames

                if isCte || (tryStoredView store schema table.Table).IsSome then
                    None
                else
                    qualifiers
                    |> Map.tryFind (qualifier.ToLowerInvariant())
                    |> Option.map (fun (columns, _) ->
                        { Qualifier = qualifier
                          Schema = schema
                          Table = table.Table
                          Columns = columns })
            | _ -> None)

    let originFor source (column: ColumnDef) =
        { Schema = source.Schema
          Table = source.Qualifier
          OriginalTable = source.Table
          OriginalName = column.Name }

    let byQualifier qualifier name =
        physicalSources
        |> List.tryPick (fun source ->
            if sameName source.Qualifier qualifier then
                source.Columns
                |> List.tryFind (fun column -> sameName column.Name name)
                |> Option.map (originFor source)
            else
                None)

    let byName name =
        physicalSources
        |> List.choose (fun source ->
            source.Columns
            |> List.tryFind (fun column -> sameName column.Name name)
            |> Option.map (originFor source))
        |> function
            | [ origin ] -> Some origin
            | _ -> None

    let originsForExpression =
        function
        | Star None ->
            sourceItems
            |> List.collect (fun item ->
                let qualifier = sourceQualifier item

                match physicalSources |> List.tryFind (fun source -> sameName source.Qualifier qualifier) with
                | Some source -> source.Columns |> List.map (originFor source >> Some)
                | None -> qualifierColumns qualifier |> List.map (fun _ -> None))
        | Star(Some qualifier) ->
            match physicalSources |> List.tryFind (fun source -> sameName source.Qualifier qualifier) with
            | Some source -> source.Columns |> List.map (originFor source >> Some)
            | None -> qualifierColumns qualifier |> List.map (fun _ -> None)
        | Col name -> [ byName name ]
        | QualifiedCol(qualifier, name) -> [ byQualifier qualifier name ]
        | _ -> [ None ]

    select.Projections |> List.collect (fst >> originsForExpression)

/// Static result metadata for each projection, used ahead of the
/// value-derived fallback whenever the expression determines its own type.
///
/// The data-driven read can only see what a `Value` is, not what it was
/// declared as, so it reports every integer as `LONGLONG` and every string as
/// `VAR_STRING`. MySQL reports the declared type, and clients act on the
/// difference: an `ENUM` renders as `ENUM` only when the column definition
/// carries `ENUM_FLAG`, and `TINYINT(1)` is a `bool` to a client rather than a
/// number. Where a projection resolves back to a real base-table column, that
/// declared type wins.
///
/// `None` keeps the value-derived metadata for expressions whose result
/// family is not statically known. The projection expansion mirrors
/// `outputColumnFsps` so the lists remain column-aligned.
let private outputColumnWireOverridesFor
    (rollup: bool)
    (ctx: EvalContext)
    (columns: ColumnDef list)
    (select: SelectStmt)
    : ColumnMetadata option list =
    let projections = select.Projections

    let overrideOf (c: ColumnDef) =
        match c.Type with
        // WITH ROLLUP materializes each grouped column into a *nullable*
        // temporary to hold the super-aggregate row's NULL, and an enum's
        // value set doesn't survive that — MySQL reports the column as plain
        // VARCHAR there, so claiming ENUM would claim more than the server
        // delivers. Widths and BOOLEAN survive the temporary.
        | TEnum _ when rollup -> Some { ColumnWire.metadataOfColumn c with TypeId = TypeVarString; Flags = 0us }
        | _ -> ColumnWire.resultMetadataOf c

    let overrides =
        projections
        |> List.collect (fun proj ->
            match proj with
            | Star None, _ -> columns |> List.map overrideOf
            | Star(Some qualifier), _ -> columnsForQualifier ctx qualifier |> List.map overrideOf
            | expr, _ ->
                [ match tryColumnDefForExpr ctx expr |> Option.bind overrideOf with
                  | Some ty -> Some ty
                  | None -> metadataOfExpr ctx expr ])

    let origins = outputColumnOrigins ctx.Store ctx.DbName ctx.Qualifiers select

    if origins.Length = overrides.Length then
        List.map2
            (fun origin metadata ->
                metadata
                |> Option.map (fun value ->
                    let value =
                        origin
                        |> Option.bind (fun (source: ColumnOrigin) ->
                            Storage.tableSnapshot ctx.Store source.Schema source.OriginalTable
                            |> Result.toOption
                            |> Option.map (fun table -> ColumnWire.withIndexFlags table.Indexes source.OriginalName value))
                        |> Option.defaultValue value

                    { value with
                        Origin = origin }))
            origins
            overrides
    else
        overrides

/// Applies `outputColumnWireOverrides` on top of a data-driven wire-type list,
/// keeping the data-driven type wherever there's no override. Falls back
/// wholesale on a length mismatch (both lists come from the same projection
/// expansion, so they shouldn't disagree) rather than throwing from `map2`.
/// The non-grouped wrapper keeps its callers independent of rollup handling.
let private outputColumnWireOverrides ctx columns select =
    outputColumnWireOverridesFor false ctx columns select

let private applyWireOverrides (overrides: ColumnMetadata option list) (types: ColumnMetadata list) : ColumnMetadata list =
    if List.length overrides = List.length types then
        List.map2 (fun ov ty -> defaultArg ov ty) overrides types
    else
        types

let private padNumeric width (text: string) =
    if text.Length >= width then
        text
    elif text.StartsWith("-", System.StringComparison.Ordinal) then
        "-" + text.Substring(1).PadLeft(width - 1, '0')
    else
        text.PadLeft(width, '0')

let private renderOutputValue format value =
    let text =
        match format.Column |> Option.bind _.NumericDisplay, value with
        | Some display, VDouble number ->
            match display.Decimals with
            | Some decimals -> Some(number.ToString("F" + string decimals, System.Globalization.CultureInfo.InvariantCulture))
            | None -> Value.toText value
        | Some _, VDecimal number ->
            match format.Column |> Option.map _.Type with
            | Some(TDecimal(_, scale, _)) -> Some(number.ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture))
            | _ -> Value.toText value
        | _ ->
            match format.Fsp with
            | Some fsp -> Value.toTextFsp fsp value
            | None -> Value.toText value

    match format.Column |> Option.bind _.NumericDisplay, text with
    | Some({ ZeroFill = true } as display), Some rendered ->
        let width =
            match display.Width, format.Column |> Option.map _.Type with
            | Some width, _ -> width
            | None, Some(TDecimal(precision, scale, _)) -> precision + (if scale > 0 then 1 else 0)
            | None, Some(TFloat _) -> 12
            | None, Some(TDouble _) -> 22
            | _ -> rendered.Length

        Some(padNumeric width rendered)
    | _ -> text

let private renderOutputCols (formats: OutputColumnFormat list) (outputCols: (string * Value) list) : string option list =
    if List.length formats = List.length outputCols then
        List.map2 (fun format (_, value) -> renderOutputValue format value) formats outputCols
    else
        outputCols |> List.map (snd >> Value.toText)

let private displayValueForText (ctx: EvalContext) expression value =
    match displayColumnForExpr ctx expression with
    | Some column when column.NumericDisplay |> Option.exists _.ZeroFill ->
        renderOutputValue (outputFormatOfColumn column) value
        |> Option.map VString
        |> Option.defaultValue VNull
    | _ -> value

let private collationOfColumn (ctx: EvalContext) (column: ColumnDef) : Collation.Collation option =
    match column.Type with
    | TChar _ | TVarchar _ | TTinyText | TText | TMediumText | TLongText | TEnum _ | TSet _ | TJson ->
        match column.Charset with
        // `CHARACTER SET binary` compares byte-for-byte; ascii/latin1
        // columns use their charset's default collation, approximated by
        // the server default ai_ci (latin1_swedish_ci/ascii_general_ci
        // fold case and most accents the same way for the common cases).
        | Some "binary" -> Collation.tryFind "utf8mb4_bin"
        | _ ->
            // A real column always carries a baked-in collation; a stringy
            // synthetic derived column falls back to the connection default.
            column.Collation
            |> Option.bind Collation.tryFind
            |> Option.orElseWith (fun () -> Some ctx.Store.ExecutionSettings.ConnectionCollation)
    | _ -> None

/// The collation a comparison involving `expr` resolves under: an
/// explicit `expr COLLATE name` tag wins, then a string-typed column's
/// declared `COLLATE`, then `None` (the caller falls back to the server
/// default — literal-to-literal semantics).
let private resolvedCollation (ctx: EvalContext) (expr: Expr) : Collation.Collation option =
    match expr with
    | Collate(_, name) -> Collation.tryFind name
    | _ -> tryColumnDefForExpr ctx expr |> Option.bind (collationOfColumn ctx)

let private coercibilityOfExpr = function
    | Collate _ -> 0
    | Col _
    | QualifiedCol _ -> 2
    | FuncCall(name, _) ->
        match name.ToUpperInvariant() with
        | "USER"
        | "CURRENT_USER"
        | "SESSION_USER"
        | "SYSTEM_USER"
        | "VERSION"
        | "DATABASE" -> 3
        | _ -> 4
    | Lit(VString _)
    | Lit(VBytes _)
    | Lit(VJson _) -> 4
    | Lit VNull -> 6
    | Lit _ -> 5
    | _ -> 4

/// The collation an equality-classified key resolves under: an explicit
/// `expr COLLATE`, then a string column's own collation, then the
/// connection collation (literals) — the same resolution comparisons use.
let private keyCollation (ctx: EvalContext) (expr: Expr) : Collation.Collation =
    resolvedCollation ctx expr |> Option.defaultValue ctx.Store.ExecutionSettings.ConnectionCollation

let private regexCollation (ctx: EvalContext) (functionName: string) (subjectExpr: Expr) (subject: Value) (patternExpr: Expr) (pattern: Value) : Result<Collation.Collation, EvalError> =
    let subjectRaw = tryRawBytes subject |> Option.isSome
    let patternRaw = tryRawBytes pattern |> Option.isSome
    let subjectCollation =
        resolvedCollation ctx subjectExpr
        |> Option.defaultValue ctx.Store.ExecutionSettings.ConnectionCollation

    let patternCollation =
        resolvedCollation ctx patternExpr
        |> Option.defaultValue ctx.Store.ExecutionSettings.ConnectionCollation

    match subjectRaw, patternRaw with
    | true, true -> Ok(Collation.tryFind "utf8mb4_bin" |> Option.defaultValue Collation.defaultCollation)
    | true, false -> Error(3995, sprintf "Character set 'binary' cannot be used in conjunction with '%s' in call to %s." patternCollation.Name functionName)
    | false, true -> Error(3995, sprintf "Character set '%s' cannot be used in conjunction with 'binary' in call to %s." subjectCollation.Name functionName)
    | false, false ->
        match subjectExpr, patternExpr with
        | Collate(_, left), Collate(_, right) when not (left.Equals(right, System.StringComparison.OrdinalIgnoreCase)) ->
            Error(1267, sprintf "Illegal mix of collations (%s,EXPLICIT) and (%s,EXPLICIT) for operation '%s'" left right functionName)
        | Collate(_, _), _ -> Ok subjectCollation
        | _, Collate(_, _) -> Ok patternCollation
        | _ -> Ok subjectCollation

/// A group/distinct/partition key normalized to collation equality: string
/// values become their collation's canonical key (`KeyOf` is injective per
/// collation), so structural equality of the normalized keys is exactly
/// MySQL's collation equality. Non-string values pass through untouched.
let private collationKeyOf (ctx: EvalContext) (expr: Expr) (value: Value) : Value =
    match value with
    | VString text -> VString((keyCollation ctx expr).KeyOf text)
    | _ -> value

/// Converts a displayed ENUM label into its 1-based declaration ordinal for
/// one ORDER BY key, and tags string-typed columns with the collation their
/// sort must use. The original row/projection value remains a string; only
/// the private sort key changes. Invalid labels cannot normally reach
/// storage, but ordinal 0 mirrors MySQL's sentinel ordering if one does.
let private orderValueForExpr (ctx: EvalContext) (expr: Expr) (value: Value) : Value * Collation.Collation option =
    match tryColumnDefForExpr ctx expr, value with
    | Some { Type = TEnum declared }, VString label ->
        let ordinal =
            declared
            |> List.tryFindIndex (fun item -> System.String.Equals(item, label, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun index -> VInt(int64 (index + 1)))
            |> Option.defaultValue (VInt 0L)
        ordinal, None
    | _ ->
        match value with
        | VString _ -> value, Some(keyCollation ctx expr)
        | _ -> value, None

/// `Star(Some qualifier)` (`t.*`) resolution — same shape as
/// `resolveQualifiedCol`, but hands back every one of that qualifier's own
/// `(name, value)` pairs instead of a single column, so a `JOIN`'s `t.*`
/// expands to just `t`'s own columns rather than every joined table's
/// columns concatenated (which is what `evalProjection`'s unqualified
/// `Star None` case still means).
let rec private resolveStarQualifier (ctx: EvalContext) (qualifier: string) : Result<(string * Value) list, EvalError> =
    match Map.tryFind (qualifier.ToLowerInvariant()) ctx.Qualifiers with
    | Some(cols, offset) ->
        Ok(cols |> List.mapi (fun i column -> column.Name, readColumnValue ctx.Store column ctx.Row.[offset + i]))
    | None ->
        match ctx.Outer with
        | Some parent -> resolveStarQualifier parent qualifier
        | None -> Error(unknownColumn (sprintf "%s.*" qualifier))

/// Splits an `ON`/`WHERE`-style expression into its top-level `AND`
/// conjuncts — `a AND b AND c` flattens to `[a; b; c]`; anything else (an
/// `OR`, a single predicate) is the one-element list `[expr]`. Only
/// conjunction commutes freely enough to split an equi-join's hash keys
/// from its residual filter (`extractEquiKeys` below) — `OR` can't, so a
/// disjunction stays one opaque conjunct and reports no keys.
let rec private conjuncts (expr: Expr) : Expr list =
    let rec loop acc expr =
        match expr with
        | BinOp(And, l, r) -> loop (loop acc l) r
        | _ -> expr :: acc

    List.rev (loop [] expr)

/// Splits a `JOIN ... ON` expression's `AND`-conjuncts into equi-join key
/// pairs — a `QualifiedCol = QualifiedCol` conjunct with one side resolving
/// into the columns already in scope and the other into the just-joined
/// table's own columns, as `(leftIndex, rightIndex)` with `rightIndex`
/// relative to the joined table's own row — and a residual list of
/// everything else (a range predicate, `a.x + 1 = b.y`, a same-side
/// equality like `a.x = a.y`, ...) that still needs per-candidate-pair
/// evaluation. An empty key list means "no usable equi-join key anywhere in
/// this ON clause" — `applyJoin`/`applyMutationJoin` fall back to the
/// nested loop entirely rather than build a `Dictionary` with nothing to
/// key it on.
let private extractEquiKeys
    (resolveQualified: string -> string -> (int * ColumnType) option)
    (leftColumnCount: int)
    (onExpr: Expr)
    : (int * int) list * Expr list =
    let classify (left: Expr) (right: Expr) =
        match left, right with
        | QualifiedCol(lq, lc), QualifiedCol(rq, rc) ->
            match resolveQualified lq lc, resolveQualified rq rc with
            | Some(ia, _), Some(ib, _) when ia < leftColumnCount && ib >= leftColumnCount -> Some(ia, ib - leftColumnCount)
            | Some(ia, _), Some(ib, _) when ib < leftColumnCount && ia >= leftColumnCount -> Some(ib, ia - leftColumnCount)
            | _ -> None
        | _ -> None

    let keys, residual =
        conjuncts onExpr
        |> List.fold
            (fun (keys, residual) conjunct ->
                match conjunct with
                | BinOp(Eq, l, r) ->
                    match classify l r with
                    | Some pair -> pair :: keys, residual
                    | None -> keys, conjunct :: residual
                | _ -> keys, conjunct :: residual)
            ([], [])

    List.rev keys, List.rev residual

/// Column types the hash join trusts to bucket safely: `Value.compare`
/// coerces *any* pair of these numeric types through `toDouble` (so `1 =
/// 1.0` joins), and folds any pair of these text types through the same
/// case/pad-insensitive collation `compareStrings` uses (so `'Alice' =
/// 'alice'` and `'a' = 'a '` join) — the "non-obvious correctness trap":
/// `keyClassOf` below only decides when it's safe to
/// *attempt* a bucket at all; `JoinKeyComparer` still does the real
/// equality check through `Value.compare` itself.
let private isJoinNumericType =
    function
    | TTinyInt _
    | TBool
    | TSmallInt _
    | TMediumInt _
    | TInt _
    | TBigInt _
    | TYear
    | TDecimal _
    | TDouble _
    | TFloat _ -> true
    | _ -> false

let private isJoinTextType =
    function
    | TChar _
    | TVarchar _
    | TTinyText
    | TText
    | TMediumText
    | TLongText
    | TEnum _
    | TSet _ -> true
    | _ -> false

/// Whether an equi-join key's two column types are hash-safe together —
/// `Some true` (numeric bucket) or `Some false` (text bucket) when both
/// sides agree, `None` when they don't (a `DATE`, a `BLOB`/`JSON`, or a
/// numeric-vs-text mismatch whose cross-type coercion `Value.compare`
/// resolves case by case rather than uniformly). `None` sends the whole
/// join to the nested loop instead of a bucket that could disagree with
/// `Value.compare` about which rows tie.
let private keyClassOf (leftType: ColumnType) (rightType: ColumnType) : bool option =
    if isJoinNumericType leftType && isJoinNumericType rightType then Some true
    elif isJoinTextType leftType && isJoinTextType rightType then Some false
    else None

/// Runtime twin of `keyClassOf`: every value actually occupying a hash key
/// column must be `NULL` or the shape its declared class promises. Guards
/// the gap a *declared* type alone can't close — a derived table's every
/// column reports the synthetic `TText` (`deriveColumns`) no matter what
/// `Value` shape its rows really carry — so a mismatch here falls the whole
/// join back to the nested loop instead of building a bucket that silently
/// drops rows it can't classify.
let private valueMatchesKeyClass (isNumeric: bool) (v: Value) : bool =
    match v with
    | VNull -> true
    | VInt _
    | VDouble _
    | VDecimal _ -> isNumeric
    | VString _
    | VBytes _ -> not isNumeric
    | _ -> false

let private rowsMatchKeyClasses (classes: bool list) (keyIndices: int list) (rows: Value[] seq) : bool =
    rows
    |> Seq.forall (fun row -> List.forall2 (fun idx cls -> valueMatchesKeyClass cls row.[idx]) keyIndices classes)

/// One equi-join key (however many `AND`-ed `=` conjuncts contributed a
/// pair) read off one side's row, `None` if any column is `NULL` — SQL's
/// `NULL = anything` is never true, so a `NULL`-keyed row can never join
/// and is simply never added to (or looked up in) the hash bucket, the same
/// way it would silently fail every `Eq` check in the nested loop.
let private equiKeyBy (keyIndices: int[]) (valueAt: int -> Value) : Value[] option =
    let key = keyIndices |> Array.map valueAt
    if key |> Array.contains VNull then None else Some key

let private equiKeyOf (keyIndices: int[]) (row: Value[]) : Value[] option =
    equiKeyBy keyIndices (fun index -> row.[index])

let private readEquiKeyOf (store: Store) (columns: ColumnDef list) (keyIndices: int[]) (row: Value[]) : Value[] option =
    equiKeyBy keyIndices (fun index -> readColumnValue store columns.[index] row.[index])

/// `Dictionary<Value[], _>` key comparer for SQL equality keys. It must
/// agree with expression equality (the resolved collation for strings,
/// `Value.compare`'s numeric coercion otherwise), not .NET's ordinal/exact
/// array equality.
/// `GetHashCode` only needs to *agree* with `Equals` — every value that
/// could tie under a column's collation must land in the same bucket — so
/// it hashes a coarser normalized form (the column collation's folded hash,
/// or a `double`) while `Equals` still does the exact per-column check. The
/// hash join opts into numeric coercion after validating its runtime key
/// classes. GROUP BY and window partitions retain the structural equality
/// of non-string expression results while still honoring string collations.
/// `keyClassOf`/`rowsMatchKeyClasses` keep this comparer from ever seeing a
/// pair `Value.compare` treats non-uniformly depending on which side each
/// value is on (a parseable-as-date string vs. a `DATE`).
type private SqlValueKeyComparer(collations: Collation.Collation list, coerceNumbers: bool) =
    let bucketOf (i: int) (v: Value) : obj =
        match v with
        | VString text -> box ((collations.[i]).HashOf text)
        | VInt _
        | VUInt _
        | VBit _
        | VDouble _
        | VDecimal _ when coerceNumbers -> box (Value.toDouble v)
        | _ -> box (hash v)

    interface IEqualityComparer<Value[]> with
        member _.Equals(a: Value[], b: Value[]) =
            let mutable i = 0
            let mutable equal = a.Length = b.Length

            while equal && i < a.Length do
                equal <-
                    match a.[i], b.[i] with
                    | VString sx, VString sy -> (collations.[i]).Equals sx sy
                    | x, y when coerceNumbers -> Value.compare x y = 0
                    | x, y -> x = y

                i <- i + 1

            equal

        member _.GetHashCode(a: Value[]) =
            match a with
            // Single-column key — the common case — hashes its one bucket
            // without the `Array.mapi`/`Array.fold` the multi-column shape
            // needs. Same hash function, no per-key array churn.
            | [| v |] -> (bucketOf 0 v).GetHashCode()
            | _ -> a |> Array.mapi (fun i v -> (bucketOf i v).GetHashCode()) |> Array.fold (fun h x -> h * 31 + x) 17

/// Like `Storage.traverse`, but over a lazy `seq` rather than a strict
/// `list` — short-circuits on the first `Error` without ever visiting (or
/// allocating a combined row for) later elements — and match-only: `f`
/// returns `None` for an element that doesn't belong in the result (the
/// non-equi `JOIN` fallback's `ON` came back false), and only `Some` is
/// added to the accumulator. That's the difference between "streams pairs
/// instead of materializing" actually being true and merely not true: the
/// non-equi fallback below hands this one `(left row, right row)` tuple per
/// candidate pair, and without the `option` filter here, every one of those
/// tuples (and its `Array.append`-combined row) still piled up in `acc`
/// before the caller's own `List.filter (fun (..., ok) -> ok)` threw most of
/// them away — an `a * b * bool` triple, not the pair itself, is small, but
/// at cross-product scale (a 10k x 50k `ON a.x + 1 = b.y` is 500M candidate
/// pairs) "small per element" was still O(left x right) overall. Filtering
/// inside the fold makes memory O(output) instead.
///
/// The non-equi `JOIN` fallback (`applyJoin`'s/`applyMutationJoin`'s "not
/// hashEligible" branch) is also the one caller with no build-side key to
/// hash on, so it's the case most likely to run long enough for
/// `queryCancellation` to matter (see that doc).
let private traverseSeqWithLimit (limit: (int * 'e) option) (f: 'a -> Result<'b option, 'e>) (xs: 'a seq) : Result<'b list, 'e> =
    let token = queryCancellation.Value
    let acc = ResizeArray()
    let mutable error = None
    let mutable i = 0
    use enumerator = xs.GetEnumerator()

    while error.IsNone && enumerator.MoveNext() do
        match limit with
        | Some(maxItems, tooMany) when i >= maxItems -> error <- Some tooMany
        | _ -> ()

        if error.IsNone && i % cancellationCheckInterval = 0 then
            token.ThrowIfCancellationRequested()

        if error.IsNone then
            i <- i + 1

            match f enumerator.Current with
            | Ok(Some y) -> acc.Add y
            | Ok None -> ()
            | Error e -> error <- Some e

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq acc)

let private traverseSeq (f: 'a -> Result<'b option, 'e>) (xs: 'a seq) : Result<'b list, 'e> =
    traverseSeqWithLimit None f xs

let private maxJoinCandidateRows = 1_000_000

/// Bounded top-`capacity` selection for `ORDER BY ... LIMIT n [OFFSET m]`
/// (`capacity` = `n + m`, already clamped to a sane `int` by the caller):
/// evaluates each item through `f` and keeps only the `capacity` best in a
/// sorted `ResizeArray`, binary-search-inserting and dropping whichever
/// item currently sorts last the moment a new one beats it. `f`'s output is
/// never accumulated anywhere but this buffer, so peak memory is
/// O(capacity), not O(matched rows) — the buffer also starts empty and
/// grows with `Add`/`Insert` rather than preallocating `capacity` slots up
/// front, so a client-supplied `LIMIT` can't size an allocation before a
/// single row has been read. O(rows * log capacity) comparisons instead of
/// a full O(rows * log rows) sort holding every matched row. Still visits
/// every item in `items` (an `ORDER BY` that needs a real sort sees the
/// whole scan in real MySQL too — confirmed against the oracle: a poison
/// row past a `LIMIT`'s cut still raises once `ORDER BY` forces a
/// filesort), so this bounds what gets *kept*, not what gets *evaluated*.
let private boundedTopN (capacity: int) (cmp: 'b -> 'b -> int) (f: 'a -> Result<'b option, 'e>) (items: 'a seq) : Result<'b list, 'e> =
    if capacity <= 0 then
        Ok []
    else
        let token = queryCancellation.Value
        let buf = ResizeArray<'b>()
        let mutable error = None
        let mutable i = 0
        use enumerator = items.GetEnumerator()

        let insertSorted (item: 'b) =
            let mutable lo, hi = 0, buf.Count
            while lo < hi do
                let mid = (lo + hi) / 2
                if cmp buf.[mid] item <= 0 then lo <- mid + 1 else hi <- mid
            buf.Insert(lo, item)

        while error.IsNone && enumerator.MoveNext() do
            if i % cancellationCheckInterval = 0 then
                token.ThrowIfCancellationRequested()

            i <- i + 1

            match f enumerator.Current with
            | Ok(Some item) ->
                if buf.Count < capacity then
                    insertSorted item
                elif cmp item buf.[buf.Count - 1] < 0 then
                    buf.RemoveAt(buf.Count - 1)
                    insertSorted item
            | Ok None -> ()
            | Error e -> error <- Some e

        match error with
        | Some e -> Error e
        | None -> Ok(List.ofSeq buf)

/// The no-`ORDER BY` `SELECT` pipeline's `WHERE`/`DISTINCT`/`LIMIT`/`OFFSET`
/// stage: pulls rows from `xs` one at a time through `f` and stops the
/// enumerator outright the moment `offset + limit` survivors exist, rather
/// than visiting every row the way `traverseSeq` (which every `ORDER BY`
/// path still needs, since sorting requires seeing everything) does. This
/// is the actual LIMIT short-circuit — verified against a real MySQL oracle
/// that a row-level error past a `LIMIT`'s cut, with no `ORDER BY`, never
/// surfaces, because the row is never evaluated in the first place; this
/// mirrors that by never calling `f` on it. `distinct` dedupes on the
/// projected row's text key (`f`'s returned `string list`, the same
/// encoding `SELECT DISTINCT`'s materialized path already keyed on) before
/// counting a row toward `offset`/`limit`, so `DISTINCT ... LIMIT n` still
/// streams instead of falling back to a full materialize.
let private streamLimited
    (distinct: bool)
    (offset: int)
    (limit: int option)
    (f: 'a -> Result<(string option list * 'b) option, 'e>)
    (xs: 'a seq)
    : Result<(string option list * 'b) list, 'e> =
    let token = queryCancellation.Value
    let seen = if distinct then Some(System.Collections.Generic.HashSet<string option list>(HashIdentity.Structural)) else None
    let acc = ResizeArray()
    let mutable skipped = 0
    let mutable error : 'e option = None
    let mutable i = 0
    use enumerator = xs.GetEnumerator()

    let wantMore () = limit |> Option.forall (fun l -> acc.Count < l)

    while error.IsNone && wantMore () && enumerator.MoveNext() do
        if i % cancellationCheckInterval = 0 then
            token.ThrowIfCancellationRequested()

        i <- i + 1

        match f enumerator.Current with
        | Error e -> error <- Some e
        | Ok None -> ()
        | Ok(Some(key, value)) ->
            let isNew = seen |> Option.forall (fun s -> s.Add key)

            if isNew then
                if skipped < offset then skipped <- skipped + 1 else acc.Add(key, value)

    match error with
    | Some e -> Error e
    | None -> Ok(List.ofSeq acc)

/// The hash-join build/probe loop `applyJoin`/`applyMutationJoin` each need
/// on both sides of their own "build on the smaller side" choice: bucket
/// `build` by `buildKeyOf`'s key into a `Dictionary`, then walk `probe` and
/// yield one `(buildIndex, buildItem, probeIndex, probeItem)` per bucket
/// match. Fully generic over what a "build"/"probe" item actually *is* — a
/// plain `Value[]` row in `applyJoin`, an identity-tracking
/// `Value[] option list * Value[]` pair in `applyMutationJoin` — since it
/// only ever touches an item through `buildKeyOf`/`probeKeyOf`, never its
/// shape. One definition instead of the fill-then-probe loop written out
/// per build-side choice (three times across the two callers).
///
/// The build side fills a `Dictionary` eagerly (unavoidable — that's the
/// whole point of a hash join), but the probe side `yield`s matches lazily:
/// nothing past the last pair a caller actually pulls ever runs. Real only
/// when a caller stops pulling early — `applyJoin`'s `INNER`/`CROSS`,
/// no-residual-conjunct case hands this straight through to `runSelect`'s
/// `WHERE`/`LIMIT` streaming instead of collecting it into a list first.
/// Every other caller still drains the whole thing (`LEFT`/`RIGHT` needs
/// every match to know which rows *didn't* match; a residual `ON` conjunct
/// needs every candidate re-checked), so laziness costs those nothing
/// beyond a `seq`'s per-item overhead over a list comprehension's.
let private hashPairs
    (collations: Collation.Collation list)
    (buildKeyOf: 'b -> Value[] option)
    (probeKeyOf: 'p -> Value[] option)
    (build: (int * 'b) list)
    (probe: (int * 'p) seq)
    : (int * 'b * int * 'p) seq =
    let buckets = Dictionary<Value[], ResizeArray<int * 'b>>(SqlValueKeyComparer(collations, true))

    for buildIndex, buildItem in build do
        match buildKeyOf buildItem with
        | Some key ->
            match buckets.TryGetValue key with
            | true, bucket -> bucket.Add(buildIndex, buildItem)
            | false, _ ->
                let bucket = ResizeArray()
                bucket.Add(buildIndex, buildItem)
                buckets.Add(key, bucket)
        | None -> ()

    seq {
        for probeIndex, probeItem in probe do
            match probeKeyOf probeItem with
            | Some key ->
                match buckets.TryGetValue key with
                | true, bucket ->
                    for buildIndex, buildItem in bucket do
                        yield buildIndex, buildItem, probeIndex, probeItem
                | false, _ -> ()
            | None -> ()
    }

/// `hashPairs`' output, narrowed to the candidates whose residual (leftover,
/// non-equi-key) `ON` conjuncts actually hold — the tail every hash-join
/// branch needs after building its candidate list, shared instead of each
/// writing its own `traverse |> Result.mapError |> filter ok |> map`.
/// `extract` picks the piece `residualHolds` evaluates against out of a
/// candidate item that may carry more than just the combined row (see
/// `applyMutationJoin`'s identity-tracking shape).
let private keepMatches
    (residualHolds: 'c -> Result<bool, EvalError>)
    (extract: 'x -> 'c)
    (candidates: (int * int * 'x) list)
    : Result<(int * int * 'x) list, QueryResult> =
    candidates
    |> traverseSeq (fun (li, ri, x) -> residualHolds (extract x) |> Result.map (fun ok -> if ok then Some(li, ri, x) else None))
    |> Result.mapError Err

/// Evaluates one expression against one row. Three-valued logic throughout
/// (comparisons/AND/OR/NOT return `VNull` — SQL's "unknown" — rather than a
/// boolean whenever an operand is `VNull`, per `Value`'s helpers), function
/// calls resolve through `ctx.Registry` (error 1305 if unregistered), and a
/// bare column resolves through `ctx.ColumnIndex` (error 1054 if unknown).
/// MySQL compares an ENUM column against a number by declaration ordinal —
/// `WHERE status = 1`, and `= '1'` too (a quoted number that isn't itself a
/// declared label), match the first declared value, not the text "1".
/// `Some` is that label's 1-based ordinal when `expr` resolves to an
/// ENUM-typed column and `v` is its stored label; `None` lets the caller
/// fall back to the plain comparison.
let private enumOrdinalForColumn (column: ColumnDef option) (value: Value) : Value option =
    match column with
    | Some { Type = TEnum declared } ->
        match value with
        | VString label ->
            declared
            |> List.tryFindIndex (fun item -> System.String.Equals(item, label, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map (fun idx -> VInt(int64 (idx + 1)))
        | _ -> None
    | _ -> None

let private enumOrdinalFor (ctx: EvalContext) (expr: Expr) (value: Value) : Value option =
    enumOrdinalForColumn (tryColumnDefForExpr ctx expr) value

/// An ENUM operand as MySQL's arithmetic and its numeric aggregates see it:
/// the declaration ordinal, typed DOUBLE — `status + 0` comes back over the
/// wire as a DOUBLE, and `SUM(status)` as a DOUBLE rather than SUM's usual
/// exact DECIMAL. Anything that isn't an ENUM column reference passes through.
let private enumNumericOperand (ctx: EvalContext) (expr: Expr) (v: Value) : Value =
    match enumOrdinalFor ctx expr v with
    | Some(VInt ordinal) -> VDouble(float ordinal)
    | _ -> v

/// The numeric side of an ENUM comparison: a real integer, or a
/// fully-numeric string (MySQL reads quoted numbers as indices too).
let private ordinalComparand (v: Value) : Value option =
    match v with
    | VInt _ -> Some v
    | VString s ->
        match System.Int64.TryParse(s.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture) with
        | true, i -> Some(VInt i)
        | false, _ -> None
    | _ -> None

/// Resolves a `Value * Value` comparison the same way `BinOp`'s `=`/`<`/...
/// cases do: an ENUM column compares by ordinal against a numeric operand
/// (either side may be the column), and a string compare folds through the
/// operand's own `COLLATE`/column collation rather than a raw ordinal
/// `Value.compare`. Shared so `Between` (two `AND`ed range checks) gets
/// the same resolution as BinOp/`IN`.
let private comparisonOperands
    (leftColumn: ColumnDef option)
    (left: Value)
    (rightColumn: ColumnDef option)
    (right: Value)
    : Value * Value =
    let pa, pb =
        match enumOrdinalForColumn leftColumn left, ordinalComparand right with
        | Some oa, Some nb -> oa, nb
        | _ ->
            match ordinalComparand left, enumOrdinalForColumn rightColumn right with
            | Some na, Some ob -> na, ob
            | _ -> left, right

    pa, pb

let private resolvedComparisonCollation
    (ctx: EvalContext)
    (expression: Expr)
    (column: ColumnDef option)
    : Collation.Collation option =
    resolvedCollation ctx expression
    |> Option.orElseWith (fun () -> column |> Option.bind (collationOfColumn ctx))

type private CollationOperand =
    { Collation: Collation.Collation
      Coercibility: int
      Charset: string }

let private charsetOfCollation (collation: Collation.Collation) =
    match collation.Name.IndexOf('_') with
    | -1 -> collation.Name.ToLowerInvariant()
    | index -> collation.Name[..index - 1].ToLowerInvariant()

let private isUnicodeCharset charset =
    charset = "utf8mb4" || charset = "utf8mb3" || charset = "utf8"

let private isBinaryCollation (collation: Collation.Collation) =
    collation.Name.EndsWith("_bin", System.StringComparison.OrdinalIgnoreCase)

let private coercibilityName = function
    | 0 -> "EXPLICIT"
    | 1 -> "NONE"
    | 2 -> "IMPLICIT"
    | 3 -> "SYSCONST"
    | 4 -> "COERCIBLE"
    | 5 -> "NUMERIC"
    | _ -> "IGNORABLE"

let private collationOperand
    (ctx: EvalContext)
    (expression: Expr)
    (column: ColumnDef option)
    : CollationOperand =
    let collation =
        resolvedComparisonCollation ctx expression column
        |> Option.defaultValue ctx.Store.ExecutionSettings.ConnectionCollation

    let coercibility =
        match expression, column with
        | Collate _, _ -> 0
        | _, Some column when collationOfColumn ctx column |> Option.isSome -> 2
        | _ -> coercibilityOfExpr expression

    { Collation = collation
      Coercibility = coercibility
      Charset = charsetOfCollation collation }

let private resolveOperandCollation
    (operation: string)
    (left: CollationOperand)
    (right: CollationOperand)
    : Result<Collation.Collation, EvalError> =
    if left.Coercibility < right.Coercibility then
        Ok left.Collation
    elif right.Coercibility < left.Coercibility then
        Ok right.Collation
    elif left.Collation.Name.Equals(right.Collation.Name, System.StringComparison.OrdinalIgnoreCase) then
        Ok left.Collation
    elif left.Coercibility = 0 then
        Error(
            1267,
            sprintf
                "Illegal mix of collations (%s,EXPLICIT) and (%s,EXPLICIT) for operation '%s'"
                left.Collation.Name
                right.Collation.Name
                operation
        )
    elif left.Charset = right.Charset && isBinaryCollation left.Collation then
        Ok left.Collation
    elif left.Charset = right.Charset && isBinaryCollation right.Collation then
        Ok right.Collation
    elif isUnicodeCharset left.Charset <> isUnicodeCharset right.Charset then
        Ok(if isUnicodeCharset left.Charset then left.Collation else right.Collation)
    else
        Error(
            1267,
            sprintf
                "Illegal mix of collations (%s,%s) and (%s,%s) for operation '%s'"
                left.Collation.Name
                (coercibilityName left.Coercibility)
                right.Collation.Name
                (coercibilityName right.Coercibility)
                operation
        )

let private comparisonCollation
    (ctx: EvalContext)
    (operation: string)
    (leftExpr: Expr)
    (leftColumn: ColumnDef option)
    (rightExpr: Expr)
    (rightColumn: ColumnDef option)
    : Result<Collation.Collation, EvalError> =
    resolveOperandCollation
        operation
        (collationOperand ctx leftExpr leftColumn)
        (collationOperand ctx rightExpr rightColumn)

let private comparisonName = function
    | Eq -> "="
    | NullSafeEq -> "<=>"
    | Neq -> "<>"
    | Lt -> "<"
    | Lte -> "<="
    | Gt -> ">"
    | Gte -> ">="
    | _ -> "="

let private resolvedCompareWithColumns
    (ctx: EvalContext)
    (leftExpr: Expr)
    (leftColumn: ColumnDef option)
    (left: Value)
    (rightExpr: Expr)
    (rightColumn: ColumnDef option)
    (right: Value)
    (operation: string)
    : Result<int, EvalError> =
    let pa, pb = comparisonOperands leftColumn left rightColumn right

    match pa, pb with
    | VString sa, VString sb ->
        comparisonCollation ctx operation leftExpr leftColumn rightExpr rightColumn
        |> Result.map (fun collation -> collation.ComparePrimary sa sb)
    | _ -> Ok(Value.compare pa pb)

let private resolvedCompare (ctx: EvalContext) (operation: string) (leftExpr: Expr) (left: Value) (rightExpr: Expr) (right: Value) : Result<int, EvalError> =
    resolvedCompareWithColumns
        ctx
        leftExpr
        (tryColumnDefForExpr ctx leftExpr)
        left
        rightExpr
        (tryColumnDefForExpr ctx rightExpr)
        right
        operation

type private QuantifiedOperand =
    { Expression: Expr
      Column: ColumnDef option }

type private RowOperand =
    | RowScalar of expression: Expr * column: ColumnDef option * value: Value
    | RowValues of RowOperand list

type private RangeOffset =
    | NumericRangeOffset of decimal
    | TemporalRangeOffset of Value

let private comparisonResult
    (ctx: EvalContext)
    (leftExpr: Expr)
    (leftColumn: ColumnDef option)
    (left: Value)
    (rightExpr: Expr)
    (rightColumn: ColumnDef option)
    (op: Op)
    (right: Value)
    : Result<Value, EvalError> =
    match left, right with
    | VNull, _
    | _, VNull -> Ok VNull
    | _ ->
        let comparedLeft, comparedRight = comparisonOperands leftColumn left rightColumn right
        let operation = comparisonName op

        let finish compared equal =
            match op with
            | Eq
            | NullSafeEq -> boolToValue equal
            | Neq -> boolToValue (not equal)
            | Lt -> boolToValue (compared < 0)
            | Lte -> boolToValue (compared <= 0)
            | Gt -> boolToValue (compared > 0)
            | Gte -> boolToValue (compared >= 0)
            | _ -> VNull

        match comparedLeft, comparedRight with
        | VString leftText, VString rightText ->
            comparisonCollation ctx operation leftExpr leftColumn rightExpr rightColumn
            |> Result.map (fun collation ->
                finish
                    (collation.ComparePrimary leftText rightText)
                    (collation.Equals leftText rightText))
        | _ ->
            let compared = Value.compare comparedLeft comparedRight
            Ok(finish compared (compared = 0))

let private quantifiedComparisonResult
    (ctx: EvalContext)
    (leftExpr: Expr)
    (left: Value)
    (rightOperand: QuantifiedOperand)
    (op: Op)
    (right: Value)
    : Result<Value, EvalError> =
    comparisonResult
        ctx
        leftExpr
        (tryColumnDefForExpr ctx leftExpr)
        left
        rightOperand.Expression
        rightOperand.Column
        op
        right

let private quantifiedEqualityMembershipResult
    (ctx: EvalContext)
    (leftExpression: Expr)
    (leftValue: Value)
    (rightOperand: QuantifiedOperand)
    (subquery: ExpressionSubqueryResult)
    (op: Op)
    (quantifier: Quantifier)
    : Value option =
    match op, subquery.Rows, leftValue with
    | (Eq | Neq), [], _ -> Some(if quantifier = Any then VInt 0L else VInt 1L)
    | (Eq | Neq), _, VNull -> Some VNull
    | (Eq | Neq), _, _ ->
        subquery.EqualityMembership
        |> Option.bind (fun membership ->
            let key =
                match membership.Domain with
                | TextMembership expected ->
                    comparisonCollation
                        ctx
                        "="
                        leftExpression
                        (tryColumnDefForExpr ctx leftExpression)
                        rightOperand.Expression
                        rightOperand.Column
                    |> Result.toOption
                    |> Option.filter (fun actual -> actual.Name = expected.Name)
                    |> Option.bind (fun _ -> equalityMembershipKey membership.Domain leftValue)
                | _ -> equalityMembershipKey membership.Domain leftValue

            key
            |> Option.map (fun key ->
                let containsEqual = membership.Values.Contains key
                let containsDifferent = membership.Values |> Set.exists ((<>) key)

                match op, quantifier with
                | Eq, Any ->
                    if containsEqual then VInt 1L elif membership.ContainsNull then VNull else VInt 0L
                | Eq, All ->
                    if containsDifferent then VInt 0L elif membership.ContainsNull then VNull else VInt 1L
                | Neq, Any ->
                    if containsDifferent then VInt 1L elif membership.ContainsNull then VNull else VInt 0L
                | Neq, All ->
                    if containsEqual then VInt 0L elif membership.ContainsNull then VNull else VInt 1L
                | _ -> VNull))
    | _ -> None

let private sourceHasQualifier (qualifier: string) = function
    | FromTable table ->
        table.Table.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)
        || (table.Alias |> Option.exists (fun alias -> alias.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)))
    | FromSubquery(_, alias)
    | FromLateral(_, alias)
    | FromJsonTable(_, _, _, alias) -> alias.Equals(qualifier, System.StringComparison.OrdinalIgnoreCase)

/// MySQL promotes a binary UNION branch for equality and ordering. Other
/// collation combinations retain the first branch's ordering in this engine.
let private strictestUnionCollation (left: Collation.Collation) (right: Collation.Collation) =
    if left.Name = "utf8mb4_bin" || right.Name = "utf8mb4_bin" then
        Collation.tryFind "utf8mb4_bin" |> Option.defaultValue left
    else
        left

let rec private selectSourceColumns (store: Store) (dbName: string) = function
    | FromTable table ->
        let database = table.Database |> Option.defaultValue dbName

        match
            if table.Database.IsNone then
                currentCteScope () |> Map.tryFind (table.Table.ToLowerInvariant()) |> Option.map fst
            else
                None
        with
        | Some columns -> columns |> List.map Some
        | None ->
            match tryStoredView store database table.Table with
            | Some view ->
                match Parser.parse view.Definition with
                | Ok(Select viewSelect) ->
                    let columns = selectProjectionColumns store view.Schema viewSelect

                    if view.Columns.IsEmpty || view.Columns.Length <> columns.Length then
                        columns
                    else
                        List.map2 (fun name column -> column |> Option.map (fun value -> { value with Name = name })) view.Columns columns
                | _ -> []
            | None ->
                scan store database table.Table
                |> Result.toOption
                |> Option.map fst
                |> Option.defaultValue []
                |> List.map Some
    | FromSubquery(PlainSelect body, _)
    | FromLateral(PlainSelect body, _) -> selectProjectionColumns store dbName body
    | FromSubquery(UnionSelect(first, rest, _, _, _), _)
    | FromLateral(UnionSelect(first, rest, _, _, _), _) ->
        let branches = first :: (rest |> List.map snd)
        let columns = branches |> List.map (selectProjectionColumns store dbName)

        if columns |> List.forall (fun branch -> branch.Length = columns.Head.Length) then
            columns
            |> List.transpose
            |> List.map (fun candidates ->
                let present = candidates |> List.choose id

                match present with
                | [] -> None
                | first :: rest ->
                    let collation =
                        (first :: rest)
                        |> List.choose _.Collation
                        |> List.choose Collation.tryFind
                        |> function
                            | [] -> None
                            | first :: rest -> Some((rest |> List.fold strictestUnionCollation first).Name)

                    Some { first with Collation = collation })
        else
            []
    | FromJsonTable _ -> []

and private selectProjectionColumns (store: Store) (dbName: string) (select: SelectStmt) : ColumnDef option list =

    let sources =
        (select.From |> Option.toList)
        @ (select.Joins |> List.map _.Table)
        |> List.map (fun source -> source, selectSourceColumns store dbName source)

    let columnFor (name: string) (candidates: (FromItem * ColumnDef option list) list) =
        candidates
        |> List.collect snd
        |> List.choose id
        |> List.filter (fun column -> column.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))
        |> function
            | [ column ] -> Some column
            | _ -> None

    let rec projectionColumns =
        function
        | Star None -> sources |> List.collect snd
        | Star(Some qualifier) -> sources |> List.filter (fst >> sourceHasQualifier qualifier) |> List.collect snd
        | Col name -> [ columnFor name sources ]
        | QualifiedCol(qualifier, name) -> sources |> List.filter (fst >> sourceHasQualifier qualifier) |> columnFor name |> List.singleton
        | Collate(value, collation) ->
            projectionColumns value
            |> List.map (Option.map (fun column -> { column with Collation = Some collation; Charset = None }))
        | _ -> [ None ]

    select.Projections
    |> List.collect (fun (expression, alias) ->
        projectionColumns expression
        |> List.map (fun column ->
            match column, alias with
            | Some value, Some name -> Some { value with Name = name }
            | _ -> column))

let private subqueryProjectionOperand (ctx: EvalContext) (select: SelectStmt) : QuantifiedOperand =
    match select.Projections, selectProjectionColumns ctx.Store ctx.DbName select with
    | [ (Collate(_, collation), _) ], [ column ] ->
        { Expression = Collate(Lit VNull, collation)
          Column = column }
    | [ _ ], [ column ] ->
        { Expression = Lit VNull
          Column = column }
    | _ ->
        { Expression = Lit VNull
          Column = None }

let private subqueryRowOperand (ctx: EvalContext) (select: SelectStmt) (values: Value[]) : RowOperand =
    let columns = selectProjectionColumns ctx.Store ctx.DbName select

    let expressions =
        match select.Projections, columns with
        | [ (Star _, _) ], columns -> List.replicate columns.Length (Lit VNull)
        | projections, columns when projections.Length = columns.Length ->
            projections
            |> List.map fst
            |> List.map (function
                | Collate(_, collation) -> Collate(Lit VNull, collation)
                | _ -> Lit VNull)
        | _ -> List.replicate values.Length (Lit VNull)

    values
    |> Array.toList
    |> List.mapi (fun index value ->
        let expression = expressions |> List.tryItem index |> Option.defaultValue (Lit VNull)
        let column = columns |> List.tryItem index |> Option.defaultValue None
        RowScalar(expression, column, value))
    |> RowValues

let private rowComparisonResult
    (ctx: EvalContext)
    (op: Op)
    (left: RowOperand)
    (right: RowOperand)
    : Result<Value, EvalError> =
    let width = function
        | RowScalar _ -> 1
        | RowValues values -> values.Length

    let scalarComparison left right comparisonOp =
        match left, right with
        | RowScalar(leftExpr, leftColumn, leftValue), RowScalar(rightExpr, rightColumn, rightValue) ->
            match comparisonOp, leftValue, rightValue with
            | NullSafeEq, VNull, VNull -> Ok(VInt 1L)
            | NullSafeEq, VNull, _
            | NullSafeEq, _, VNull -> Ok(VInt 0L)
            | NullSafeEq, _, _ -> comparisonResult ctx leftExpr leftColumn leftValue rightExpr rightColumn NullSafeEq rightValue
            | _ -> comparisonResult ctx leftExpr leftColumn leftValue rightExpr rightColumn comparisonOp rightValue
        | _ -> Error(1241, sprintf "Operand should contain %d column(s)" (width left))

    let rec compareRows comparisonOp left right =
        match left, right with
        | RowScalar _, RowScalar _ -> scalarComparison left right comparisonOp
        | RowValues leftValues, RowValues rightValues when leftValues.Length = rightValues.Length ->
            let pairs = List.zip leftValues rightValues

            let rec equal sawNull pairs =
                match pairs with
                | [] -> Ok(if sawNull then VNull else VInt 1L)
                | (leftItem, rightItem) :: rest ->
                    match compareRows Eq leftItem rightItem with
                    | Ok(VInt 0L) -> Ok(VInt 0L)
                    | Ok VNull -> equal true rest
                    | Ok _ -> equal sawNull rest
                    | Error error -> Error error

            let rec ordered pairs =
                match pairs with
                | [] -> Ok(VInt(if comparisonOp = Lte || comparisonOp = Gte then 1L else 0L))
                | (leftItem, rightItem) :: rest ->
                    match compareRows Eq leftItem rightItem with
                    | Ok(VInt 1L) -> ordered rest
                    | Ok VNull -> Ok VNull
                    | Ok _ -> compareRows comparisonOp leftItem rightItem
                    | Error error -> Error error

            let rec nullSafe pairs =
                match pairs with
                | [] -> Ok(VInt 1L)
                | (leftItem, rightItem) :: rest ->
                    match compareRows NullSafeEq leftItem rightItem with
                    | Ok(VInt 1L) -> nullSafe rest
                    | Ok _ -> Ok(VInt 0L)
                    | Error error -> Error error

            match comparisonOp with
            | Eq -> equal false pairs
            | Neq ->
                equal false pairs
                |> Result.map (function
                    | VInt 1L -> VInt 0L
                    | VInt 0L -> VInt 1L
                    | _ -> VNull)
            | Lt
            | Lte
            | Gt
            | Gte -> ordered pairs
            | NullSafeEq -> nullSafe pairs
            | _ -> Error(1241, "Operand should contain 1 column(s)")
        | _ -> Error(1241, sprintf "Operand should contain %d column(s)" (width left))

    compareRows op left right

type private SubqueryScope =
    { Qualifiers: Set<string>
      Columns: Set<string> }

let private emptySubqueryScope =
    { Qualifiers = Set.empty
      Columns = Set.empty }

let private statementVariantFunctions =
    set
        [ "BENCHMARK"; "CONNECTION_ID"; "CURDATE"; "CURRENT_DATE"; "CURRENT_TIME"; "CURRENT_TIMESTAMP"
          "CURRENT_USER"; "CURTIME"; "DATABASE"; "FOUND_ROWS"; "LAST_INSERT_ID"; "LOCALTIME"
          "LOCALTIMESTAMP"; "NOW"; "RAND"; "RANDOM_BYTES"; "ROW_COUNT"; "SLEEP"; "SYSDATE"
          "UNIX_TIMESTAMP"; "USER"; "UUID"; "UUID_SHORT"; "VERSION" ]

let private tableSubqueryScope (store: Store) (dbName: string) (table: TableRef) : SubqueryScope option =
    let tableDb = table.Database |> Option.defaultValue dbName
    let qualifier = table.Alias |> Option.defaultValue table.Table |> fun name -> name.ToLowerInvariant()
    let scope (columns: ColumnDef list) =
        { Qualifiers = Set.singleton qualifier
          Columns = columns |> List.map (_.Name >> fun name -> name.ToLowerInvariant()) |> Set.ofList }

    let cteShadowsTable =
        table.Database.IsNone
        && currentCteScope () |> Map.containsKey (table.Table.ToLowerInvariant())

    if cteShadowsTable then
        None
    elif table.Database.IsNone && table.Table.Equals("dual", System.StringComparison.OrdinalIgnoreCase) then
        Some(scope [])
    elif tableDb.Equals("information_schema", System.StringComparison.OrdinalIgnoreCase) then
        InformationSchema.scan store.Catalog table.Table None |> Option.map (fst >> scope)
    else
        match scan store tableDb table.Table with
        | Ok(columns, _) -> Some(scope columns)
        | Error _ -> None

let private selectSubqueryScope (store: Store) (dbName: string) (select: SelectStmt) : SubqueryScope option =
    let sources = (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)

    if not select.Ctes.IsEmpty || sources |> List.exists (function FromTable _ -> false | _ -> true) then
        None
    else
        sources
        |> List.fold
            (fun scope source ->
                scope
                |> Option.bind (fun scope ->
                    match source with
                    | FromTable table ->
                        tableSubqueryScope store dbName table
                        |> Option.map (fun next ->
                            { Qualifiers = Set.union scope.Qualifiers next.Qualifiers
                              Columns = Set.union scope.Columns next.Columns })
                    | _ -> None))
            (Some emptySubqueryScope)

let private isStatementStableFunction (registry: Registry) (name: string) : bool =
    let key = name.ToUpperInvariant()

    not (statementVariantFunctions.Contains key)
    && (match Map.tryFind key registry.Extensions with
        | Some extension -> extension.Deterministic
        | None -> true)

let rec private isStatementStableExpr (store: Store) (registry: Registry) (dbName: string) (scope: SubqueryScope) (expression: Expr) : bool =
    let every expressions = expressions |> List.forall (isStatementStableExpr store registry dbName scope)

    match expression with
    | Lit _
    | Star None -> true
    | Star(Some qualifier) -> scope.Qualifiers.Contains(qualifier.ToLowerInvariant())
    | Col name -> scope.Columns.Contains(name.ToLowerInvariant())
    | QualifiedCol(table, _) -> scope.Qualifiers.Contains(table.ToLowerInvariant())
    | Placeholder _
    | UserVariable _
    | SystemVariable _
    | AssignUserVariable _
    | MatchAgainst _
    | WindowOver _ -> false
    | FuncCall(name, arguments) -> isStatementStableFunction registry name && every arguments
    | Row values -> every values
    | Exists select
    | Subquery select -> isStatementStableSelect store registry dbName scope select
    | InSubquery(value, select) ->
        isStatementStableExpr store registry dbName scope value
        && isStatementStableSelect store registry dbName scope select
    | QuantifiedComparison(value, _, _, select) ->
        isStatementStableExpr store registry dbName scope value
        && isStatementStableSelect store registry dbName scope select
    | BinOp(_, left, right) -> every [ left; right ]
    | Not value
    | IsNull value
    | IsNotNull value
    | IsTrue value
    | IsFalse value
    | Distinct value
    | OrderBy(value, _)
    | Cast(value, _)
    | Collate(value, _) -> isStatementStableExpr store registry dbName scope value
    | Like(value, pattern, _, _)
    | Regexp(value, pattern) -> every [ value; pattern ]
    | In(value, candidates) -> every (value :: candidates)
    | Between(value, lower, upper) -> every [ value; lower; upper ]
    | Case(subject, branches, otherwise) ->
        every (Option.toList subject @ (branches |> List.collect (fun (condition, result) -> [ condition; result ])) @ Option.toList otherwise)

and private isStatementStableSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outerScope: SubqueryScope)
    (select: SelectStmt)
    : bool =
    match selectSubqueryScope store dbName select with
    | None -> false
    | Some ownScope when not select.Windows.IsEmpty -> false
    | Some ownScope ->
        let scope =
            { Qualifiers = Set.union outerScope.Qualifiers ownScope.Qualifiers
              Columns = Set.union outerScope.Columns ownScope.Columns }

        let expressions =
            (select.Projections |> List.map fst)
            @ (select.Joins |> List.map _.On)
            @ (select.Where |> Option.toList)
            @ select.GroupBy
            @ (select.Having |> Option.toList)
            @ (select.OrderBy |> List.map fst)
            @ (select.Limit |> Option.toList)
            @ (select.Offset |> Option.toList)

        expressions |> List.forall (isStatementStableExpr store registry dbName scope)

let rec private evalExpr (ctx: EvalContext) (expr: Expr) : Result<Value, EvalError> =
    let eval = evalExpr ctx

    match expr with
    | Lit(VZeroDate date) when
        let year, month, day = Temporal.zeroDateParts date
        (year = 0 && month = 0 && day = 0 && ctx.Store.ExecutionSettings.SqlMode.NoZeroDate)
        || ((year <> 0 || month <> 0 || day <> 0) && ctx.Store.ExecutionSettings.SqlMode.NoZeroInDate) ->
        Error(1525, sprintf "Incorrect DATE value: '%s'" (Temporal.formatZeroDate date))
    | Lit(VZeroDateTime dateTime) when
        let date, _, _, _, _ = Temporal.zeroDateTimeParts dateTime
        let year, month, day = Temporal.zeroDateParts date
        (year = 0 && month = 0 && day = 0 && ctx.Store.ExecutionSettings.SqlMode.NoZeroDate)
        || ((year <> 0 || month <> 0 || day <> 0) && ctx.Store.ExecutionSettings.SqlMode.NoZeroInDate) ->
        Error(1525, sprintf "Incorrect DATETIME value: '%s'" (Temporal.formatZeroDateTime dateTime))
    | Lit v -> Ok v
    | Row _ -> Error(1241, "Operand should contain 1 column(s)")
    // MATCH reaches scalar evaluation only when its statement shape has no
    // physical FULLTEXT source for the score pre-pass.
    | MatchAgainst _ -> Error(1191, "Can't find FULLTEXT index matching the column list")
    | Placeholder _ -> Error(1064, "unbound prepared-statement placeholder")
    | Star _ -> Error(1054, "Invalid use of '*'")
    // Only reachable if a `RowNumberOver`/`LagOver` ever escapes
    // `runWindowedSelect`'s rewrite (which substitutes every occurrence,
    // wherever it's nested, for a plain `Col` reference before any of this
    // runs) — real MySQL itself rejects a window function outside a
    // `SELECT`'s own projection/`ORDER BY` list the same way.
    | WindowOver _ -> Error(1054, "Invalid use of a group function")
    | Col name -> resolveCol ctx name
    | QualifiedCol(table, col) -> resolveQualifiedCol ctx table col
    | UserVariable variable when variable.Sql = "@" -> Ok VNull
    | UserVariable variable ->
        match UserVariableRef.validationError variable with
        | Some message -> Error(3061, message)
        | None ->
            currentVariableContext ()
            |> Option.bind (fun bindings -> bindings.UserVariables.Value |> Map.tryFind variable.Name)
            |> Option.defaultValue VNull
            |> Ok
    | SystemVariable(scope, variable) ->
        match currentVariableContext () with
        | Some bindings -> bindings.ReadSystemVariable (scope |> Option.defaultValue "") variable |> Result.map (Option.defaultValue VNull)
        | None -> Error(1193, sprintf "Unknown system variable '%s'" variable)
    | AssignUserVariable(variable, value) ->
        match UserVariableRef.validationError variable with
        | Some message -> Error(3061, message)
        | None ->
            eval value
            |> Result.bind (fun evaluated ->
                match currentVariableContext () with
                | None -> Error(1105, "User-defined variables require a session")
                | Some _ when suppressVariableAssignments.Value -> Ok evaluated
                | Some bindings ->
                    let name = variable.Name

                    if Map.containsKey name bindings.UserVariables.Value || bindings.UserVariables.Value.Count < bindings.MaxUserVariables then
                        bindings.UserVariables.Value <- Map.add name evaluated bindings.UserVariables.Value
                        Ok evaluated
                    else
                        Error(1105, "Too many user-defined variables"))
    | Not e -> eval e |> Result.map (fun v -> truthy v |> Option.map (not >> boolToValue) |> Option.defaultValue VNull)
    | IsNull e -> eval e |> Result.map (function VNull -> VInt 1L | _ -> VInt 0L)
    | IsNotNull e -> eval e |> Result.map (function VNull -> VInt 0L | _ -> VInt 1L)
    | IsTrue e -> eval e |> Result.map (fun v -> boolToValue (truthy v = Some true))
    | IsFalse e -> eval e |> Result.map (fun v -> boolToValue (truthy v = Some false))
    | BinOp((Eq | Neq | Lt | Lte | Gt | Gte | NullSafeEq as op), (Row _ as a), b)
    | BinOp((Eq | Neq | Lt | Lte | Gt | Gte | NullSafeEq as op), a, (Row _ as b)) ->
        evalRowOperand ctx a
        |> Result.bind (fun left -> evalRowOperand ctx b |> Result.bind (rowComparisonResult ctx op left))
    | BinOp(op, a, b) ->
        // Arithmetic can leave the `BIGINT UNSIGNED` domain, which MySQL
        // refuses with 1690 rather than answering in a wider type. That
        // refusal arrives as an exception (`Value.narrowUnsigned` — the
        // arithmetic has no error channel of its own) and becomes an
        // ordinary `EvalError` here, at the first frame that has one.
        try
            // And/Or already evaluate both operands (no short-circuit, since
            // SQL's three-valued logic needs both sides to tell "false" apart
            // from "unknown"), so every `BinOp` collapses into one total match
            // on `op` here — all 12 `Ast.Op` cases handled in the one place,
            // rather than two more `failwith`-guarded helpers each only
            // partially matching the same type.
            eval a
            |> Result.bind (fun va ->
                eval b
                |> Result.bind (fun vb ->
                    let compareWith comparison =
                        comparisonResult
                            ctx
                            a
                            (tryColumnDefForExpr ctx a)
                            va
                            b
                            (tryColumnDefForExpr ctx b)
                            comparison
                            vb

                    // An ENUM is a number in numeric context: `status + 0` is
                    // the declaration ordinal, not 0 from a non-numeric label.
                    let arith (f: Value -> Value -> Value) =
                        f (enumNumericOperand ctx a va) (enumNumericOperand ctx b vb)

                    let divide (f: Value -> Value -> Value) =
                        let left = enumNumericOperand ctx a va
                        let right = enumNumericOperand ctx b vb

                        match left, right with
                        | VNull, _
                        | _, VNull -> Ok VNull
                        | _, divisor when Value.isArithmeticZero divisor ->
                            Diagnostics.divisionByZero () |> Result.map (fun () -> VNull)
                        | _ -> Ok(f left right)

                    match op with
                    | And ->
                        match truthy va, truthy vb with
                        | Some false, _
                        | _, Some false -> Ok(VInt 0L)
                        | Some true, Some true -> Ok(VInt 1L)
                        | _ -> Ok VNull
                    | Or ->
                        match truthy va, truthy vb with
                        | Some true, _
                        | _, Some true -> Ok(VInt 1L)
                        | Some false, Some false -> Ok(VInt 0L)
                        | _ -> Ok VNull
                    // XOR has no short-circuit: either operand being unknown
                    // makes the answer unknown (`NULL XOR 1` is NULL, unlike
                    // `NULL OR 1`).
                    | Xor ->
                        match truthy va, truthy vb with
                        | Some a, Some b -> Ok(boolToValue (a <> b))
                        | _ -> Ok VNull
                    // `datetime_expr +/- INTERVAL n unit` parses to a plain
                    // `BinOp`, same as `1 + 2` — `vb` here is `INTERVAL`'s own
                    // encoded marker value (see `Functions.intervalFn`), so it
                    // needs the same real date arithmetic `DATE_ADD`/`DATE_SUB`
                    // give it rather than falling into generic numeric add/sub.
                    | Add when isIntervalValue vb -> tryDateIntervalBinOp 1.0 va vb |> Option.defaultValue (Value.add va vb) |> Ok
                    | Sub when isIntervalValue vb -> tryDateIntervalBinOp -1.0 va vb |> Option.defaultValue (Value.sub va vb) |> Ok
                    | SignedSub when isIntervalValue vb -> tryDateIntervalBinOp -1.0 va vb |> Option.defaultValue (Value.subSigned va vb) |> Ok
                    | Add -> Ok(arith Value.add)
                    | Sub -> Ok(arith Value.sub)
                    | SignedSub -> Ok(arith Value.subSigned)
                    | Mul -> Ok(arith Value.mul)
                    | Div -> divide Value.div
                    | IntDiv -> divide Value.intDiv
                    | Eq -> compareWith Eq
                    | Neq -> compareWith Neq
                    | Lt -> compareWith Lt
                    | Lte -> compareWith Lte
                    | Gt -> compareWith Gt
                    | Gte -> compareWith Gte
                    // Never unknown, unlike every other comparison here: both
                    // sides `NULL` is true, either side (but not both) `NULL` is
                    // false, otherwise it uses the same equality resolver.
                    | NullSafeEq ->
                        match va, vb with
                        | VNull, VNull -> Ok(VInt 1L)
                        | VNull, _
                        | _, VNull -> Ok(VInt 0L)
                        | _ -> compareWith NullSafeEq))
        with Value.UnsignedOutOfRange ->
            let expression = InformationSchema.exprToSql (BinOp(op, a, b))
            Error(1690, sprintf "BIGINT UNSIGNED value is out of range in '%s'" expression)
    | Like(e, p, caseSensitive, escape) ->
        eval e
        |> Result.bind (fun ve ->
            eval p
            |> Result.bind (fun vp ->
                let ve = displayValueForText ctx e ve
                let vp = displayValueForText ctx p vp

                match tryRawBytes ve, tryRawBytes vp with
                | Some _, _
                | _, Some _ -> Ok(Collation.tryFind "utf8mb4_bin" |> Option.defaultValue Collation.defaultCollation)
                | _ ->
                    comparisonCollation
                        ctx
                        "like"
                        e
                        (tryColumnDefForExpr ctx e)
                        p
                        (tryColumnDefForExpr ctx p)
                |> Result.map (fun collation -> likeOp (Some collation) caseSensitive escape ve vp)))
    | Regexp(e, p) ->
        eval e
        |> Result.bind (fun ve ->
            eval p
            |> Result.bind (fun vp ->
                let ve = displayValueForText ctx e ve
                let vp = displayValueForText ctx p vp
                regexCollation ctx "regexp_like" e ve p vp
                |> Result.bind (fun collation -> regexpOp (Some collation) ve vp)))
    | In((Row _ as e), xs)
    | In(e, ((Row _) :: _ as xs)) ->
        evalRowOperand ctx e
        |> Result.bind (fun value ->
            xs
            |> traverse (evalRowOperand ctx)
            |> Result.bind (fun candidates ->
                candidates
                |> traverse (rowComparisonResult ctx Eq value)
                |> Result.map (fun results ->
                    if results |> List.exists ((=) (VInt 1L)) then
                        VInt 1L
                    elif results |> List.exists ((=) VNull) then
                        VNull
                    else
                        VInt 0L)))
    | In(e, xs) ->
        eval e
        |> Result.bind (fun ve ->
            match ve with
            | VNull -> Ok VNull
            | _ ->
                xs
                |> traverse eval
                |> Result.bind (fun values ->
                    List.zip xs values
                    |> traverse (fun (candidateExpr, candidate) ->
                        comparisonResult
                            ctx
                            e
                            (tryColumnDefForExpr ctx e)
                            ve
                            candidateExpr
                            (tryColumnDefForExpr ctx candidateExpr)
                            Eq
                            candidate)
                    |> Result.map (fun comparisons ->
                        if comparisons |> List.exists ((=) (VInt 1L)) then VInt 1L
                        elif comparisons |> List.exists ((=) VNull) then VNull
                        else VInt 0L)))
    | InSubquery((Row _ as e), select) ->
        evalRowOperand ctx e
        |> Result.bind (fun value ->
            let subquery = runExpressionSubquery ctx select select

            match subquery.Result with
            | Err(code, message) -> Error(code, message)
            | Affected _ -> Ok VNull
            | MultipleResults _ -> Error nestedSubqueryResultsError
            | ResultSet(_, _) ->
                let membershipKey =
                    match subquery.RowEqualityMembership, value with
                    | Some membership, RowValues leftValues ->
                        let right = subqueryRowOperand ctx select (Array.create leftValues.Length VNull)

                        match right with
                        | RowValues rightValues when leftValues.Length = membership.Domains.Length && rightValues.Length = leftValues.Length ->
                            let rec keys found domains left right =
                                match domains, left, right with
                                | [], [], [] -> Some(List.rev found)
                                | domain :: restDomains,
                                  RowScalar(leftExpr, leftColumn, leftValue) :: restLeft,
                                  RowScalar(rightExpr, rightColumn, _) :: restRight ->
                                    let key =
                                        match domain with
                                        | TextMembership expected ->
                                            comparisonCollation ctx "=" leftExpr leftColumn rightExpr rightColumn
                                            |> Result.toOption
                                            |> Option.filter (fun actual -> actual.Name = expected.Name)
                                            |> Option.bind (fun _ -> equalityMembershipKey domain leftValue)
                                        | _ -> equalityMembershipKey domain leftValue

                                    key
                                    |> Option.bind (fun value -> keys (value :: found) restDomains restLeft restRight)
                                | _ -> None

                            keys [] membership.Domains leftValues rightValues
                        | _ -> None
                    | _ -> None

                match membershipKey, subquery.RowEqualityMembership with
                | Some key, Some membership when membership.Values.Contains key -> Ok(VInt 1L)
                | Some _, Some membership when not membership.ContainsNullableRows -> Ok(VInt 0L)
                | _ ->
                    subquery.Rows
                    |> traverse (fun row ->
                        subqueryRowOperand ctx select row
                        |> rowComparisonResult ctx Eq value)
                    |> Result.map (fun results ->
                        if results |> List.exists ((=) (VInt 1L)) then
                            VInt 1L
                        elif results |> List.exists ((=) VNull) then
                            VNull
                        else
                            VInt 0L))
    | InSubquery(e, select) ->
        // `ve = NULL` can't short-circuit before running the subquery the
        // way the literal-list `In` case above does: `NULL IN (<empty
        // set>)` is FALSE (an OR over zero disjuncts), not UNKNOWN — only a
        // *non-empty* candidate set makes it UNKNOWN — and emptiness is
        // exactly what the subquery has to run to find out. `NOT IN` over
        // an empty subquery (a common "no matching rows yet" shape) would
        // otherwise wrongly drop every row whose `e` is `NULL`.
        eval e
        |> Result.bind (fun ve ->
            let subquery = runExpressionSubquery ctx select select

            match subquery.Result with
            | Err(code, message) -> Error(code, message)
            | Affected _ -> Ok VNull
            | MultipleResults _ -> Error nestedSubqueryResultsError
            | ResultSet(columns, _) when columns.Length <> 1 -> Error(1241, "Operand should contain 1 column(s)")
            | ResultSet(_, _) ->
                let rightOperand = subqueryProjectionOperand ctx select

                match quantifiedEqualityMembershipResult ctx e ve rightOperand subquery Eq Any with
                | Some result -> Ok result
                | None ->
                    subquery.Rows
                    |> List.map (Array.tryHead >> Option.defaultValue VNull)
                    |> traverse (quantifiedComparisonResult ctx e ve rightOperand Eq)
                    |> Result.map (fun comparisons ->
                        if comparisons |> List.exists ((=) (VInt 1L)) then VInt 1L
                        elif comparisons |> List.exists ((=) VNull) then VNull
                        else VInt 0L))
    | QuantifiedComparison(e, op, quantifier, select) ->
        eval e
        |> Result.bind (fun left ->
            let subquery = runExpressionSubquery ctx select select

            match subquery.Result with
            | Err(code, message) -> Error(code, message)
            | Affected _ -> Ok VNull
            | MultipleResults _ -> Error nestedSubqueryResultsError
            | ResultSet(columns, _) when columns.Length <> 1 -> Error(1241, "Operand should contain 1 column(s)")
            | ResultSet(_, _) ->
                let rightOperand = subqueryProjectionOperand ctx select

                match quantifiedEqualityMembershipResult ctx e left rightOperand subquery op quantifier with
                | Some result -> Ok result
                | None ->
                    subquery.Rows
                    |> traverse (fun row ->
                        let right = row |> Array.tryHead |> Option.defaultValue VNull
                        quantifiedComparisonResult ctx e left rightOperand op right)
                    |> Result.map (fun values ->
                        match quantifier with
                        | Any ->
                            if values |> List.exists (fun value -> value = VInt 1L) then VInt 1L
                            elif values |> List.exists (fun value -> value = VNull) then VNull
                            else VInt 0L
                        | All ->
                            if values |> List.exists (fun value -> value = VInt 0L) then VInt 0L
                            elif values |> List.exists (fun value -> value = VNull) then VNull
                            else VInt 1L))
    | Distinct e
    | OrderBy(e, _) -> eval e
    | Case(subject, whens, elseBranch) ->
        let fallback () =
            match elseBranch with
            | Some e -> eval e
            | None -> Ok VNull

        match subject with
        | Some se ->
            eval se
            |> Result.bind (fun sv ->
                let rec tryWhens =
                    function
                    | [] -> fallback ()
                    | (whenExpr, resExpr) :: rest ->
                        eval whenExpr
                        |> Result.bind (fun wv ->
                            comparisonResult
                                ctx
                                se
                                (tryColumnDefForExpr ctx se)
                                sv
                                whenExpr
                                (tryColumnDefForExpr ctx whenExpr)
                                Eq
                                wv
                            |> Result.bind (function
                                | VInt 1L -> eval resExpr
                                | _ -> tryWhens rest))

                tryWhens whens)
        | None ->
            let rec tryWhens =
                function
                | [] -> fallback ()
                | (condExpr, resExpr) :: rest ->
                    eval condExpr
                    |> Result.bind (fun cv -> if truthy cv = Some true then eval resExpr else tryWhens rest)

            tryWhens whens
    | Between(e, lo, hi) ->
        eval e
        |> Result.bind (fun ve ->
            eval lo
            |> Result.bind (fun vlo ->
                eval hi
                |> Result.bind (fun vhi ->
                    match ve, vlo, vhi with
                    | VNull, _, _
                    | _, VNull, _
                    | _, _, VNull -> Ok VNull
                    | _ ->
                        resolvedCompare ctx ">=" e ve lo vlo
                        |> Result.bind (fun lower ->
                            resolvedCompare ctx "<=" e ve hi vhi
                            |> Result.map (fun upper -> boolToValue (lower >= 0 && upper <= 0))))))
    | FuncCall(name, [ argument ]) when name.Equals("DEFAULT", System.StringComparison.OrdinalIgnoreCase) ->
        match tryColumnDefForExpr ctx argument with
        | Some { Default = Some(DExpression expression) } -> eval expression
        | Some column when column.Default.IsSome || column.Nullable ->
            Ok(Storage.evalDefaultWithMode (Storage.temporalCoercionMode ctx.Store) column)
        | Some column -> Error(1364, sprintf "Field '%s' doesn't have a default value" column.Name)
        | None -> Error(1054, "Unknown column in 'field list'")
    | FuncCall(name, [ argument ]) when name.Equals("COERCIBILITY", System.StringComparison.OrdinalIgnoreCase) ->
        Ok(VInt(int64 (coercibilityOfExpr argument)))
    | FuncCall(name, [ argument ]) when name.Equals("SLEEP", System.StringComparison.OrdinalIgnoreCase) ->
        eval argument
        |> Result.bind (fun value ->
            let seconds = Value.toDouble value

            if value = VNull || System.Double.IsNaN seconds || System.Double.IsInfinity seconds || seconds < 0.0 then
                Error(1210, "Incorrect arguments to sleep.")
            else
                let cancellation = Storage.queryCancellation.Value
                let mutable remaining = seconds
                let mutable interrupted = false

                while remaining > 0.0 && not interrupted do
                    let interval = min remaining 0.1
                    interrupted <- cancellation.WaitHandle.WaitOne(System.TimeSpan.FromSeconds interval)
                    remaining <- remaining - interval

                Ok(VInt(if interrupted then 1L else 0L)))
    | FuncCall(name, [ countExpr; body ]) when name.Equals("BENCHMARK", System.StringComparison.OrdinalIgnoreCase) ->
        eval countExpr
        |> Result.bind (fun value ->
            let repetitions = Value.toDouble value

            if value = VNull || System.Double.IsNaN repetitions || System.Double.IsInfinity repetitions || repetitions < 0.0 then
                Ok VNull
            else
                let count =
                    if repetitions >= float System.Int64.MaxValue then
                        System.Int64.MaxValue
                    else
                        int64 repetitions
                let cancellation = Storage.queryCancellation.Value
                let mutable iteration = 0L
                let mutable failure = None

                while iteration < count && failure.IsNone do
                    if iteration % int64 Storage.cancellationCheckInterval = 0L then
                        cancellation.ThrowIfCancellationRequested()

                    match eval body with
                    | Ok _ -> iteration <- iteration + 1L
                    | Error error -> failure <- Some error

                match failure with
                | Some error -> Error error
                | None -> Ok(VInt 0L))
    | FuncCall(name, [ Cast(argument, TChar length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        eval argument |> Result.map (Functions.weightStringChar (keyCollation ctx argument) length)
    | FuncCall(name, [ Cast(argument, TBinary length) ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        eval argument
        |> Result.map (Functions.weightStringBinaryWith (Collation.Charset.encode (sourceCharset ctx argument)) length)
    | FuncCall(name, [ argument ]) when name.Equals("WEIGHT_STRING", System.StringComparison.OrdinalIgnoreCase) ->
        let source =
            match argument with
            | Cast(value, TChar _) -> value
            | Cast(value, TBinary _) -> value
            | _ -> argument

        eval argument |> Result.map (Functions.weightString (keyCollation ctx source))
    | FuncCall(name, subjectExpr :: patternExpr :: rest)
        when name.Equals("REGEXP_LIKE", System.StringComparison.OrdinalIgnoreCase)
             || name.Equals("REGEXP_INSTR", System.StringComparison.OrdinalIgnoreCase)
             || name.Equals("REGEXP_SUBSTR", System.StringComparison.OrdinalIgnoreCase)
             || name.Equals("REGEXP_REPLACE", System.StringComparison.OrdinalIgnoreCase) ->
        eval subjectExpr
        |> Result.bind (fun subject ->
            eval patternExpr
            |> Result.bind (fun pattern ->
                let subject = displayValueForText ctx subjectExpr subject
                let pattern = displayValueForText ctx patternExpr pattern

                rest
                |> traverse eval
                |> Result.bind (fun values ->
                    let arguments = subject :: pattern :: values

                    Functions.validateRegexpArity name arguments

                    regexCollation ctx (name.ToLowerInvariant()) subjectExpr subject patternExpr pattern
                    |> Result.map (fun collation ->
                        match Functions.regexpFunction name collation with
                        | Some function_ -> function_ arguments
                        | None -> VNull))))
    | FuncCall(name, args) ->
        let scalar =
            match Functions.lookup name ctx.Registry with
            | Some function_ -> Some function_
            | None when name.Contains('.', System.StringComparison.Ordinal) -> None
            | None -> Functions.lookup (ctx.DbName + "." + name) ctx.Registry

        match scalar with
        | None -> Error(unknownFunction name)
        | Some fn ->
            args
            |> traverse eval
            |> Result.bind (fun values ->
                try
                    let values =
                        List.zip args values
                        |> List.mapi (fun index (expression, value) ->
                            if Functions.isTextArgument name index ctx.Registry then
                                displayValueForText ctx expression value
                            else
                                value)

                    let invoke () = fn values

                    match registryAccount ctx.Registry with
                    | Some account ->
                        DynamicScope.withValue scalarExecutionAccount (Some account) invoke |> Ok
                    | None -> Ok(invoke ())
                with Diagnostics.EvaluationError(code, message) ->
                    Error(code, message))
    // `expr COLLATE name` evaluates as its inner expression — the tag
    // only steers which collation comparisons resolve under.
    | Collate(e, _) -> eval e
    | Cast(e, ((TChar _ | TVarchar _ | TBinary _ | TVarBinary _) as ty))
        when tryColumnDefForExpr ctx e |> Option.bind _.NumericDisplay |> Option.exists _.ZeroFill ->
        eval e
        |> Result.bind (fun value -> eval (Cast(Lit(displayValueForText ctx e value), ty)))
    // MySQL doesn't support CAST-ing to VECTOR (STRING_TO_VECTOR is the
    // sanctioned conversion), and quietly blob-coercing here would mint
    // wrong-dimension vectors that only fail much later, at INSERT time.
    | Cast(_, TVector _) -> Error(1064, "CAST to VECTOR is not supported; use STRING_TO_VECTOR()")
    // `CAST(x AS UNSIGNED)` maps into the whole `BIGINT UNSIGNED` domain by
    // *wrapping*, oracle-verified: -1 is 18446744073709551615 and
    // -9223372036854775808 is 9223372036854775808. That is not the same rule
    // a `BIGINT UNSIGNED` *column* applies to the same input (a column
    // clamps/rejects), so this can't route through `Storage.coerceValue` the
    // way the other cast targets do. Fractions round half-away-from-zero
    // before wrapping (`CAST(-1.9 AS UNSIGNED)` is 18446744073709551614),
    // Integer/DECIMAL inputs past the top clamp to it. An exponent-notation
    // DOUBLE past signed BIGINT instead clamps at 2^63-1, matching MySQL's
    // conversion through the approximate-number domain.
    | Cast(e, TBigInt true) ->
        eval e
        |> Result.map (fun v -> enumOrdinalFor ctx e v |> Option.defaultValue v)
        |> Result.map (fun v ->
            let wrap (d: decimal) =
                let n = System.Math.Round(d, System.MidpointRounding.AwayFromZero)

                if n >= 0m then
                    VUInt(uint64 (min n (decimal System.UInt64.MaxValue)))
                else
                    // Two's-complement wrap in exact arithmetic: `decimal`
                    // spans 2^64 with digits to spare, so this never rounds.
                    VUInt(uint64 (max 0m (n + decimal System.UInt64.MaxValue + 1m)))

            match v with
            | VNull -> VNull
            | VUInt _ -> v
            | VInt i -> VUInt(uint64 i)
            | VDecimal d -> wrap d
            | VDouble d ->
                if System.Double.IsNaN d then
                    VUInt 0UL
                elif d >= float System.Int64.MaxValue then
                    VUInt(uint64 System.Int64.MaxValue)
                elif d < float System.Int64.MinValue then
                    raise Value.UnsignedOutOfRange
                else
                    wrap (decimal d)
            | VString s ->
                // MySQL reads the leading numeric prefix and treats the rest
                // as garbage (`CAST('12abc' AS UNSIGNED)` is 12).
                match leadingNumericPrefix leadingFloatPrefixRegex s with
                | Some prefix ->
                    match System.Decimal.TryParse(prefix, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture) with
                    | true, d -> wrap d
                    | false, _ -> VUInt 0UL
                | None -> VUInt 0UL
            | other -> wrap (decimal (toDouble other)))
    // `CAST(x AS JSON)` yields a JSON-*typed* value, not text that happens to
    // look like JSON — which is what makes `CAST('1' AS JSON) < CAST('"a"' AS
    // JSON)` follow MySQL's JSON type precedence (`Value.compare`'s JSON
    // branch) instead of a string compare. Routing it through
    // `Storage.coerceValue`'s TJson case (shared with the text types) lost
    // both that and the printer's normalization: unparseable text came back
    // verbatim where MySQL raises 3141, and a valid document kept its
    // written key order and spacing rather than MySQL's stored order.
    | Cast(e, TJson) ->
        eval e
        |> Result.bind (fun v ->
            match v with
            | VNull -> Ok VNull
            | VJson _ -> Ok v
            // A non-string scalar converts to its own JSON shape: numbers to
            // JSON numbers, temporals to the quoted string MySQL renders
            // (`CAST(NOW() AS JSON)` is `"2026-01-01 00:00:00.000000"`).
            | VInt _
            | VDouble _
            | VDecimal _ -> Ok(VJson(toText v |> Option.defaultValue "null"))
            | VDate _
            | VDateTime _ -> Ok(VJson(Functions.jsonQuote (toTextFsp 6 v |> Option.defaultValue "")))
            | _ ->
                match Functions.jsonParseDocument (toText v |> Option.defaultValue "") with
                | Ok node -> Ok(VJson(Functions.jsonNodeText node))
                | Error() ->
                    Error(3141, "Invalid JSON text in argument 1 to function cast_as_json: \"Invalid value.\" at position 0."))
    | Cast(e, ty) ->
        eval e
        // Casting an ENUM to a number reads its declaration ordinal, the same
        // numeric context arithmetic and `=` put it in (`enumOrdinalFor`).
        |> Result.map (fun v ->
            match ty with
            | TTinyInt _ | TBool | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _
            | TDecimal _ | TDouble _ | TFloat _ -> enumOrdinalFor ctx e v |> Option.defaultValue v
            | _ -> v)
        |> Result.bind (fun v ->
            // Reuses `Storage.coerceValue` against a throwaway column of the
            // cast's target type rather than a second coercion table, always
            // non-strict: MySQL's own CAST never raises 1366 for an
            // out-of-range/unparseable conversion, independent of
            // STRICT_TRANS_TABLES — `CAST('abc' AS SIGNED)` is `0` (with a
            // warning), `CAST('abc' AS DATE)` is `NULL`, under the session's
            // *default* strict sql_mode included. A numeric target still
            // needs its own leading-numeric-prefix parse first
            // (`leadingNumericPrefix`): `coerceValue`'s own `parseNumeric`
            // requires the *whole* string to parse, so `CAST('12abc' AS
            // SIGNED)` (MySQL: `12`) would otherwise fall all the way to the
            // non-strict `0` fallback instead of `12`.
            let castCol: ColumnDef =
                { Name = "CAST"
                  Type = ty
                  NumericDisplay = None
                  Nullable = true
                  Default = None
                  AutoIncrement = false
                  PrimaryKey = false
                  Unique = false
                  Generated = None
                  Comment = ""
                  Collation = None
                  Charset = None
                  OnUpdateCurrentTimestamp = false }

            let v =
                match v, ty with
                | VUInt u, TBigInt false -> VInt(int64 u)
                | VString s, (TTinyInt _ | TBool | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _ | TYear) ->
                    VString(leadingNumericPrefix leadingIntegerPrefixRegex s |> Option.defaultValue "")
                | VString s, (TDouble _ | TFloat _ | TDecimal _) ->
                    VString(leadingNumericPrefix leadingFloatPrefixRegex s |> Option.defaultValue "")
                | _ -> v

            match
                Diagnostics.suppress (fun () ->
                    Storage.coerceValueWithMode
                        { Strict = false
                          NoZeroDate = true
                          NoZeroInDate = true
                          TruncateFractional = ctx.Store.ExecutionSettings.SqlMode.TimeTruncateFractional }
                        castCol
                        v)
            with
            | Ok(VZeroDate _)
            | Ok(VZeroDateTime _) -> Ok VNull
            | Ok v' -> Ok v'
            | Error err -> Error(Storage.toMySqlError err))
    | Exists select ->
        match (runExpressionSubquery ctx select (existsEarlyExitSelect select)).Result with
        | ResultSet(_, rows) -> Ok(boolToValue (not (List.isEmpty rows)))
        | Err(code, message) -> Error(code, message)
        // A `SELECT` under `EXISTS (...)` is never an `INSERT`/`UPDATE`/
        // `DELETE` (the parser's `selectStmtRecord` only builds `SelectStmt`
        // records, nothing else reaches here), so `Affected` can't occur.
        | Affected _ -> Ok VNull
        | MultipleResults _ -> Error nestedSubqueryResultsError
    | Subquery select ->
        // Reads the subquery's own typed `Value`, not a `VString` re-wrap
        // of its text resultset — a bare-text round trip would make e.g.
        // `(SELECT MAX(n) FROM t) > (SELECT MIN(n) FROM t)` compare
        // lexicographically instead of numerically.
        let subquery = runExpressionSubquery ctx select select

        match subquery.Result, subquery.Rows with
        | Err(code, message), _ -> Error(code, message)
        | Affected _, _ -> Ok VNull
        | MultipleResults _, _ -> Error nestedSubqueryResultsError
        | ResultSet(cols, _), _ when List.length cols <> 1 -> Error(1241, "Operand should contain 1 column(s)")
        | ResultSet(_, []), _ -> Ok VNull
        | ResultSet(_, [ _ ]), [ row ] -> Ok(row |> Array.tryHead |> Option.defaultValue VNull)
        | ResultSet(_, _), _ -> Error(1242, "Subquery returns more than 1 row")

and private evalRowOperand (ctx: EvalContext) (expr: Expr) : Result<RowOperand, EvalError> =
    match expr with
    | Row values ->
        values
        |> traverse (evalRowOperand ctx)
        |> Result.map RowValues
    | Subquery select ->
        let subquery = runExpressionSubquery ctx select select

        match subquery.Result, subquery.Rows with
        | Err(code, message), _ -> Error(code, message)
        | Affected _, _ -> Ok(RowValues [])
        | MultipleResults _, _ -> Error nestedSubqueryResultsError
        | ResultSet(columns, _), [] -> Ok(subqueryRowOperand ctx select (Array.create columns.Length VNull))
        | ResultSet(_, [ _ ]), [ row ] -> Ok(subqueryRowOperand ctx select row)
        | ResultSet(_, _), _ -> Error(1242, "Subquery returns more than 1 row")
    | _ ->
        evalExpr ctx expr
        |> Result.map (fun value -> RowScalar(expr, tryColumnDefForExpr ctx expr, value))

/// Evaluates an ORDER BY expression and applies the column-type-specific
/// sort representation. Expressions such as CAST(enum_col AS CHAR) remain
/// ordinary lexical strings; only a direct ENUM column reference uses its
/// declaration ordinal, matching MySQL.
and private existsEarlyExitSelect (select: SelectStmt) : SelectStmt =
    if select.Limit.IsNone
       && select.Offset.IsNone
       && select.OrderBy.IsEmpty
       && select.GroupBy.IsEmpty
       && select.Having.IsNone
       && not select.Distinct
       && not select.Rollup
       && select.Windows.IsEmpty
       && not select.CalculateFoundRows
       && select.Locking.IsEmpty then
        { select with Limit = Some(Lit(VInt 1L)) }
    else
        select

and private runExpressionSubquery
    (ctx: EvalContext)
    (cacheKey: SelectStmt)
    (select: SelectStmt)
    : ExpressionSubqueryResult =
    let memo = (currentStatementMemo ()).ExpressionSubqueries

    let equalityMembership rows =
        let containsNull = rows |> List.exists (Array.tryHead >> Option.exists ((=) VNull))
        let values = rows |> List.map (Array.tryHead >> Option.defaultValue VNull)

        let domain =
            match selectProjectionColumns ctx.Store ctx.DbName select with
            | [ Some column ] -> equalityMembershipDomain ctx.Store column
            | _ -> None

        domain
        |> Option.bind (fun domain ->
            let rec keys found =
                function
                | [] -> Some found
                | VNull :: rest -> keys found rest
                | value :: rest ->
                    equalityMembershipKey domain value
                    |> Option.bind (fun key -> keys (key :: found) rest)

            keys [] values
            |> Option.map (fun values ->
                { Values = Set.ofList values
                  ContainsNull = containsNull
                  Domain = domain }))

    let rowEqualityMembership (rows: Value[] list) =
        let domains =
            selectProjectionColumns ctx.Store ctx.DbName select
            |> List.map (Option.bind (equalityMembershipDomain ctx.Store))

        if domains.Length < 2 || domains |> List.exists Option.isNone then
            None
        else
            let domains = domains |> List.choose id

            let rec tupleKeys found domains values =
                match domains, values with
                | [], [] -> Some(List.rev found)
                | domain :: restDomains, value :: restValues ->
                    equalityMembershipKey domain value
                    |> Option.bind (fun key -> tupleKeys (key :: found) restDomains restValues)
                | _ -> None

            let rec rowsWithKeys (found: Value list list) containsNullableRows (remaining: Value[] list) =
                match remaining with
                | [] ->
                    Some
                        { Values = Set.ofList found
                          ContainsNullableRows = containsNullableRows
                          Domains = domains }
                | row :: rest when row.Length <> domains.Length -> None
                | row :: rest when row |> Array.contains VNull -> rowsWithKeys found true rest
                | row :: rest ->
                    tupleKeys [] domains (Array.toList row)
                    |> Option.bind (fun key -> rowsWithKeys (key :: found) containsNullableRows rest)

            rowsWithKeys [] false rows

    let execute outer =
        let result, _, rows = runSelectStmt ctx.Store ctx.Registry ctx.DbName select outer

        { Result = result
          Rows = rows
          EqualityMembership = None
          RowEqualityMembership = None }

    match memo.TryGetValue cacheKey with
    | true, MemoizedSubquery result -> result
    | true, UnmemoizedSubquery -> execute (Some ctx)
    | _ when isStatementStableSelect ctx.Store ctx.Registry ctx.DbName emptySubqueryScope cacheKey ->
        let result = execute None
        let result =
            { result with
                EqualityMembership = equalityMembership result.Rows
                RowEqualityMembership = rowEqualityMembership result.Rows }
        memo.[cacheKey] <- MemoizedSubquery result
        result
    | _ ->
        memo.[cacheKey] <- UnmemoizedSubquery
        execute (Some ctx)

and private evalOrderKey (ctx: EvalContext) (expr: Expr) : Result<Value * Collation.Collation option, EvalError> =
    let orderCtx = { ctx with Clause = OrderClause }
    evalExpr orderCtx expr |> Result.map (orderValueForExpr orderCtx expr)

/// Resolves one `TableRef` (a real table, or `information_schema`'s virtual
/// one) to its columns and rows — the one place both the base `FROM` and
/// every `JOIN` target resolve through, so there's exactly one
/// `information_schema` special case rather than one per call site.
and private resolveTableRef
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (tableRef: TableRef)
    : Result<ColumnDef list * Value[] list, QueryResult> =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    // An unqualified name resolves against the statement's `WITH` bindings
    // first — a CTE shadows a real table of the same name, as in MySQL.
    match (if tableRef.Database.IsSome then None else currentCteScope () |> Map.tryFind (tableRef.Table.ToLowerInvariant())) with
    | Some materialized -> Ok materialized
    | None ->

    if tableRef.Database.IsNone && System.String.Equals(tableRef.Table, "dual", System.StringComparison.OrdinalIgnoreCase) then
        Ok([], [ [||] ])
    elif System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
        match InformationSchema.scan store.Catalog tableRef.Table (Some(describeStoredViewColumns store registry)) with
        | Some(columns, rows) -> Ok(columns, rows)
        | None -> Error(storageErr (NoSuchTable tableRef.Table))
    elif
        System.String.Equals(tableDb, "fsdb", System.StringComparison.OrdinalIgnoreCase)
        && store.VirtualTables.ContainsKey(tableRef.Table.ToLowerInvariant())
    then
        // The host-extension overlay (`Db.registerTable`) on the `fsdb`
        // schema — which is also `Storage.defaultDatabase`, so this is an
        // overlay, not a whole-schema shadow: a registered name wins over a
        // same-named real table, every other real table falls through to
        // `scanList` below unchanged.
        // Full scan; the engine post-filters. There is no
        // information_schema-style narrowing analogue until a big virtual
        // table hurts.
        let vt = store.VirtualTables.[tableRef.Table.ToLowerInvariant()]
        Ok(vt.Columns, vt.Rows())
    else
        match tryStoredView store tableDb tableRef.Table with
        | Some view ->
            let key = view.Schema.ToLowerInvariant(), view.Name.ToLowerInvariant()
            let stack = currentViewStack ()
            let memo = (currentStatementMemo ()).Views

            match memo.TryGetValue key with
            | true, cached -> cached
            | _ when Set.contains key stack ->
                Error(Err(1462, sprintf "View's SELECT contains a recursive reference to view '%s'" view.Name))
            | _ ->
                let savedCtes = currentCteScope ()

                try
                    viewStack.Value <- Set.add key stack
                    cteScope.Value <- Map.empty

                    let resolved =
                        match Parser.parse view.Definition with
                        | Result.Ok((Select select) as statement) ->
                            match registryForView store registry view statement with
                            | Result.Error(code, message) -> Error(Err(code, message))
                            | Result.Ok viewRegistry ->
                                resolveFromSubquery
                                    store
                                    viewRegistry
                                    view.Schema
                                    (FromSubquery(PlainSelect select, view.Name))
                                    None
                        | Result.Ok((Union(first, rest, orderBy, limit, offset)) as statement) ->
                            match registryForView store registry view statement with
                            | Result.Error(code, message) -> Error(Err(code, message))
                            | Result.Ok viewRegistry ->
                                resolveFromSubquery
                                    store
                                    viewRegistry
                                    view.Schema
                                    (FromSubquery(UnionSelect(first, rest, orderBy, limit, offset), view.Name))
                                    None
                        | _ -> Error(Err(1356, sprintf "View '%s.%s' references invalid table(s) or column(s)" view.Schema view.Name))

                    let resolved =
                        resolved
                        |> Result.bind (fun (columns, rows) ->
                            let columns =
                                if view.Columns.IsEmpty then
                                    Ok columns
                                elif view.Columns.Length <> columns.Length then
                                    Error(
                                        Err(
                                            1353,
                                            "In definition of view, derived table or common table expression, SELECT list and column names list have different column counts"
                                        )
                                    )
                                else
                                    Ok(List.map2 (fun column name -> { column with Name = name }) columns view.Columns)

                            columns
                            |> Result.bind (fun columns ->
                                match columns |> List.countBy (fun column -> column.Name.ToLowerInvariant()) |> List.tryFind (fun (_, count) -> count > 1) with
                                | Some(name, _) -> Error(Err(1060, sprintf "Duplicate column name '%s'" name))
                                | None -> Ok(columns, rows)))

                    if not (isNull (box memo)) then memo.[key] <- resolved
                    resolved
                finally
                    viewStack.Value <- stack
                    cteScope.Value <- savedCtes
        | None ->
            if tableRef.Partitions.IsEmpty then
                tryPhysicalTableRef store dbName tableRef
                |> Result.bind (function
                    | Some table -> Ok(table.Columns, table.RowsArray |> List.ofSeq)
                    | None -> scanList store tableDb tableRef.Table |> Result.mapError storageErr)
            else
                match tableSnapshot store tableDb tableRef.Table with
                | Error e -> Error(storageErr e)
                | Ok unfiltered ->
                    let table = filterLockingReadTable tableRef unfiltered

                    match table.Partitioning with
                    | None -> Error(Err(1747, "PARTITION () clause on non partitioned table"))
                    | Some partitioning ->
                        let partitionNames =
                            [ 0u .. partitioning.Count - 1u ]
                            |> List.map (fun index -> sprintf "p%d" index, index)
                            |> Map.ofList

                        let requested =
                            tableRef.Partitions
                            |> List.distinctBy _.ToLowerInvariant()
                            |> List.map (fun name -> name.ToLowerInvariant())

                        match requested |> List.tryFind (fun name -> not (Map.containsKey name partitionNames)) with
                        | Some name -> Error(Err(1735, sprintf "Unknown partition '%s' in table '%s'" name table.OriginalName))
                        | None ->
                            let selected = requested |> List.map (fun name -> partitionNames.[name]) |> Set.ofList
                            let qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table
                            let columns = table.Columns
                            let ctxFor = contextFactory store registry tableDb (columnIndexOf columns) (singleQualifier qualifier columns) None

                            let rec selectRows selectedRows remaining =
                                match remaining with
                                | [] -> Ok(columns, List.rev selectedRows)
                                | row :: rest ->
                                    match evalExpr (ctxFor row) partitioning.Expression with
                                    | Error(code, message) -> Error(Err(code, message))
                                    | Ok value when Set.contains (hashPartitionIndex partitioning value) selected ->
                                        selectRows (row :: selectedRows) rest
                                    | Ok _ -> selectRows selectedRows rest

                            selectRows [] (List.ofSeq table.RowsArray)

/// Derives query columns from schema and expression metadata without reading
/// rows or evaluating user expressions.
and private describeQueryColumns
    (store: Store)
    (registry: Registry)
    (schema: string)
    (source: ColumnDescriptionSource)
    : ColumnDef list option =
    let decimalParts =
        function
        | TDecimal(precision, scale, _) -> Some(precision, scale)
        | TTinyInt _
        | TBool -> Some(3, 0)
        | TSmallInt _ -> Some(5, 0)
        | TMediumInt _ -> Some(7, 0)
        | TInt _ -> Some(10, 0)
        | TBigInt _ -> Some(19, 0)
        | _ -> None

    let literalDecimalParts =
        function
        | VInt value -> Some(string value |> fun text -> text.TrimStart('-').Length, 0)
        | VUInt value -> Some(string value |> fun text -> text.Length, 0)
        | VDecimal value ->
            let text = string value
            let dot = text.IndexOf '.'
            let scale = if dot < 0 then 0 else text.Length - dot - 1
            Some(text.TrimStart('-').Replace(".", "").Length, scale)
        | _ -> None

    let directProjection name (column: ColumnDef) =
        { column with
            Name = name
            Nullable = column.Nullable && not column.PrimaryKey
            Default = if column.AutoIncrement then Some(DConst(VInt 0L)) else column.Default
            AutoIncrement = false
            PrimaryKey = false
            Unique = false
            Generated = None
            Comment = "" }

    let describeColumn column =
        { Column = column
          NumericParts = decimalParts column.Type }

    let describeLiteral column value =
        { Column = column
          NumericParts = literalDecimalParts value |> Option.orElseWith (fun () -> decimalParts column.Type) }

    let renameColumns (names: string list) (columns: ViewColumnDescriptor list) =
        if names.IsEmpty then
            Some columns
        elif names.Length = columns.Length then
            List.map2
                (fun (descriptor: ViewColumnDescriptor) name ->
                    { descriptor with Column = { descriptor.Column with Name = name } })
                columns
                names
            |> Some
        else
            None

    let computedColumn name ty nullable defaultValue collation =
        { Name = name
          Type = ty
          NumericDisplay = None
          Nullable = nullable
          Default = defaultValue
          AutoIncrement = false
          PrimaryKey = false
          Unique = false
          Generated = None
          Comment = ""
          Collation = collation
          Charset = collation |> Option.map Collation.charsetOfCollation
          OnUpdateCurrentTimestamp = false }

    let isNullable (column: ColumnDef) = column.Nullable && not column.PrimaryKey

    let stringLength =
        function
        | TChar length
        | TVarchar length
        | TBinary length
        | TVarBinary length -> Some length
        | _ -> None

    let unionColumn (columns: ViewColumnDescriptor list) =
        let first = List.head columns
        let definitions = columns |> List.map _.Column
        let types = definitions |> List.map _.Type
        let collations =
            definitions
            |> List.choose _.Collation
            |> List.choose Collation.tryFind

        let collation =
            match collations with
            | [] -> None
            | first :: rest -> rest |> List.fold strictestUnionCollation first |> fun value -> Some value.Name

        let numericParts = columns |> List.map _.NumericParts
        let allNumeric = numericParts |> List.forall Option.isSome

        let decimalType () =
            numericParts
            |> List.choose id
            |> fun parts ->
                let scale = parts |> List.map snd |> List.max
                let integralDigits = parts |> List.map (fun (precision, partScale) -> precision - partScale) |> List.max
                TDecimal(min 65 (integralDigits + scale), scale, false)

        let displayLength =
            function
            | Some(precision, scale) -> precision + (if scale = 0 then 1 else 2)
            | None -> 1

        let stringType =
            let lengths =
                List.zip definitions numericParts
                |> List.map (fun (column, parts) -> stringLength column.Type |> Option.defaultValue (displayLength parts))

            lengths |> List.max |> TVarchar

        let mergedType =
            match types with
            | _ when types |> List.exists (stringLength >> Option.isSome) && types |> List.exists (fun ty -> decimalParts ty |> Option.isSome) -> stringType
            | _ when allNumeric && types |> List.exists (function TDecimal _ -> true | _ -> false) -> decimalType ()
            | _ when allNumeric && types |> List.forall ((=) first.Column.Type) -> first.Column.Type
            | _ when allNumeric -> TBigInt false
            | _ when types |> List.forall (stringLength >> Option.isSome) -> stringType
            | _ when types |> List.forall ((=) first.Column.Type) -> first.Column.Type
            | _ -> TText

        let nullable = definitions |> List.exists isNullable

        let clearedDefault =
            if nullable then
                None
            else
                match mergedType with
                | TTinyInt _
                | TBool
                | TSmallInt _
                | TMediumInt _
                | TInt _
                | TBigInt _ -> Some(DConst(VInt 0L))
                | TDecimal(_, scale, _) ->
                    let text = if scale = 0 then "0" else "0." + String.replicate scale "0"
                    Some(DConst(VString text))
                | TChar _
                | TVarchar _
                | TTinyText
                | TText
                | TMediumText
                | TLongText -> Some(DConst(VString ""))
                | _ -> None

        describeColumn (computedColumn first.Column.Name mergedType nullable clearedDefault collation)

    let unionColumns (branches: ViewColumnDescriptor list list) =
        match branches with
        | [] -> None
        | first :: _ when branches |> List.exists (fun columns -> columns.Length <> first.Length) -> None
        | first :: rest ->
            rest
            |> List.fold (fun merged branch -> List.map2 (fun left right -> unionColumn [ left; right ]) merged branch) first
            |> Some

    let rec describeBody (seen: Set<string * string>) dbName ctes =
        function
        | PlainSelect select -> describeSelect seen dbName ctes select
        | UnionSelect(first, rest, _, _, _) ->
            let branches = first :: (rest |> List.map snd)
            let described = branches |> List.map (describeSelect seen dbName ctes)

            if described |> List.forall Option.isSome then
                described |> List.choose id |> unionColumns
            else
                None

    and sourceColumns seen dbName ctes =
        function
        | FromTable tableRef ->
            let tableDb = tableRef.Database |> Option.defaultValue dbName

            if tableRef.Database.IsNone && Map.containsKey (tableRef.Table.ToLowerInvariant()) ctes then
                Map.tryFind (tableRef.Table.ToLowerInvariant()) ctes
            elif System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
                InformationSchema.scan store.Catalog tableRef.Table None |> Option.map (fst >> List.map describeColumn)
            else
                match tryStoredView store tableDb tableRef.Table with
                | Some(view: StoredView) ->
                    let key = view.Schema.ToLowerInvariant(), view.Name.ToLowerInvariant()

                    if Set.contains key seen then
                        None
                    else
                        Parser.parse view.Definition
                        |> Result.toOption
                        |> Option.bind (function
                            | Select select -> describeSelect (Set.add key seen) view.Schema Map.empty select
                            | Union(first, rest, orderBy, limit, offset) ->
                                describeBody (Set.add key seen) view.Schema Map.empty (UnionSelect(first, rest, orderBy, limit, offset))
                            | _ -> None)
                        |> Option.bind (fun columns ->
                            let declaredColumns: string list = view.Columns

                            renameColumns declaredColumns columns)
                | None -> scan store tableDb tableRef.Table |> Result.toOption |> Option.map (fst >> List.map describeColumn)
        | FromSubquery(body, _)
        | FromLateral(body, _) -> describeBody seen dbName ctes body
        | FromJsonTable(_, _, columns, _) -> jsonTableColumnDefs columns |> List.map describeColumn |> Some

    and describeSelect seen dbName inheritedCtes (select: SelectStmt) =
        let sourceItems = (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)

        let describeCte ctes (cte: CommonTableExpr) =
            match cte.Recursive, cte.Body with
            | true, UnionSelect(anchor, recursiveBranches, _, _, _) ->
                describeSelect seen dbName ctes anchor
                |> Option.bind (renameColumns cte.CteColumns)
                |> Option.bind (fun anchorColumns ->
                    let scope = Map.add (cte.CteName.ToLowerInvariant()) anchorColumns ctes

                    recursiveBranches
                    |> List.map (snd >> describeSelect seen dbName scope)
                    |> fun branches ->
                        if branches |> List.forall Option.isSome then
                            anchorColumns :: (branches |> List.choose id)
                            |> unionColumns
                            |> Option.map (List.map (fun descriptor ->
                                { descriptor with Column = { descriptor.Column with Nullable = true; Default = None } }))
                        else
                            None)
            | _ ->
                describeBody seen dbName ctes cte.Body
                |> Option.bind (renameColumns cte.CteColumns)

        let ctes =
            select.Ctes
            |> List.fold
                (fun resolved cte ->
                    resolved
                    |> Option.bind (fun ctes ->
                        describeCte ctes cte
                        |> Option.bind (fun columns ->
                            Some(Map.add (cte.CteName.ToLowerInvariant()) columns ctes))))
                (Some inheritedCtes)

        ctes
        |> Option.bind (fun cteMap ->
            sourceItems
            |> List.fold
                (fun collected item ->
                    collected
                    |> Option.bind (fun sources ->
                        sourceColumns seen dbName cteMap item
                        |> Option.map (fun columns -> sources @ [ fromItemQualifier item, columns ])))
                (Some [])
            |> Option.map (fun sources ->
            let descriptors = sources |> List.collect snd
            let columns = descriptors |> List.map _.Column
            let qualifiers =
                sources
                |> List.map (fun (qualifier, source) -> qualifier, source |> List.map _.Column)
                |> qualifierRanges
            let context = contextFactory store registry dbName (columnIndexOf columns) qualifiers None (probeRow columns)

            let columnForExpression name expression =
                match tryColumnDefForExpr context expression with
                | Some column -> directProjection name column |> describeColumn
                | None ->
                    let concatColumn =
                        match expression with
                        | FuncCall(functionName, arguments) when functionName.Equals("CONCAT", System.StringComparison.OrdinalIgnoreCase) ->
                            let lengthOf =
                                function
                                | Lit(VString text) -> Some(text.EnumerateRunes() |> Seq.length)
                                | value ->
                                    tryColumnDefForExpr context value
                                    |> Option.bind (fun column ->
                                        match column.Type with
                                        | TChar length
                                        | TVarchar length -> Some length
                                        | _ -> None)

                            let collation =
                                arguments
                                |> List.tryPick (fun argument -> tryColumnDefForExpr context argument |> Option.bind _.Collation)

                            arguments
                            |> List.map lengthOf
                            |> List.fold (fun total length -> total + Option.defaultValue 1 length) 0
                            |> min 65535
                            |> fun length -> Some(computedColumn name (TVarchar length) true None collation)
                        | _ -> None

                    let parts expression =
                        tryColumnDefForExpr context expression
                        |> Option.bind (fun column -> decimalParts column.Type)
                        |> Option.orElseWith (fun () ->
                            match expression with
                            | Lit value -> literalDecimalParts value
                            | _ -> None)

                    let decimalDefault scale =
                        if scale = 0 then DConst(VString "0") else DConst(VString("0." + String.replicate scale "0"))

                    let literalColumn =
                        match expression with
                        | Lit(VInt value) ->
                            let literalType =
                                if value >= -99999999L && value <= 99999999L then
                                    TInt false
                                else
                                    TBigInt false

                            computedColumn name literalType false (Some(DConst(VInt 0L))) None
                            |> fun column -> describeLiteral column (VInt value)
                            |> Some
                        | Lit(VUInt value) ->
                            computedColumn name (TBigInt true) false (Some(DConst(VInt 0L))) None
                            |> fun column -> describeLiteral column (VUInt value)
                            |> Some
                        | Lit(VDecimal value) ->
                            literalDecimalParts (VDecimal value)
                            |> Option.map (fun (precision, scale) ->
                                computedColumn name (TDecimal(precision, scale, false)) false (Some(decimalDefault scale)) None
                                |> fun column -> describeLiteral column (VDecimal value))
                        | Lit(VDouble _) -> Some(computedColumn name (TDouble false) false (Some(DConst(VInt 0L))) None |> describeColumn)
                        | Lit(VString text) ->
                            Some(computedColumn name (TVarchar(text.EnumerateRunes() |> Seq.length)) false (Some(DConst(VString ""))) (Some "utf8mb4_0900_ai_ci") |> describeColumn)
                        | Lit VNull -> Some(computedColumn name (TVarBinary 0) true None None |> describeColumn)
                        | _ -> None

                    let arithmeticColumn =
                        match expression with
                        | BinOp((Add | Sub | SignedSub | Mul | Div), left, right) ->
                            let isDecimal expression =
                                match tryColumnDefForExpr context expression with
                                | Some { Type = TDecimal _ } -> true
                                | _ ->
                                    match expression with
                                    | Lit(VDecimal _) -> true
                                    | _ -> false

                            match isDecimal left || isDecimal right, parts left, parts right with
                            | true, Some(leftPrecision, leftScale), Some(rightPrecision, rightScale) ->
                                let precision, scale =
                                    match expression with
                                    | BinOp((Add | Sub | SignedSub), _, _) ->
                                        let scale = max leftScale rightScale
                                        min 65 (max (leftPrecision - leftScale) (rightPrecision - rightScale) + scale + 1), scale
                                    | BinOp(Mul, _, _) -> min 65 (leftPrecision + rightPrecision), min 30 (leftScale + rightScale)
                                    | BinOp(Div, _, _) ->
                                        let scale = max 6 (leftScale + rightPrecision + 1)
                                        min 65 (leftPrecision - leftScale + rightScale + scale), scale
                                    | _ -> 65, 30

                                let nullable expression = tryColumnDefForExpr context expression |> Option.map isNullable |> Option.defaultValue false
                                Some(computedColumn name (TDecimal(precision, scale, false)) (nullable left || nullable right) None None)
                            | _ -> None
                        | _ -> None

                    let aggregateColumn =
                        match expression with
                        | FuncCall(functionName, _) when functionName.Equals("COUNT", System.StringComparison.OrdinalIgnoreCase) ->
                            Some(computedColumn name (TBigInt false) false (Some(DConst(VInt 0L))) None)
                        | FuncCall(functionName, [ argument ])
                            when functionName.Equals("SUM", System.StringComparison.OrdinalIgnoreCase) ->
                            tryColumnDefForExpr context argument
                            |> Option.bind (fun column ->
                                decimalParts column.Type
                                |> Option.map (fun (precision, scale) ->
                                    computedColumn name (TDecimal(min 65 (precision + 22), scale, false)) true None None))
                        | FuncCall(functionName, [ argument ])
                            when functionName.Equals("AVG", System.StringComparison.OrdinalIgnoreCase) ->
                            tryColumnDefForExpr context argument
                            |> Option.bind (fun column ->
                                decimalParts column.Type
                                |> Option.map (fun (precision, scale) ->
                                    computedColumn name (TDecimal(min 65 (precision + 4), min 30 (scale + 4), false)) true None None))
                        | FuncCall(functionName, [ argument ])
                            when functionName.Equals("MIN", System.StringComparison.OrdinalIgnoreCase)
                                 || functionName.Equals("MAX", System.StringComparison.OrdinalIgnoreCase) ->
                            tryColumnDefForExpr context argument
                            |> Option.map (fun column -> { directProjection name column with Nullable = true; Default = None })
                        | _ -> None

                    let comparisonColumn =
                        match expression with
                        | BinOp((And | Or | Xor | Eq | Neq | Lt | Lte | Gt | Gte | NullSafeEq), _, _)
                        | Not _
                        | IsNull _
                        | IsNotNull _
                        | IsTrue _
                        | IsFalse _
                        | Like _
                        | Regexp _
                        | In _
                        | InSubquery _
                        | QuantifiedComparison _
                        | Between _
                        | Exists _ -> Some(computedColumn name (TInt false) false (Some(DConst(VInt 0L))) None)
                        | _ -> None

                    match literalColumn, comparisonColumn, aggregateColumn, arithmeticColumn, concatColumn, metadataOfExpr context expression with
                    | Some column, _, _, _, _, _ -> column
                    | _, Some column, _, _, _, _ -> describeColumn column
                    | _, _, Some column, _, _, _ -> describeColumn column
                    | _, _, _, Some column, _, _ -> describeColumn column
                    | _, _, _, _, Some column, _ -> describeColumn column
                    | _, _, _, _, _, Some metadata ->
                        deriveColumns [ name ] [ keyCollation context expression ] [ metadata ] |> List.head |> describeColumn
                    | _ -> computedColumn name TText true None (Some "utf8mb4_0900_ai_ci") |> describeColumn

            select.Projections
            |> List.collect (fun (expression, alias) ->
                match expression with
                | Star None -> descriptors
                | Star(Some qualifier) ->
                    qualifiers
                    |> Map.tryFind (qualifier.ToLowerInvariant())
                    |> Option.map (fst >> List.map describeColumn)
                    |> Option.defaultValue []
                | _ -> [ columnForExpression (alias |> Option.defaultValue (exprLabel expression)) expression ])))

    (match source with
     | StoredRelation name -> sourceColumns Set.empty schema Map.empty (FromTable { Database = None; Table = name; Alias = None; Partitions = [] })
     | QueryBody body -> describeBody Set.empty schema Map.empty body)
    |> Option.map (List.map _.Column)

and private describeStoredViewColumns (store: Store) (registry: Registry) (schema: string) (name: string) : ColumnDef list option =
    describeQueryColumns store registry schema (StoredRelation name)

/// Pre-filters an `information_schema` scan by the WHERE's top-level
/// `col = 'literal'` equality conjuncts (`TABLE_SCHEMA`/`TABLE_NAME` is what
/// GUI clients send) — building every catalog row only for the executor to
/// discard all but one table's was the `COLUMNS` hotspot. Conjuncts come
/// from the same `pointLookupEqualities` the PK fast path uses (inheriting
/// its correlated-qualifier guard); for the self-contained per-table views
/// the catalog itself is narrowed before row construction, and every view's
/// rows are post-filtered. Pure narrowing: the full WHERE still runs over
/// the result. `None` when the FROM isn't information_schema or the WHERE
/// has no usable conjunct (plain scan then).
/// The pre-filter compares OrdinalIgnoreCase where the WHERE
/// proper compares ai_ci — an accented table name queried by its unaccented
/// spelling would be over-filtered; GUI clients echo names the server gave
/// them, so this stays until something real hits it. Joined
/// information_schema queries take the unnarrowed path.
and private tryInformationSchemaNarrow
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (tableRef: TableRef)
    (where: Expr option)
    : (ColumnDef list * Value[] list) option =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    if not (System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase)) then
        None
    else
        let eqI (a: string) (b: string) =
            System.String.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)

        match
            pointLookupEqualities tableRef where
            |> List.choose (function
                | { Column = name; Transform = None; Value = VString value } -> Some(name, value)
                | _ -> None)
        with
        | [] -> None
        | eqs ->
            // The heavy per-table views derive each row from exactly one
            // catalog table, so a TABLE_SCHEMA/TABLE_NAME equality can
            // shrink the catalog before any row is built (cross-table views
            // like KEY_COLUMN_USAGE only get the post-filter).
            let eqFor col = eqs |> List.tryPick (fun (n, v) -> if eqI n col then Some v else None)

            let selfContained =
                match tableRef.Table.ToUpperInvariant() with
                // TABLES projects user views from mysql.views, so narrowing
                // away mysql would hide them before the ordinary row filter.
                | "STATISTICS" | "PARTITIONS" -> true
                | _ -> false

            let catalog =
                if selfContained then
                    let bySchema =
                        match eqFor "TABLE_SCHEMA" with
                        | Some s -> store.Catalog |> Map.filter (fun db _ -> eqI db s)
                        | None -> store.Catalog

                    match eqFor "TABLE_NAME" with
                    | Some t -> bySchema |> Map.map (fun _ db -> db |> Map.filter (fun _ tbl -> eqI tbl.OriginalName t))
                    | None -> bySchema
                else
                    store.Catalog

            InformationSchema.scan catalog tableRef.Table (Some(describeStoredViewColumns store registry))
            |> Option.map (fun (cols, rows) ->
                let filters =
                    eqs |> List.choose (fun (name, v) -> resolveColumn cols name |> Result.toOption |> Option.map (fun i -> i, v))

                let keep (row: Value[]) =
                    filters
                    |> List.forall (fun (i, v) ->
                        match row.[i] with
                        | VString s -> eqI s v
                        | _ -> false)

                cols, rows |> List.filter keep)

/// Reconstructs nullable derived-table columns from result metadata so an
/// outer query retains the source's numeric, temporal, and string families.
and private deriveColumns
    (names: string list)
    (collations: Collation.Collation list)
    (metadata: ColumnMetadata list)
    : ColumnDef list =
    let declaredType (column: ColumnMetadata) =
        let unsigned = column.Flags &&& UnsignedFlag <> 0us
        let characters = int column.ColumnLength / 4

        if column.TypeId = TypeTiny && column.ColumnLength = 1u then TBool
        elif column.TypeId = TypeTiny then TTinyInt unsigned
        elif column.TypeId = TypeShort then TSmallInt unsigned
        elif column.TypeId = TypeLong then TInt unsigned
        elif column.TypeId = TypeLongLong then TBigInt unsigned
        elif column.TypeId = TypeFloat then TFloat false
        elif column.TypeId = TypeDouble then TDouble false
        elif column.TypeId = TypeNewDecimal then TDecimal(65, int column.Decimals, column.Flags &&& UnsignedFlag <> 0us)
        elif column.TypeId = TypeDate then TDate
        elif column.TypeId = TypeDateTime then TDateTime(int column.Decimals)
        elif column.TypeId = TypeTime then TTime(int column.Decimals)
        elif column.TypeId = TypeYear then TYear
        elif column.TypeId = TypeString && column.Flags &&& EnumFlag <> 0us then TEnum []
        elif column.TypeId = TypeString && column.Flags &&& SetFlag <> 0us then TSet []
        elif column.TypeId = TypeString && column.Flags &&& BinaryFlag <> 0us then TBinary(int column.ColumnLength)
        elif column.TypeId = TypeString then TChar characters
        elif column.TypeId = TypeVarString && column.Flags &&& BinaryFlag <> 0us then TVarBinary(int column.ColumnLength)
        elif column.TypeId = TypeVarString then TVarchar characters
        elif column.TypeId = TypeBlob && column.Flags &&& BinaryFlag <> 0us then TBlob
        elif column.TypeId = TypeBlob then TText
        else TText

    let metadata =
        if metadata.Length = names.Length then metadata else names |> List.map (fun _ -> columnMetadata TypeVarString)

    List.map3
        (fun n (col: Collation.Collation) columnMetadata ->
            { Name = n
              Type = declaredType columnMetadata
              NumericDisplay = None
              Nullable = columnMetadata.Flags &&& NotNullFlag = 0us
              Default = None
              AutoIncrement = false
              PrimaryKey = false
              Unique = false
              Generated = None
              Comment = ""
              Collation = Some col.Name
              Charset = None
              OnUpdateCurrentTimestamp = false })
        names
        collations
        metadata

/// One `SELECT`'s per-output-column collations, resolved the same way
/// `runSelect`'s own DISTINCT keys are (`keyCollation` on the output column
/// name through the select's own context) — a bin column keeps åge/age/ÅGE
/// apart, an ai_ci one folds them, and a literal output (no source column to
/// resolve) falls back to the connection collation. Used by the UNION
/// dedupe and by `resolveFromItem`'s derived-table metadata, so both see the
/// same collation for the same select.
and private selectColumnCollations
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (names: string list)
    : Collation.Collation list =
    let columns, qualifier =
        match select.From with
        | Some fromItem ->
            match resolveFromItem store registry dbName fromItem with
            | Ok(cols, _) -> cols, fromItemQualifier fromItem
            | Error _ -> [], ""
        | None -> [], ""

    let qualifiers =
        if qualifier = "" then
            Map.empty
        else
            singleQualifier qualifier columns

    let ctx = contextFactory store registry dbName (columnIndexOf columns) qualifiers None (probeRow columns)

    names |> List.map (fun name -> keyCollation ctx (Col name))

/// The declared fsp per output column of one UNION branch — the same
/// probe-row context setup as `selectColumnCollations`, resolved through
/// `outputColumnFsps` so a `DATETIME(6)` column or `CAST(... AS DATETIME(1))`
/// reports its declared digits and a bare literal reports `None`. Falls back
/// to all-`None` when the projection expansion doesn't line up with the
/// branch's output columns (e.g. its `FROM` failed to resolve).
and private selectColumnFsps
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (names: string list)
    : int option list =
    let columns, qualifier =
        match select.From with
        | Some fromItem ->
            match resolveFromItem store registry dbName fromItem with
            | Ok(cols, _) -> cols, fromItemQualifier fromItem
            | Error _ -> [], ""
        | None -> [], ""

    let qualifiers =
        if qualifier = "" then
            Map.empty
        else
            singleQualifier qualifier columns

    let ctx = contextFactory store registry dbName (columnIndexOf columns) qualifiers None (probeRow columns)
    let fsps = outputColumnFsps ctx columns select.Projections

    if List.length fsps = List.length names then
        fsps
    else
        names |> List.map (fun _ -> None)

/// Resolves one `FromItem` — a real/virtual table via `resolveTableRef`, or
/// a derived table by running its subquery (uncorrelated: a plain derived
/// table can't see the outer query's columns, only `LATERAL` ones could,
/// which this engine doesn't support) and using its typed rows directly
/// (`runSelectStmt`'s third component — see its doc) under synthetic
/// `deriveColumns` column metadata, so e.g. `SELECT MAX(y.n) FROM (SELECT n
/// FROM t) y` still compares `y.n` numerically instead of falling back to a
/// lexicographic `VString` comparison.
/// Synthetic column metadata for a `JSON_TABLE(...)`'s COLUMNS clause —
/// every column nullable (empty/error yields NULL, the only mode this
/// subset supports), `FOR ORDINALITY` an unsigned INT like MySQL's.
and private jsonTableColumnDefs (columns: JsonTableColumn list) : ColumnDef list =
    columns
    |> List.map (fun c ->
        let def name ty =
            [ { Name = name
                Type = ty
                NumericDisplay = None
                Nullable = true
                Default = None
                AutoIncrement = false
                PrimaryKey = false
                Unique = false
                Generated = None
                Comment = ""
                Collation = None
                Charset = None
                OnUpdateCurrentTimestamp = false } ]

        match c with
        | ForOrdinality name -> def name (TInt true)
        | PathColumn(name, ty, _, _, _) -> def name ty
        | ExistsColumn(name, ty, _) -> def name ty
        // A NESTED PATH contributes its children's columns, not one of its
        // own, flattened in declaration order — the same order
        // `jsonTableRows` emits cells in.
        | NestedColumns(_, nested) -> jsonTableColumnDefs nested)
    |> List.collect id

/// Expands one already-evaluated JSON_TABLE source document into rows — the
/// one expansion both eval sites (`resolveFromItem`'s uncorrelated case and
/// `applyJsonTableJoin`'s per-left-row lateral branch) share. Oracle-pinned
/// semantics (MySQL 8.4.11): NULL doc → no rows (the inner join then drops
/// the left row); malformed doc → error 3141; no row-path match (including a
/// scalar or object under `$[*]`) → no rows; a column path's empty or
/// erroneous result → NULL (the default NULL ON EMPTY / NULL ON ERROR); FOR
/// ORDINALITY counts 1-based per invocation, so the lateral form restarts it
/// per left row.
and private jsonTableRows (doc: Value) (path: string) (columns: JsonTableColumn list) : Result<Value[] list, QueryResult> =
    // One column value: extract → unquote → coerce through a throwaway
    // ColumnDef (the `Cast` case's `Storage.coerceValue` trick), but
    // *strict*, so an uncoercible value ('abc' into INT) becomes the pinned
    // NULL rather than non-strict's 0. Numeric fractions truncate
    // toward zero like this engine's CAST (MySQL's column store rounds,
    // 3.7 → 4); align `coerceValue` if a workload ever notices.
    // Strict coercion into the declared column type, shared by an extracted
    // node and by a `DEFAULT` literal. `Error` is JSON_TABLE's "ON ERROR"
    // condition (oracle-pinned: an array or the unconvertible string '5x'
    // under an INT column takes the ON ERROR branch, while a matched JSON
    // *null* is simply NULL and takes neither branch).
    let coerce (ty: ColumnType) (raw: Value) : Result<Value, unit> =
        match Storage.coerceValue true (jsonTableColumnDefs [ PathColumn("JSON_TABLE", ty, "", JsonNull, JsonNull) ] |> List.head) raw with
        | Ok v -> Ok v
        | Error _ -> Error()

    let actionValue (name: string) (ty: ColumnType) (rowNumber: int) (onError: bool) (action: JsonTableAction) : Result<Value, QueryResult> =
        match action with
        | JsonNull -> Ok VNull
        | JsonDefault VNull -> Ok VNull
        | JsonDefault value -> Ok(coerce ty value |> Result.defaultValue VNull)
        | JsonError when onError ->
            let targetType =
                match ty with
                | TTinyInt _
                | TSmallInt _
                | TMediumInt _
                | TInt _
                | TBigInt _
                | TBool -> "INTEGER"
                | _ -> InformationSchema.columnTypeText ty |> _.ToUpperInvariant()

            Error(Err(3156, sprintf "Invalid JSON value for CAST to %s from column %s at row %d" targetType name rowNumber))
        | JsonError -> Error(Err(3665, sprintf "Missing value for JSON_TABLE column '%s'" name))

    let columnValue (ty: ColumnType) (node: JsonNode) : Result<Value, unit> =
        match node with
        | null -> Ok VNull // a matched JSON null
        | _ when ty = TJson -> Ok(VJson(Functions.jsonNodeText node))
        | _ ->
            let raw =
                match node.GetValueKind() with
                // An object/array into a scalar column is a coercion error
                // → NULL (oracle-pinned: `{"a":1}` under INT/VARCHAR → NULL).
                | JsonValueKind.Object
                | JsonValueKind.Array -> VNull
                | JsonValueKind.String -> VString(node.GetValue<string>())
                | JsonValueKind.True -> VInt 1L
                | JsonValueKind.False -> VInt 0L
                | _ -> VString(node.ToJsonString())

            match raw with
            // An object/array under a scalar column: `raw` was already
            // flattened to VNull above, and MySQL calls that ON ERROR.
            | VNull -> Error()
            | _ -> coerce ty raw

    match doc with
    | VNull -> Ok []
    | v ->
        let text = toText v |> Option.defaultValue ""

        match Functions.jsonParseDocument text with
        | Error() ->
            // MySQL's inner parser detail (`"Invalid value." at position N.`)
            // isn't reproduced — the code and prefix are the pinned part.
            Error(Err(3141, "Invalid JSON text in argument 1 to function json_table: \"Invalid value.\" at position 0."))
        | Ok root ->
            match Functions.jsonPathNodes root path with
            | None -> Error(Err(3143, "Invalid JSON path expression. The error is around character position 1."))
            | Some matches ->
                // One node's non-nested cells, in declaration order.
                let plainCells (node: JsonNode) (ordinal: int) (cols: JsonTableColumn list) : Result<Value list, QueryResult> =
                    cols
                    |> traverse (function
                        | ForOrdinality _ -> Ok(VInt(int64 ordinal + 1L))
                        | ExistsColumn(_, ty, colPath) ->
                            // Never NULL and never an error: 1 when the path
                            // matches at least one node, 0 otherwise, in the
                            // column's own declared type.
                            let hit =
                                match Functions.jsonPathNodes node colPath with
                                | Some(_ :: _) -> 1L
                                | _ -> 0L

                            Ok(coerce ty (VInt hit) |> Result.defaultValue (VInt hit))
                        | PathColumn(name, ty, colPath, onEmpty, onError) ->
                            // An unparseable *column* path takes the
                            // ON ERROR branch instead of MySQL's 3143 at
                            // prepare time; a multi-node match is ON ERROR too.
                            match Functions.jsonPathNodes node colPath with
                            | Some [ single ] ->
                                match columnValue ty single with
                                | Ok value -> Ok value
                                | Error() -> actionValue name ty (ordinal + 1) true onError
                            | Some [] -> actionValue name ty (ordinal + 1) false onEmpty
                            | _ -> actionValue name ty (ordinal + 1) true onError
                        | NestedColumns _ -> Ok VNull)

                // A NESTED PATH multiplies its parent's row once per node it
                // matches, and siblings never cross-join: each sibling's rows
                // carry NULL for the others' columns. A parent whose nested
                // paths all match nothing still yields one row with those
                // columns NULL (MySQL's OUTER semantics).
                let rec expand (node: JsonNode) (ordinal: int) (cols: JsonTableColumn list) : Result<Value list list, QueryResult> =
                    plainCells node ordinal cols
                    |> Result.bind (fun plain ->
                        // Splice one sibling's expanded cells into the flattened
                        // row, leaving every other slot as its NULL placeholder.
                        let spliceAt (index: int) (cells: Value list) =
                            cols
                            |> List.mapi (fun i c ->
                                match c with
                                | NestedColumns _ when i = index -> cells
                                | NestedColumns(_, nested) -> jsonTableColumnDefs nested |> List.map (fun _ -> VNull)
                                | _ -> [ List.item i plain ])
                            |> List.collect id

                        let nestedRows =
                            cols
                            |> List.indexed
                            |> traverse (fun (i, c) ->
                                match c with
                                | NestedColumns(nestedPath, nested) ->
                                    match Functions.jsonPathNodes node nestedPath with
                                    | Some (_ :: _ as nodes) ->
                                        nodes
                                        |> List.mapi (fun j child -> expand child j nested)
                                        |> traverse id
                                        |> Result.map (List.collect id >> List.map (spliceAt i))
                                    | _ -> Ok []
                                | _ -> Ok [])
                            |> Result.map (List.collect id)

                        nestedRows
                        |> Result.map (fun rows ->
                            match rows with
                            | [] when cols |> List.exists (function NestedColumns _ -> true | _ -> false) ->
                                [ spliceAt -1 [] ]
                            | [] -> [ plain ]
                            | rows -> rows))

                matches
                |> List.mapi (fun i node -> expand node i columns)
                |> traverse id
                |> Result.map (List.collect id >> List.map (List.toArray))

and private resolveFromItem (store: Store) (registry: Registry) (dbName: string) (item: FromItem) : Result<ColumnDef list * Value[] list, QueryResult> =
    match item with
    | FromTable tableRef -> resolveTableRef store registry dbName tableRef
    | FromLateral _ ->
        // A leading `FROM LATERAL (...)` has nothing to its left, so it is
        // just a derived table — including MySQL's own error for a column
        // reference that would have needed a left row. The correlated form
        // is `applyLateralJoin`, which re-runs the body per left row.
        resolveFromSubquery store registry dbName item None
    | FromJsonTable(source, path, columns, _alias) ->
        // The uncorrelated site (`FROM JSON_TABLE('literal', ...) jt` as the
        // base FROM): the source evaluates in a no-columns literal context,
        // same as INSERT ... VALUES expressions. The correlated/lateral form
        // is `applyJsonTableJoin`, which re-evaluates per left row instead.
        // A column reference here can never resolve — oracle-pinned (MySQL
        // 8.4.11): a table-qualified one is 1109 "Unknown table 't' in a
        // table function argument" (even when `t` appears *later* in the
        // FROM list — a forward reference is illegal, and MySQL-compatible
        // clients match on the 1109 code), and a bare one is 1054 with the
        // same context string, not the literal context's 'field list'.
        match firstColumnRef source with
        | Some(QualifiedCol(table, _)) -> Error(Err(1109, sprintf "Unknown table '%s' in a table function argument" table))
        | Some(Col name) -> Error(Err(1054, sprintf "Unknown column '%s' in 'a table function argument'" name))
        | _ ->
            let literalCtx = contextFactory store registry dbName Map.empty Map.empty None [||]

            match evalExpr literalCtx source with
            | Error(code, message) -> Error(Err(code, message))
            | Ok doc -> jsonTableRows doc path columns |> Result.map (fun rows -> jsonTableColumnDefs columns, rows)
    | FromSubquery _ ->
        // Serve a derived table from the per-statement memo if already
        // resolved through the statement memo, else compute and record it.
        let memo = (currentStatementMemo ()).FromSubqueries

        match memo.TryGetValue item with
        | true, cached -> cached
        | _ ->
            let computed = resolveFromSubquery store registry dbName item None
            memo.[item] <- computed
            computed

and private resolveFromSubquery
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (item: FromItem)
    (outer: EvalContext option)
    : Result<ColumnDef list * Value[] list, QueryResult> =
    match item with
    | FromTable _
    | FromJsonTable _ -> resolveFromItem store registry dbName item
    | FromSubquery(body, _alias)
    // A LATERAL body resolved *without* a left row is only ever the column
    // metadata probe `applyLateralJoin` runs when the left side is empty
    // (see its doc); its correlated references evaluate against the outer
    // context there, which is `None` here, so a column that only a left row
    // could supply resolves to NULL rather than erroring.
    | FromLateral(body, _alias) ->
        let result, metadata, typedRows =
            match body with
            | PlainSelect select -> runSelectStmt store registry dbName select outer
            | UnionSelect(first, rest, orderBy, limit, offset) ->
                runUnionStmtWithOuter store registry dbName first rest orderBy limit offset outer

        match result with
        | ResultSet(cols, _) ->
            // The derived columns carry their source collations — a literal
            // resolves to the connection collation, a bin column stays bin,
            // a union aggregates to its strictest branch — so outer
            // comparisons against them use the same collation MySQL would.
            let collations =
                match body with
                | PlainSelect select -> selectColumnCollations store registry dbName select cols
                | UnionSelect(first, rest, _, _, _) ->
                    (first :: (rest |> List.map snd))
                    |> List.map (fun branch -> selectColumnCollations store registry dbName branch cols)
                    |> List.reduce (List.map2 strictestUnionCollation)

            let derivedColumns = deriveColumns cols collations metadata

            let sourceColumns =
                match body with
                | PlainSelect select -> selectProjectionColumns store dbName select
                | UnionSelect _ -> []

            let columns =
                if sourceColumns.Length = derivedColumns.Length then
                    List.map2
                        (fun derived source ->
                            source
                            |> Option.map (fun source ->
                                { derived with
                                    Type = source.Type
                                    NumericDisplay = source.NumericDisplay
                                    Collation = source.Collation
                                    Charset = source.Charset })
                            |> Option.defaultValue derived)
                        derivedColumns
                        sourceColumns
                else
                    derivedColumns

            Ok(columns, typedRows)
        | Err(code, message) -> Error(Err(code, message))
        | Affected _ -> Error(Err(1064, "derived table did not return a resultset"))
        | MultipleResults _ -> Error(nestedResultsError "a derived table")

/// The qualifier a `FROM`/`JOIN` source's columns resolve `qualifier.col`
/// against: a real table's alias (or its own name), or a derived table's
/// mandatory alias.
and private fromItemQualifier (item: FromItem) : string =
    match item with
    | FromTable t -> t.Alias |> Option.defaultValue t.Table
    | FromSubquery(_, alias)
    | FromLateral(_, alias) -> alias
    | FromJsonTable(_, _, _, alias) -> alias

/// `EvalContext.Qualifiers` for every source (the `FROM` table, and each
/// `JOIN` after it) already resolved into `sources`, ordered the same
/// left-to-right way their columns are laid out in a combined row —
/// offsets accumulate across the fold, so source *n*'s columns start right
/// where source *n-1*'s end.
and private qualifierRanges (sources: (string * ColumnDef list) list) : Map<string, ColumnDef list * int> =
    sources
    |> List.fold (fun (offset, quals) (qualifier, cols) -> offset + List.length cols, Map.add (qualifier.ToLowerInvariant()) (cols, offset) quals) (0, Map.empty)
    |> snd

/// The one `WHERE` predicate every scan and mutation path shares — no
/// clause matches everything, otherwise SQL's three-valued truthiness
/// (`evalExpr` against the row under `WhereClause`, `NULL`/`false` both
/// meaning "no match", only an explicit `true` keeping the row).
and private whereMatches (ctxFor: Value[] -> EvalContext) (where: Expr option) (row: Value[]) : Result<bool, EvalError> =
    match where with
    | None -> Ok true
    | Some expr -> evalExpr { ctxFor row with Clause = WhereClause } expr |> Result.map (fun v -> truthy v = Some true)

/// `NATURAL JOIN`'s name set: every column name the two sides share,
/// matched case-insensitively (MySQL matches `c1(X)` against `c2(x)`), in
/// the left side's declaration order — which is also MySQL's output order
/// for the coalesced columns of a `SELECT *`.
and private naturalCommonNames (leftColumns: ColumnDef list) (rightColumns: ColumnDef list) : string list =
    leftColumns
    |> List.map (fun c -> c.Name)
    |> List.filter (fun name ->
        rightColumns |> List.exists (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)))

/// The equi-key index pairs for a `NATURAL`/`USING` join: `names` resolved
/// case-insensitively against both sides' column lists. `NATURAL` passes
/// `naturalCommonNames`' intersection (never missing); `USING` passes its
/// explicit list, and a listed column absent from either side is MySQL's
/// 1054 "Unknown column ... in 'from clause'".
and private namedEquiKeys (leftColumns: ColumnDef list) (rightColumns: ColumnDef list) (names: string list) : Result<(int * int) list, QueryResult> =
    let indexOf (cols: ColumnDef list) (name: string) =
        cols |> List.tryFindIndex (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase))

    names
    |> traverse (fun name ->
        match indexOf leftColumns name, indexOf rightColumns name with
        | Some li, Some ri -> Ok(li, ri)
        | _ -> Error(Err(1054, sprintf "Unknown column '%s' in 'from clause'" name)))

/// Synthesizes the equality conjunction a `NATURAL`/`USING` join's
/// equi-keys imply, for the nested-loop fallback — the hash path matches
/// key values directly and never evaluates `join.On` (which is the
/// always-true literal for these join kinds), so without this the fallback
/// would pair every row. `qualifierOfLeft` maps a left index to its
/// source's qualifier so the synthesized refs stay unambiguous.
and private namedJoinOn
    (leftColumns: ColumnDef list)
    (qualifierOfLeft: int -> string)
    (rightQualifier: string)
    (rightColumns: ColumnDef list)
    (equiKeys: (int * int) list)
    : Expr =
    equiKeys
    |> List.fold
        (fun acc (li, ri) ->
            let cond =
                BinOp(Eq, QualifiedCol(qualifierOfLeft li, leftColumns.[li].Name), QualifiedCol(rightQualifier, rightColumns.[ri].Name))

            match acc with
            | Lit(VInt 1L) -> cond
            | _ -> BinOp(And, acc, cond))
        (Lit(VInt 1L))

/// Applies one `JOIN` clause against whatever's already in scope
/// (`sourcesSoFar`/`rowsSoFar`, built by the `FROM` table and any earlier
/// `JOIN`s in the same list): resolves the joined table, matches (left row,
/// right row) pairs against `join.On`, then combines the matched pairs with
/// whatever `join.Kind` needs added on top — `LEFT`/`RIGHT` also keep the
/// side that matched nothing, `NULL`-padded on the other side; `INNER` and
/// `CROSS` (the latter's `On` is always the literal-true `join.On` the
/// parser gives it) keep only the matches. Indices (not row references)
/// track which left/right rows matched anything, so outer-join padding is
/// correct even if two rows happen to be structurally equal.
///
/// Matching itself is `extractEquiKeys`' choice: an `ON` with at least one
/// extractable `col = col` equi-key (and every key's columns hash-safe
/// together, `keyClassOf`) builds a `Dictionary` on whichever side has
/// fewer rows and probes with the other, applying any residual conjuncts
/// only to the (already key-equal) candidates a bucket lookup returns.
/// Anything else — no equi-key at all, an `OR`, a range, `a.x + 1 = b.y` —
/// falls back to a lazy nested loop over every pair instead: still
/// evaluates `join.On` for each one, but as a `seq` (`traverseSeq`) rather
/// than a materialized cross-product `list`, which is what lets a
/// non-equi join at real table sizes actually finish instead of exhausting
/// memory.
/// The per-key-column collations `JoinKeyComparer` folds under. Non-string
/// keys use the default only as an unused placeholder.
and private joinKeyCollation
    (left: ColumnDef)
    (right: ColumnDef)
    : Result<Collation.Collation, EvalError> =
    let colOf (column: ColumnDef) =
        if InformationSchema.isStringy column.Type then
            match column.Charset with
            | Some "binary" -> Collation.tryFind "utf8mb4_bin"
            | _ -> column.Collation |> Option.bind Collation.tryFind
        else
            None

    match colOf left, colOf right with
    | Some leftCollation, Some rightCollation ->
        resolveOperandCollation
            "="
            { Collation = leftCollation
              Coercibility = 2
              Charset = charsetOfCollation leftCollation }
            { Collation = rightCollation
              Coercibility = 2
              Charset = charsetOfCollation rightCollation }
    | Some collation, None
    | None, Some collation -> Ok collation
    | None, None -> Ok Collation.defaultCollation

and private joinKeyCollations
    (left: ColumnDef list)
    (right: ColumnDef list)
    (equiKeys: (int * int) list)
    : Collation.Collation list =
    equiKeys
    |> List.map (fun (li, ri) ->
        joinKeyCollation left.[li] right.[ri]
        |> Result.defaultValue Collation.defaultCollation)

and private joinKeyCollationsCompatible
    (left: ColumnDef list)
    (right: ColumnDef list)
    (equiKeys: (int * int) list)
    : bool =
    equiKeys
    |> List.forall (fun (li, ri) -> joinKeyCollation left.[li] right.[ri] |> Result.isOk)

/// A physical table can retain one immutable root for both an equality probe
/// and a scan fallback. Views, CTEs, and virtual relations have their own
/// resolution rules and stay on the ordinary path.
and private filterLockingReadTable (tableRef: TableRef) (table: Table) =
    let qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table |> fun value -> value.ToLowerInvariant()

    match currentLockingReadRows () |> Map.tryFind qualifier with
    | None -> table
    | Some allowed ->
        let rows = table.RowsArray.ToBuilder()

        for rowId, _ in table.RowsArray.Indexed do
            if not (Set.contains rowId allowed) then
                rows.Remove rowId |> ignore

        { table with RowsArray = rows.DrainToImmutable() }

and private tryPhysicalTableRef (store: Store) (dbName: string) (tableRef: TableRef) : Result<Table option, QueryResult> =
    let tableDb = tableRef.Database |> Option.defaultValue dbName
    let cteShadows = tableRef.Database.IsNone && (currentCteScope () |> Map.containsKey (tableRef.Table.ToLowerInvariant()))
    let isVirtual =
        System.String.Equals(tableDb, "fsdb", System.StringComparison.OrdinalIgnoreCase)
        && store.VirtualTables.ContainsKey(tableRef.Table.ToLowerInvariant())

    if
        cteShadows
        || isVirtual
        || System.String.Equals(tableRef.Table, "dual", System.StringComparison.OrdinalIgnoreCase)
        || System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase)
        || (tryStoredView store tableDb tableRef.Table).IsSome
    then
        Ok None
    else
        Storage.tableSnapshot store tableDb tableRef.Table
        |> Result.map (filterLockingReadTable tableRef)
        |> Result.map Some
        |> Result.mapError storageErr

and private sameIndexSemantics (left: ColumnDef) (right: ColumnDef) : bool =
    left.Type = right.Type
    &&
        (not (InformationSchema.isStringy left.Type)
         || (left.Charset = right.Charset && left.Collation = right.Collation && left.Collation.IsSome))

and private tryIndexedJoinProbe
    (store: Store)
    (join: Join)
    (leftColumns: ColumnDef list)
    (rightColumns: ColumnDef list)
    (physicalTable: Table option)
    (equiKeys: (int * int) list)
    : IndexedJoinProbe option =
    match join.Kind, join.Using, physicalTable, equiKeys with
    | (InnerJoin | NaturalJoin | LeftJoin | NaturalLeftJoin | RightJoin | NaturalRightJoin), _, Some table, _ :: _
        when storedValuesMatchReadValues store
             && (equiKeys |> List.forall (fun (leftIndex, rightIndex) -> sameIndexSemantics leftColumns.[leftIndex] rightColumns.[rightIndex])) ->
        let rightNames = equiKeys |> List.map (fun (_, rightIndex) -> rightColumns.[rightIndex].Name)

        Storage.tryEqualityIndexForColumns table rightNames
        |> Option.bind (fun index ->
            index.ColumnIndices
            |> traverse (fun rightIndex ->
                equiKeys
                |> List.tryPick (fun (leftIndex, candidate) -> if candidate = rightIndex then Some leftIndex else None)
                |> function Some leftIndex -> Ok leftIndex | None -> Error())
            |> Result.toOption
            |> Option.map (fun leftIndices ->
                { Table = table
                  Index = index
                  LeftIndices = leftIndices }))
    | _ -> None

/// Early split on the join target: `JSON_TABLE` is lateral (its source
/// re-evaluates per left row) and takes its own branch; everything else
/// resolves once up front in `applyResolvedJoin` — the pre-JSON_TABLE
/// `applyJoin` body, untouched.
and private applyJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    (sourceOverrides: JoinSourceOverrides)
    (state: (string * ColumnDef list) list * Value[] seq)
    (join: Join)
    : Result<(string * ColumnDef list) list * Value[] seq * string list, QueryResult> =
    match join.Table with
    | FromJsonTable(source, path, columns, alias) -> applyJsonTableJoin store registry dbName outer state join source path columns alias
    | FromLateral(body, alias) -> applyLateralJoin store registry dbName outer state join body alias
    | _ -> applyResolvedJoin store registry dbName outer sourceOverrides state join

/// `applyJoin`'s LATERAL branch — the derived table re-runs once per left
/// row, with that row (over the columns joined so far) as its outer context,
/// so its WHERE/ORDER BY/LIMIT see the left row's values. An `INNER`/comma
/// join drops a left row whose body produced nothing; a `LEFT JOIN ... ON
/// TRUE` pads it with NULLs instead, which is the whole point of the
/// spelling.
/// `USING`/`NATURAL` and RIGHT JOIN against a LATERAL body are
/// refused rather than silently run as something else — same policy as
/// `applyJsonTableJoin`'s.
and private applyLateralJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] seq)
    (join: Join)
    (body: SelectOrUnion)
    (alias: string)
    : Result<(string * ColumnDef list) list * Value[] seq * string list, QueryResult> =
    match join.Kind, join.Using with
    | (InnerJoin | CrossJoin | LeftJoin), [] ->
        let leftRows = rowsSoFar |> List.ofSeq
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftCtxFor = contextFactory store registry dbName (columnIndexOf combinedColumnsSoFar) (qualifierRanges sourcesSoFar) outer

        let runBody (leftRow: Value[] option) : Result<ColumnDef list * Value[] list, QueryResult> =
            let bodyOuter = leftRow |> Option.map leftCtxFor |> Option.orElse outer

            match body with
            | PlainSelect select ->
                match runSelectStmt store registry dbName select bodyOuter with
                | Err(code, message), _, _ -> Error(Err(code, message))
                | ResultSet(names, _), metadata, typedRows ->
                    Ok(deriveColumns names (names |> List.map (fun _ -> Collation.defaultCollation)) metadata, typedRows)
                | Affected _, _, _ -> Error(Err(1064, "a LATERAL derived table did not return a resultset"))
                | MultipleResults _, _, _ -> Error(nestedResultsError "a LATERAL derived table")
            | UnionSelect(first, rest, orderBy, limit, offset) ->
                match runUnionStmtWithOuter store registry dbName first rest orderBy limit offset bodyOuter with
                | Err(code, message), _, _ -> Error(Err(code, message))
                | ResultSet(names, _), metadata, typedRows ->
                    Ok(deriveColumns names (names |> List.map (fun _ -> Collation.defaultCollation)) metadata, typedRows)
                | Affected _, _, _ -> Error(Err(1064, "a LATERAL derived table did not return a resultset"))
                | MultipleResults _, _, _ -> Error(nestedResultsError "a LATERAL derived table")

        // The body still has to name its columns even when there is no left
        // row to run it against — a metadata-only pass with no outer context
        // supplies them (its correlated references read as NULL).
        let columnsProbe () =
            if leftRows.IsEmpty then
                runBody None |> Result.map fst
            else
                Ok []

        columnsProbe ()
        |> Result.bind (fun probeColumns ->
            leftRows
            |> traverse (fun leftRow ->
                runBody (Some leftRow)
                |> Result.bind (fun (bodyColumns, bodyRows) ->
                    let padding = bodyColumns |> List.map (fun _ -> VNull) |> Array.ofList

                    let expanded =
                        if bodyRows.IsEmpty && join.Kind = LeftJoin then
                            [ Array.append leftRow padding ]
                        else
                            bodyRows |> List.map (Array.append leftRow)

                    expanded
                    |> traverse (fun combined ->
                        let ctxFor =
                            contextFactory
                                store
                                registry
                                dbName
                                (columnIndexOf (combinedColumnsSoFar @ bodyColumns))
                                (qualifierRanges (sourcesSoFar @ [ alias, bodyColumns ]))
                                outer

                        evalExpr { ctxFor combined with Clause = OnClause } join.On
                        |> Result.map (fun v -> combined, truthy v = Some true))
                    |> Result.mapError Err
                    |> Result.map (fun checked' -> bodyColumns, checked' |> List.filter snd |> List.map fst)))
            |> Result.map (fun perLeftRow ->
                let bodyColumns =
                    perLeftRow |> List.tryPick (fun (cols, _) -> if List.isEmpty cols then None else Some cols)
                    |> Option.defaultValue probeColumns

                sourcesSoFar @ [ alias, bodyColumns ],
                (perLeftRow |> List.collect snd |> Seq.ofList),
                []))
    | _ ->
        Error(Err(1064, "LATERAL only supports comma-join, CROSS JOIN, [INNER] JOIN ... ON and LEFT JOIN ... ON"))

/// `applyJoin`'s JSON_TABLE branch — MySQL's lateral semantics: the source
/// expression re-evaluates against each left row (over the columns joined
/// so far, so `FROM t, JSON_TABLE(t.doc, ...) jt` sees `t`'s row), and each
/// document's expansion is appended to its own left row. FOR ORDINALITY
/// restarts per left row because each row is its own `jsonTableRows`
/// invocation. A NULL doc, a row path with no match, and a scalar under
/// `$[*]` all yield zero expansion rows; inner joins drop the left row and
/// left joins retain it with NULLs for the table-function columns.
and private applyJsonTableJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] seq)
    (join: Join)
    (source: Expr)
    (path: string)
    (columns: JsonTableColumn list)
    (alias: string)
    : Result<(string * ColumnDef list) list * Value[] seq * string list, QueryResult> =
    match join.Kind with
    | InnerJoin
    | CrossJoin
    | LeftJoin ->
        let joinColumns = jsonTableColumnDefs columns
        let newSources = sourcesSoFar @ [ alias, joinColumns ]
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftCtxFor = contextFactory store registry dbName (columnIndexOf combinedColumnsSoFar) (qualifierRanges sourcesSoFar) outer
        let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) (qualifierRanges newSources) outer

        namedEquiKeys combinedColumnsSoFar joinColumns join.Using
        |> Result.bind (fun usingKeys ->
            let leftKeyIndices = usingKeys |> List.map fst |> Array.ofList
            let rightKeyIndices = usingKeys |> List.map snd |> Array.ofList
            let keyComparer =
                SqlValueKeyComparer(joinKeyCollations combinedColumnsSoFar joinColumns usingKeys, true)
                :> System.Collections.Generic.IEqualityComparer<Value[]>

            let usingMatches (left: Value[]) (right: Value[]) =
                if usingKeys.IsEmpty then
                    true
                else
                    match
                        readEquiKeyOf store combinedColumnsSoFar leftKeyIndices left,
                        readEquiKeyOf store joinColumns rightKeyIndices right
                    with
                    | Some leftKey, Some rightKey -> keyComparer.Equals(leftKey, rightKey)
                    | _ -> false

            let rec qualifierInScope (ctx: EvalContext) (qualifier: string) =
                ctx.Qualifiers.ContainsKey(qualifier.ToLowerInvariant())
                || (ctx.Outer |> Option.exists (fun outerCtx -> qualifierInScope outerCtx qualifier))

            let expandLeft (left: Value[]) : Result<Value[] list, QueryResult> =
                let leftCtx = leftCtxFor left

                let sourceResult =
                    match evalExpr leftCtx source with
                    | Error(1054, message) ->
                        let missingQualifier = Regex.Match(message, @"^Unknown column '([^.']+)\.")

                        if missingQualifier.Success && not (qualifierInScope leftCtx missingQualifier.Groups.[1].Value) then
                            let qualifier = missingQualifier.Groups.[1].Value
                            Error(1109, sprintf "Unknown table '%s' in a table function argument" qualifier)
                        else
                            Error(1054, message)
                    | result -> result

                match sourceResult with
                | Error(code, message) -> Error(Err(code, message))
                | Ok doc ->
                    jsonTableRows doc path columns
                    |> Result.bind (fun jtRows ->
                        jtRows
                        |> List.filter (usingMatches left)
                        |> traverse (fun right ->
                            let combined = Array.append left right

                            evalExpr { ctxFor combined with Clause = OnClause } join.On
                            |> Result.map (fun value -> combined, truthy value = Some true))
                        |> Result.mapError Err
                        |> Result.map (fun checkedRows ->
                            let matches = checkedRows |> List.filter snd |> List.map fst

                            if matches.IsEmpty && join.Kind = LeftJoin then
                                [ Array.append left (Array.create joinColumns.Length VNull) ]
                            else
                                matches))

            rowsSoFar
            |> List.ofSeq
            |> traverse expandLeft
            |> Result.map (fun expanded -> newSources, (expanded |> List.concat |> Seq.ofList), join.Using))
    | _ ->
        Error(Err(1064, "JSON_TABLE only supports comma-join, CROSS JOIN, [INNER] JOIN ... ON, and LEFT JOIN ... ON"))

and private planJoinOrder (store: Store) (dbName: string) (select: SelectStmt) : Join list =
    let qualifier (source: FromItem) = fromItemQualifier source |> _.ToLowerInvariant()

    let references expression =
        Expression.collect
            (function
            | QualifiedCol(name, _) -> Some(name.ToLowerInvariant())
            | _ -> None)
            expression
        |> Set.ofList

    let hasUnqualifiedReference expression =
        Expression.exists
            (function
            | Col _
            | Star None -> true
            | _ -> false)
            expression

    let expressions =
        (select.Projections |> List.map fst)
        @ (select.Where |> Option.toList)
        @ select.GroupBy
        @ (select.Having |> Option.toList)
        @ (select.OrderBy |> List.map fst)
        @ (select.Joins |> List.map _.On)

    let eligible =
        not select.StraightJoin
        && expressions |> List.forall (hasUnqualifiedReference >> not)
        && select.Joins
           |> List.forall (fun join ->
               join.Kind = InnerJoin
               && join.Using.IsEmpty
               && match join.Table with FromTable _ -> true | _ -> false)

    match select.From, eligible with
    | Some(FromTable baseTable), true ->
        let baseQualifier = baseTable.Alias |> Option.defaultValue baseTable.Table |> _.ToLowerInvariant()

        let tableFor (join: Join) =
            match join.Table with
            | FromTable tableRef ->
                tryPhysicalTableRef store dbName tableRef
                |> Result.toOption
                |> Option.flatten
            | _ -> None

        let indexedByBoundColumn bound (join: Join) =
            let candidate = qualifier join.Table

            conjuncts join.On
            |> List.exists (function
                | BinOp(Eq, QualifiedCol(leftQualifier, leftColumn), QualifiedCol(rightQualifier, rightColumn)) ->
                    let leftQualifier = leftQualifier.ToLowerInvariant()
                    let rightQualifier = rightQualifier.ToLowerInvariant()

                    let candidateColumn =
                        if leftQualifier = candidate && bound |> Set.contains rightQualifier then Some leftColumn
                        elif rightQualifier = candidate && bound |> Set.contains leftQualifier then Some rightColumn
                        else None

                    candidateColumn
                    |> Option.bind (fun column ->
                        tableFor join
                        |> Option.bind (fun table -> Storage.tryEqualityIndexForColumns table [ column ]))
                    |> Option.isSome
                | _ -> false)

        let rec choose bound (planned: Join list) (remaining: Join list) =
            match remaining with
            | [] -> List.rev planned
            | _ ->
                let ready =
                    remaining
                    |> List.indexed
                    |> List.filter (fun (_, join) ->
                        references join.On
                        |> Set.forall (fun name -> name = qualifier join.Table || bound |> Set.contains name))

                match ready with
                | [] -> select.Joins
                | _ ->
                    let index, next =
                        ready
                        |> List.minBy (fun (originalIndex, join) ->
                            let rowCount = tableFor join |> Option.map (_.RowsArray.Count) |> Option.defaultValue System.Int32.MaxValue
                            (if indexedByBoundColumn bound join then 0 else 1), rowCount, originalIndex)

                    let remaining = remaining |> List.mapi (fun i join -> i, join) |> List.choose (fun (i, join) -> if i = index then None else Some join)
                    choose (Set.add (qualifier next.Table) bound) (next :: planned) remaining

        choose (Set.singleton baseQualifier) [] select.Joins
    | _ -> select.Joins

and private applyResolvedJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (outer: EvalContext option)
    (sourceOverrides: JoinSourceOverrides)
    ((sourcesSoFar, rowsSoFar): (string * ColumnDef list) list * Value[] seq)
    (join: Join)
    : Result<(string * ColumnDef list) list * Value[] seq * string list, QueryResult> =
    let joinSource =
        let qualifier = (fromItemQualifier join.Table).ToLowerInvariant()

        match Map.tryFind qualifier sourceOverrides with
        | Some source -> Ok(source.Columns, source.Rows, source.PhysicalTable)
        | None ->
            match join.Table with
            | FromTable tableRef ->
                tryPhysicalTableRef store dbName tableRef
                |> Result.bind (function
                    | Some table -> Ok(table.Columns, table.RowsArray :> Value[] seq, Some table)
                    | None ->
                        resolveFromItem store registry dbName join.Table
                        |> Result.map (fun (columns, rows) -> columns, rows :> Value[] seq, None))
            | _ ->
                resolveFromItem store registry dbName join.Table
                |> Result.map (fun (columns, rows) -> columns, rows :> Value[] seq, None)

    match joinSource with
    | Error e -> Error e
    | Ok(joinColumns, joinRows, physicalTable) ->
        let joinQualifier = fromItemQualifier join.Table
        let newSources = sourcesSoFar @ [ joinQualifier, joinColumns ]
        let qualifiers = qualifierRanges newSources
        let combinedColumnsSoFar = sourcesSoFar |> List.collect snd
        let leftNullPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
        let rightNullPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList

        // The column names this join coalesces (`SELECT *` shows them once):
        // `NATURAL`'s intersection, or `USING`'s explicit list — empty for a
        // plain `ON` join. Returned to `runSelectStmt` for its star/ref
        // rewrite.
        let coalesceNames =
            match join.Kind with
            | NaturalJoin
            | NaturalLeftJoin
            | NaturalRightJoin -> naturalCommonNames combinedColumnsSoFar joinColumns
            | _ -> join.Using

        let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers outer

        // Most join strategies need an indexed left side, but keep that
        // force lazy: an always-true inner/cross join below can compose its
        // Cartesian product as a seq, allowing a whole chain of such joins
        // to reach runSelect's LIMIT without materializing an earlier link.
        let leftIndexed = lazy (rowsSoFar |> List.ofSeq |> List.indexed)
        let resolveQualified (qualifier: string) (column: string) =
            qualifiers
            |> Map.tryFind (qualifier.ToLowerInvariant())
            |> Option.bind (fun (columns, offset) ->
                columns
                |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun index -> offset + index, columns.[index].Type))

        let buildCombinedRows (rightIndexed: (int * Value[]) list) (matched: (int * int * Value[]) list) =
            let matchedCombined = matched |> List.map (fun (_, _, c) -> c)

            let combinedRows =
                match join.Kind with
                | InnerJoin
                | CrossJoin
                | NaturalJoin -> matchedCombined
                | LeftJoin
                | NaturalLeftJoin ->
                    let matchesByLeft = matched |> List.groupBy (fun (li, _, _) -> li) |> Map.ofList

                    leftIndexed.Value
                    |> List.collect (fun (li, left) ->
                        matchesByLeft
                        |> Map.tryFind li
                        |> Option.map (List.sortBy (fun (_, ri, _) -> ri) >> List.map (fun (_, _, combined) -> combined))
                        |> Option.defaultValue [ Array.append left rightNullPadding ])
                | RightJoin
                | NaturalRightJoin ->
                    let matchesByRight = matched |> List.groupBy (fun (_, ri, _) -> ri) |> Map.ofList

                    rightIndexed
                    |> List.collect (fun (ri, right) ->
                        matchesByRight
                        |> Map.tryFind ri
                        |> Option.map (List.sortBy (fun (li, _, _) -> li) >> List.map (fun (_, _, combined) -> combined))
                        |> Option.defaultValue [ Array.append leftNullPadding right ])

            newSources, combinedRows

        // `NATURAL`/`USING` equi-keys come straight from the coalesced
        // names; a plain `ON` join keeps the expression-based extraction.
        // An empty name set (a `NATURAL` join with no common columns) falls
        // through to `extractEquiKeys` on the always-true `On`, which finds
        // no keys and drops to the nested loop — MySQL's Cartesian product.
        let equiKeysResult =
            if coalesceNames.IsEmpty then
                Ok(extractEquiKeys resolveQualified combinedColumnsSoFar.Length join.On)
            else
                namedEquiKeys combinedColumnsSoFar joinColumns coalesceNames |> Result.map (fun keys -> keys, [])

        match equiKeysResult with
        | Error e -> Error e
        | Ok(equiKeys, residualConjuncts) ->
            // For a named (NATURAL/USING) join the nested-loop fallback
            // must still enforce the equi-keys — `join.On` is the
            // always-true literal for these kinds, so synthesize the
            // conjunction the hash path matches directly.
            let effectiveOn =
                if coalesceNames.IsEmpty then
                    join.On
                else
                    let qualifierOfLeft idx =
                        let rec find offset =
                            function
                            | [] -> failwith "applyJoin: left column index out of range"
                            | (q, cols) :: rest ->
                                if idx < offset + List.length cols then q
                                else find (offset + List.length cols) rest

                        find 0 sourcesSoFar

                    namedJoinOn combinedColumnsSoFar qualifierOfLeft joinQualifier joinColumns equiKeys

            let keyClasses =
                equiKeys |> List.map (fun (li, ri) -> keyClassOf combinedColumnsSoFar.[li].Type joinColumns.[ri].Type)

            let keyCollations = joinKeyCollations combinedColumnsSoFar joinColumns equiKeys

            let residualHolds (combined: Value[]) : Result<bool, EvalError> =
                residualConjuncts
                |> traverse (fun c -> evalExpr { ctxFor combined with Clause = OnClause } c)
                |> Result.map (List.forall (fun v -> truthy v = Some true))

            let indexedJoinProbe = tryIndexedJoinProbe store join combinedColumnsSoFar joinColumns physicalTable equiKeys

            let hashEligible =
                storedValuesMatchReadValues store
                && indexedJoinProbe.IsNone
                && not equiKeys.IsEmpty
                && joinKeyCollationsCompatible combinedColumnsSoFar joinColumns equiKeys
                && keyClasses |> List.forall Option.isSome
                && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map fst) (leftIndexed.Value |> Seq.map snd)
                && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map snd) joinRows

            let isConstantTrue =
                function
                | Lit value -> truthy value = Some true
                | BinOp(Eq, Lit left, Lit right) -> Value.equals left right = Some true
                | _ -> false

            match indexedJoinProbe with
            | Some probe ->
                let exactKey =
                    probe.Index.PrefixLengths |> List.forall Option.isNone
                    && probe.Index.Transforms |> List.forall Option.isNone

                let candidateHolds combined =
                    if exactKey then
                        residualHolds combined
                    else
                        evalExpr { ctxFor combined with Clause = OnClause } effectiveOn
                        |> Result.map (truthy >> (=) (Some true))

                let rightRowsFor (left: Value[]) =
                    probe.LeftIndices
                    |> List.map (fun leftIndex -> left.[leftIndex])
                    |> Storage.tryEqualityLookupForIndex store probe.Table probe.Index
                    |> Option.map Seq.ofList
                    |> Option.defaultValue probe.Table.RowsArray.Indexed

                match join.Kind, exactKey, residualConjuncts with
                | (InnerJoin | NaturalJoin), true, [] ->
                    let candidates =
                        seq {
                            for left in rowsSoFar do
                                for _, right in rightRowsFor left do
                                    yield Array.append left right
                        }

                    Ok(newSources, candidates, coalesceNames)
                | (InnerJoin | NaturalJoin), _, _ ->
                    seq {
                        for left in rowsSoFar do
                            for _, right in rightRowsFor left do
                                yield Array.append left right
                    }
                    |> traverseSeqWithLimit
                        (Some(
                            maxJoinCandidateRows,
                            (1105, sprintf "Join exceeds the %d-row candidate limit" maxJoinCandidateRows)
                        ))
                        (fun combined -> candidateHolds combined |> Result.map (fun matches -> if matches then Some combined else None))
                    |> Result.mapError Err
                    |> Result.map (fun matched -> newSources, matched :> Value[] seq, coalesceNames)
                | (LeftJoin | NaturalLeftJoin), true, [] ->
                    let candidates =
                        seq {
                            for left in rowsSoFar do
                                let mutable matched = false

                                for _, right in rightRowsFor left do
                                    matched <- true
                                    yield Array.append left right

                                if not matched then
                                    yield Array.append left rightNullPadding
                        }

                    Ok(newSources, candidates, coalesceNames)
                | (LeftJoin | NaturalLeftJoin), _, _ ->
                    seq {
                        for leftIndex, left in leftIndexed.Value do
                            for rightIndex, (_, right) in rightRowsFor left |> Seq.indexed do
                                yield leftIndex, rightIndex, Array.append left right
                    }
                    |> traverseSeqWithLimit
                        (Some(
                            maxJoinCandidateRows,
                            (1105, sprintf "Join exceeds the %d-row candidate limit" maxJoinCandidateRows)
                        ))
                        (fun ((_, _, combined) as candidate) ->
                            candidateHolds combined
                            |> Result.map (fun matches -> if matches then Some candidate else None))
                    |> Result.mapError Err
                    |> Result.map (buildCombinedRows [] >> fun (joinedSources, rows) -> joinedSources, rows :> Value[] seq, coalesceNames)
                | (RightJoin | NaturalRightJoin), _, _ ->
                    let positionedRight = probe.Table.RowsArray.Indexed |> Seq.indexed |> List.ofSeq
                    let rightPositions = positionedRight |> List.map (fun (index, (rowId, _)) -> rowId, index) |> Map.ofList
                    let rightIndexed = positionedRight |> List.map (fun (index, (_, row)) -> index, row)

                    seq {
                        for leftIndex, left in leftIndexed.Value do
                            for rowId, right in rightRowsFor left do
                                match Map.tryFind rowId rightPositions with
                                | Some rightIndex -> yield leftIndex, rightIndex, Array.append left right
                                | None -> ()
                    }
                    |> traverseSeqWithLimit
                        (Some(
                            maxJoinCandidateRows,
                            (1105, sprintf "Join exceeds the %d-row candidate limit" maxJoinCandidateRows)
                        ))
                        (fun ((_, _, combined) as candidate) ->
                            candidateHolds combined
                            |> Result.map (fun matches -> if matches then Some candidate else None))
                    |> Result.mapError Err
                    |> Result.map (buildCombinedRows rightIndexed >> fun (joinedSources, rows) -> joinedSources, rows :> Value[] seq, coalesceNames)
                | _ -> failwith "indexed join kind"
            | None when hashEligible ->
                let leftKeyIndices = equiKeys |> List.map fst |> Array.ofList
                let rightKeyIndices = equiKeys |> List.map snd |> Array.ofList
                let rightCount = joinRows |> Seq.length
                let buildOnLeft = leftIndexed.Value.Length <= rightCount

                match join.Kind, residualConjuncts with
                | (InnerJoin | CrossJoin | NaturalJoin), [] ->
                    // Nothing here needs to see every match up front: `INNER`/
                    // `CROSS`/`NATURAL` keep only matched pairs (no
                    // unmatched-side padding to compute, unlike `LEFT`/
                    // `RIGHT`), and an empty residual means every hash-bucket
                    // hit is already a real match (no `ON`-conjunct re-check
                    // that could itself fail past the point a caller stops
                    // pulling). So this is `hashPairs`' lazy `seq` straight
                    // through, `Array.append`-combined but not collected —
                    // `runSelect`'s `WHERE`/`LIMIT` streaming decides how
                    // much of it ever actually runs.
                    let combined : Value[] seq =
                        if buildOnLeft then
                            hashPairs keyCollations (equiKeyOf leftKeyIndices) (equiKeyOf rightKeyIndices) leftIndexed.Value (joinRows |> Seq.indexed)
                            |> Seq.map (fun (_, l, _, r) -> Array.append l r)
                        else
                            hashPairs keyCollations (equiKeyOf rightKeyIndices) (equiKeyOf leftKeyIndices) (joinRows |> Seq.indexed |> List.ofSeq) leftIndexed.Value
                            |> Seq.map (fun (_, r, _, l) -> Array.append l r)

                    Ok(newSources, combined, coalesceNames)
                | _ ->
                    let rightIndexed = joinRows |> Seq.indexed |> List.ofSeq

                    let candidates : (int * int * Value[]) seq =
                        if buildOnLeft then
                            hashPairs keyCollations (equiKeyOf leftKeyIndices) (equiKeyOf rightKeyIndices) leftIndexed.Value rightIndexed
                            |> Seq.map (fun (li, l, ri, r) -> li, ri, Array.append l r)
                        else
                            hashPairs keyCollations (equiKeyOf rightKeyIndices) (equiKeyOf leftKeyIndices) rightIndexed leftIndexed.Value
                            |> Seq.map (fun (ri, r, li, l) -> li, ri, Array.append l r)

                    candidates
                    |> traverseSeqWithLimit
                        (Some(
                            maxJoinCandidateRows,
                            (1105, sprintf "Join exceeds the %d-row candidate limit" maxJoinCandidateRows)
                        ))
                        (fun ((_, _, combined) as candidate) ->
                            residualHolds combined
                            |> Result.map (fun matches -> if matches then Some candidate else None))
                    |> Result.mapError Err
                    |> Result.map (buildCombinedRows rightIndexed >> fun (s, r) -> s, r :> Value[] seq, coalesceNames)
            | None ->
                let rightIndexed = joinRows |> Seq.indexed |> List.ofSeq

                match join.Kind, isConstantTrue effectiveOn with
                | (InnerJoin | CrossJoin | NaturalJoin), true ->
                    let combined =
                        seq {
                            for left in rowsSoFar do
                                for _, right in rightIndexed do
                                    yield Array.append left right
                        }

                    Ok(newSources, combined, coalesceNames)
                | _ ->
                    let pairs = seq { for li, l in leftIndexed.Value do for ri, r in rightIndexed -> li, ri, l, r }

                    pairs
                    |> traverseSeqWithLimit
                        (Some(
                            maxJoinCandidateRows,
                            (1105, sprintf "Join exceeds the %d-row candidate limit" maxJoinCandidateRows)
                        ))
                        (fun (li, ri, l, r) ->
                            let combined = Array.append l r

                            evalExpr { ctxFor combined with Clause = OnClause } effectiveOn
                            |> Result.map (fun v -> if truthy v = Some true then Some(li, ri, combined) else None))
                    |> Result.mapError Err
                    |> Result.map (buildCombinedRows rightIndexed >> fun (s, r) -> s, r :> Value[] seq, coalesceNames)

/// Like `applyJoin`, but for a multi-table `UPDATE`/`DELETE ... JOIN`
/// rather than a `SELECT`: alongside the flattened row `evalExpr` needs,
/// each combined row also keeps every source's own physical `Value[]`
/// (`None` on an outer-join side that matched nothing — there's no real row
/// there to update/delete). A separate, smaller near-duplicate of
/// `applyJoin`'s equi-key hash-join/lazy-nested-loop split (see its doc)
/// rather than threading identity through the shared `SELECT` path. A
/// derived source contributes columns and values but no physical row
/// identity, so it can filter target rows without becoming writable.
and private applyMutationJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    ((sourcesSoFar, rowsSoFar): MutationSource list * (Value[] option list * Value[]) list)
    (join: Join)
    : Result<MutationSource list * (Value[] option list * Value[]) list, QueryResult> =
    match join.Table with
    | FromLateral _ ->
        Error(Err(1064, "a lateral derived table isn't supported as a multi-table UPDATE/DELETE JOIN source"))
    | FromJsonTable _ ->
        // MySQL allows a JSON_TABLE join source in multi-table
        // UPDATE/DELETE; its lateral row expansion does not yet preserve the
        // source identity list used by physical mutation targets.
        Error(Err(1064, "JSON_TABLE isn't supported as a multi-table UPDATE/DELETE JOIN source"))
    | source ->
        let resolved =
            match source with
            | FromTable tableRef ->
                resolveTableRef store registry dbName tableRef
                |> Result.map (fun (columns, rows) -> fromItemQualifier source, Some tableRef, columns, rows)
            | FromSubquery _ ->
                resolveFromSubquery store registry dbName source None
                |> Result.map (fun (columns, rows) -> fromItemQualifier source, None, columns, rows)
            | FromLateral _
            | FromJsonTable _ -> failwith "applyMutationJoin: source handled above"

        match resolved with
        | Error e -> Error e
        | Ok(joinQualifier, tableRef, joinColumns, joinRows) ->
            let newSources = sourcesSoFar @ [ { Qualifier = joinQualifier; PhysicalTable = tableRef; Columns = joinColumns } ]
            let qualifiers = qualifierRanges (newSources |> List.map (fun source -> source.Qualifier, source.Columns))
            let combinedColumnsSoFar = sourcesSoFar |> List.collect _.Columns
            let leftFlatPadding = combinedColumnsSoFar |> List.map (fun _ -> VNull) |> Array.ofList
            let rightFlatPadding = joinColumns |> List.map (fun _ -> VNull) |> Array.ofList
            let leftIdentityPadding = sourcesSoFar |> List.map (fun _ -> None)

            let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumnsSoFar @ joinColumns)) qualifiers None

            let leftIndexed = rowsSoFar |> List.indexed
            let rightIndexed = joinRows |> List.indexed
            let leftFlatRows = rowsSoFar |> List.map snd

            let resolveQualified (qualifier: string) (column: string) =
                qualifiers
                |> Map.tryFind (qualifier.ToLowerInvariant())
                |> Option.bind (fun (columns, offset) ->
                    columns
                    |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun index -> offset + index, columns.[index].Type))

            let buildCombinedRows (matched: (int * int * (Value[] option list * Value[])) list) =
                let matchedRows = matched |> List.map (fun (_, _, row) -> row)
                let matchedLeft = matched |> List.map (fun (li, _, _) -> li) |> Set.ofList
                let matchedRight = matched |> List.map (fun (_, ri, _) -> ri) |> Set.ofList

                let leftOnly =
                    leftIndexed
                    |> List.filter (fst >> matchedLeft.Contains >> not)
                    |> List.map (fun (_, (lIdent, lFlat)) -> lIdent @ [ None ], Array.append lFlat rightFlatPadding)

                let rightOnly =
                    rightIndexed
                    |> List.filter (fst >> matchedRight.Contains >> not)
                    |> List.map (fun (_, r) -> leftIdentityPadding @ [ tableRef |> Option.map (fun _ -> r) ], Array.append leftFlatPadding r)

                let rows =
                    match join.Kind with
                    | InnerJoin
                    | CrossJoin
                    | NaturalJoin -> matchedRows
                    | LeftJoin
                    | NaturalLeftJoin -> matchedRows @ leftOnly
                    | RightJoin
                    | NaturalRightJoin -> matchedRows @ rightOnly

                newSources, rows

            let coalesceNames =
                match join.Kind with
                | NaturalJoin
                | NaturalLeftJoin
                | NaturalRightJoin -> naturalCommonNames combinedColumnsSoFar joinColumns
                | _ -> join.Using

            let equiKeysResult =
                if coalesceNames.IsEmpty then
                    Ok(extractEquiKeys resolveQualified combinedColumnsSoFar.Length join.On)
                else
                    namedEquiKeys combinedColumnsSoFar joinColumns coalesceNames |> Result.map (fun keys -> keys, [])

            match equiKeysResult with
            | Error e -> Error e
            | Ok(equiKeys, residualConjuncts) ->
                // For a named (NATURAL/USING) join the nested-loop fallback
                // must still enforce the equi-keys — `join.On` is the
                // always-true literal for these kinds, so synthesize the
                // conjunction the hash path matches directly.
                let effectiveOn =
                    if coalesceNames.IsEmpty then
                        join.On
                    else
                        let qualifierOfLeft idx =
                            let rec find offset =
                                function
                                | [] -> failwith "applyMutationJoin: left column index out of range"
                                | (source: MutationSource) :: rest ->
                                    if idx < offset + List.length source.Columns then source.Qualifier
                                    else find (offset + List.length source.Columns) rest

                            find 0 sourcesSoFar

                        namedJoinOn combinedColumnsSoFar qualifierOfLeft joinQualifier joinColumns equiKeys

                let keyClasses =
                    equiKeys |> List.map (fun (li, ri) -> keyClassOf combinedColumnsSoFar.[li].Type joinColumns.[ri].Type)

                let keyCollations = joinKeyCollations combinedColumnsSoFar joinColumns equiKeys

                let hashEligible =
                    storedValuesMatchReadValues store
                    && not equiKeys.IsEmpty
                    && joinKeyCollationsCompatible combinedColumnsSoFar joinColumns equiKeys
                    && keyClasses |> List.forall Option.isSome
                    && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map fst) leftFlatRows
                    && rowsMatchKeyClasses (keyClasses |> List.map Option.get) (equiKeys |> List.map snd) joinRows

                let residualHolds (combinedFlat: Value[]) : Result<bool, EvalError> =
                    residualConjuncts
                    |> traverse (fun c -> evalExpr { ctxFor combinedFlat with Clause = OnClause } c)
                    |> Result.map (List.forall (fun v -> truthy v = Some true))

                if hashEligible then
                    let leftKeyIndices = equiKeys |> List.map fst |> Array.ofList
                    let rightKeyIndices = equiKeys |> List.map snd |> Array.ofList
                    let buildOnLeft = rowsSoFar.Length <= joinRows.Length

                    let rightIdentity row = tableRef |> Option.map (fun _ -> row)

                    let leftKeyOf (lIdent: Value[] option list, lFlat: Value[]) = equiKeyOf leftKeyIndices lFlat
                    let rightKeyOf (r: Value[]) = equiKeyOf rightKeyIndices r

                    let candidates : (int * int * (Value[] option list * Value[])) list =
                        if buildOnLeft then
                            hashPairs keyCollations leftKeyOf rightKeyOf leftIndexed rightIndexed
                            |> Seq.map (fun (li, (lIdent, lFlat), ri, r) -> li, ri, (lIdent @ [ rightIdentity r ], Array.append lFlat r))
                            |> List.ofSeq
                        else
                            hashPairs keyCollations rightKeyOf leftKeyOf rightIndexed leftIndexed
                            |> Seq.map (fun (ri, r, li, (lIdent, lFlat)) -> li, ri, (lIdent @ [ rightIdentity r ], Array.append lFlat r))
                            |> List.ofSeq

                    candidates |> keepMatches residualHolds snd |> Result.map buildCombinedRows
                else
                    let pairs = seq { for li, l in leftIndexed do for ri, r in rightIndexed -> li, ri, l, r }

                    pairs
                    |> traverseSeq (fun (li, ri, (lIdent, lFlat), r) ->
                        let combinedFlat = Array.append lFlat r

                        evalExpr { ctxFor combinedFlat with Clause = OnClause } effectiveOn
                        |> Result.map (fun v -> if truthy v = Some true then Some(li, ri, (lIdent @ [ tableRef |> Option.map (fun _ -> r) ], combinedFlat)) else None))
                    |> Result.mapError Err
                    |> Result.map buildCombinedRows

/// Resolves `from :: joins` into the same `(sources, rows)` shape
/// `applyMutationJoin` builds up — the multi-table `UPDATE`/`DELETE`
/// counterpart to `runSelectStmt`'s `FROM`/`JOIN` resolution.
and private runMutationJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (from: TableRef)
    (joins: Join list)
    : Result<MutationSource list * (Value[] option list * Value[]) list, QueryResult> =
    match resolveTableRef store registry dbName from with
    | Error e -> Error e
    | Ok(cols, rows) ->
        let baseQualifier = from.Alias |> Option.defaultValue from.Table
        let initial =
            [ { Qualifier = baseQualifier
                PhysicalTable = Some from
                Columns = cols } ],
            (rows |> List.map (fun row -> [ Some row ], row))
        joins |> List.fold (fun acc j -> acc |> Result.bind (fun st -> applyMutationJoin store registry dbName st j)) (Ok initial)

/// Resolves a `SELECT`'s `FROM` (a real table, `information_schema`'s
/// virtual one, a derived table, or none) plus every `JOIN` after it, and
/// runs `select` against the combined result — the `Statement` case's
/// `Select` branch and every subquery form (`Exists`/`Subquery`/
/// `InSubquery`/quantified comparisons/a derived table's own `FROM`) all fund into this one place
/// rather than each re-implementing the join-materialization logic.
/// `outer` is `None` for a top-level statement and `Some` when this is
/// itself a subquery — see `EvalContext.Outer`. Its per-column MySQL wire
/// metadata (see `columnMetadataOf`) is only available here —
/// by the time a `SELECT`'s rows reach `execute`'s return value they're
/// already the wire's flat `string option list` text, with no `Value`
/// left to read a type off of — but this can't be `public` itself (`outer:
/// EvalContext option` would leak the `private` `EvalContext` type through
/// a public signature); `runTopLevelSelect` near `execute` below is the
/// type-preserving public entry point `QueryHandler` calls instead.
/// Replaces every unqualified `Col` whose (case-insensitive) name is in
/// `map` with `map`'s entry — the COALESCE expression a `NATURAL`/`USING`
/// join synthesized for that common column, so WHERE/ORDER BY/GROUP
/// BY/HAVING see MySQL's coalesced value instead of a 1052-ambiguous
/// physical column. Subquery bodies (`Exists`/`Subquery`) are their own
/// scope — their own `runSelectStmt` applies their own rewrite, and walking
/// into them here could capture an inner column with an outer coalesce.
and private rewriteCoalescedCols (map: Map<string, Expr>) (expr: Expr) : Expr =
    let sub = rewriteCoalescedCols map

    match expr with
    | Placeholder _ -> expr
    | MatchAgainst(cols, q, mode) -> MatchAgainst(cols, sub q, mode)
    | Col name ->
        match Map.tryFind (name.ToLowerInvariant()) map with
        | Some repl -> repl
        | None -> expr
    | QualifiedCol _
    | Lit _
    | UserVariable _
    | SystemVariable _
    | Star _
    | Exists _
    | Subquery _
    | WindowOver _ -> expr
    | Row values -> Row(values |> List.map sub)
    | BinOp(op, a, b) -> BinOp(op, sub a, sub b)
    | AssignUserVariable(name, value) -> AssignUserVariable(name, sub value)
    | Not e -> Not(sub e)
    | IsNull e -> IsNull(sub e)
    | IsNotNull e -> IsNotNull(sub e)
    | IsTrue e -> IsTrue(sub e)
    | IsFalse e -> IsFalse(sub e)
    | Like(e, p, cs, esc) -> Like(sub e, sub p, cs, esc)
    | Regexp(e, p) -> Regexp(sub e, sub p)
    | In(e, xs) -> In(sub e, xs |> List.map sub)
    | InSubquery(e, s) -> InSubquery(sub e, s)
    | QuantifiedComparison(e, op, quantifier, s) -> QuantifiedComparison(sub e, op, quantifier, s)
    | Between(e, lo, hi) -> Between(sub e, sub lo, sub hi)
    | FuncCall(name, args) -> FuncCall(name, args |> List.map sub)
    | Distinct e -> Distinct(sub e)
    | OrderBy(e, dir) -> OrderBy(sub e, dir)
    | Cast(e, ty) -> Cast(sub e, ty)
    | Collate(e, name) -> Collate(sub e, name)
    | Case(subject, whens, elseBranch) ->
        Case(subject |> Option.map sub, whens |> List.map (fun (c, r) -> sub c, sub r), elseBranch |> Option.map sub)

/// Rewrites a select whose joins coalesce columns (`NATURAL`/`USING`) into
/// MySQL's exact shape: `SELECT *` expands to the coalesced common columns
/// first (`COALESCE` of every source occurrence, left to right), then the
/// left side's remaining columns, then the right's — except `RIGHT` joins,
/// which put the right side's remaining columns before the left's (verified
/// against MySQL 8.4). Chained joins fold left-to-right, each coalescing
/// join moving its new commons to the front. Unqualified `Col` references
/// to a coalesced name become the same COALESCE expression.
///
/// `sources` is the resolved `(qualifier, columns)` per source in FROM
/// order (one more entry than `joins`); `namesPerJoin` is `applyJoin`'s
/// coalesced-name list per join, same order.
and private rewriteNaturalSelect
    (select: SelectStmt)
    (sources: (string * ColumnDef list) list)
    (joins: Join list)
    (namesPerJoin: string list list)
    : SelectStmt =
    let qualified (qualifier: string) (name: string) = QualifiedCol(qualifier, name)

    let baseQualifier, baseColumns = List.head sources

    // The ordered logical column plan: (output name, expr).
    let plan =
        (List.zip joins namesPerJoin, List.tail sources)
        ||> List.fold2
                (fun plan (join, names) rightSource ->
                    let rightQualifier, rightCols = rightSource

                    if names.IsEmpty then
                        plan @ (rightCols |> List.map (fun c -> c.Name, qualified rightQualifier c.Name))
                    else
                        let common = names |> List.map (fun n -> n.ToLowerInvariant()) |> Set.ofList
                        let isCommon ((name: string), _) = common.Contains(name.ToLowerInvariant())

                        let commons =
                            plan
                            |> List.filter isCommon
                            |> List.map (fun (name, expr) -> name, FuncCall("COALESCE", [ expr; qualified rightQualifier name ]))

                        let leftRest = plan |> List.filter (isCommon >> not)

                        let rightRest =
                            rightCols
                            |> List.filter (fun c -> not (common.Contains(c.Name.ToLowerInvariant())))
                            |> List.map (fun c -> c.Name, qualified rightQualifier c.Name)

                        match join.Kind with
                        | RightJoin
                        | NaturalRightJoin -> commons @ rightRest @ leftRest
                        | _ -> commons @ leftRest @ rightRest)
                (baseColumns |> List.map (fun c -> c.Name, qualified baseQualifier c.Name))

    // Unqualified refs to a coalesced name resolve to the COALESCE over
    // every source occurrence of that name (a name re-added by a
    // later plain join with the same column would be silently included —
    // MySQL errors 1052 there; ORM-shaped queries never hit it).
    let coalesceMap =
        namesPerJoin
        |> List.concat
        |> List.distinctBy (fun n -> n.ToLowerInvariant())
        |> List.map (fun name ->
            let occurrences =
                sources
                |> List.filter (fun (_, cols) ->
                    cols |> List.exists (fun c -> System.String.Equals(c.Name, name, System.StringComparison.OrdinalIgnoreCase)))
                |> List.map (fun (q, _) -> qualified q name)

            name.ToLowerInvariant(), FuncCall("COALESCE", occurrences))
        |> Map.ofList

    let rewriteExpr e = rewriteCoalescedCols coalesceMap e

    let projections =
        select.Projections
        |> List.collect (fun (expr, alias) ->
            match expr with
            | Star None -> plan |> List.map (fun (name, e) -> e, Some name)
            | Star(Some _) -> [ expr, alias ]
            // A bare `SELECT tenant_id` over a USING join is still labelled
            // `tenant_id` by MySQL, not by the COALESCE this rewrite puts in
            // its place — pin the original name so the label survives.
            | Col name when alias.IsNone -> [ rewriteExpr expr, Some name ]
            | _ -> [ rewriteExpr expr, alias ])

    { select with
        Projections = projections
        Where = select.Where |> Option.map rewriteExpr
        GroupBy = select.GroupBy |> List.map rewriteExpr
        Having = select.Having |> Option.map rewriteExpr
        OrderBy = select.OrderBy |> List.map (fun (e, d) -> rewriteExpr e, d) }

/// Materializes every `WITH` binding in order (each one seeing the ones
/// before it), runs `body` with them in scope, then restores the scope the
/// caller had. Materializing up front rather than re-running the body per
/// reference matches MySQL's own default for a CTE used more than once, and
/// is what makes a recursive CTE expressible at all.
and private selectOrUnionTableNames (body: SelectOrUnion) : Set<string> =
    let rec expressionNames expression =
        Expression.fold
            (fun names node ->
                let nested =
                    Expression.subqueries node
                    |> List.fold (fun found query -> Set.union found (selectNames query)) Set.empty

                Expression.Descend(Set.union names nested))
            Set.empty
            expression

    and fromNames =
        function
        | FromTable table when table.Database.IsNone -> Set.singleton (table.Table.ToLowerInvariant())
        | FromTable _ -> Set.empty
        | FromSubquery(query, _)
        | FromLateral(query, _) -> bodyNames query
        | FromJsonTable(source, _, _, _) -> expressionNames source

    and selectNames (select: SelectStmt) =
        let expressions =
            (select.Projections |> List.map fst)
            @ (select.Joins |> List.map _.On)
            @ Option.toList select.Where
            @ select.GroupBy
            @ Option.toList select.Having
            @ (select.OrderBy |> List.map fst)
            @ (select.Windows
               |> List.collect (fun (_, spec) -> Expression.overExpressions (OverSpec spec)))
            @ Option.toList select.Limit
            @ Option.toList select.Offset

        let sourceNames =
            Option.toList select.From @ (select.Joins |> List.map _.Table)
            |> List.fold (fun names source -> Set.union names (fromNames source)) Set.empty

        let nestedCteNames =
            select.Ctes
            |> List.fold (fun names cte -> Set.union names (bodyNames cte.Body)) Set.empty

        expressions
        |> List.fold (fun names expression -> Set.union names (expressionNames expression)) sourceNames
        |> Set.union nestedCteNames

    and bodyNames (query: SelectOrUnion) =
        match query with
        | PlainSelect select -> selectNames select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            let branchNames =
                first :: (rest |> List.map snd)
                |> List.fold (fun names select -> Set.union names (selectNames select)) Set.empty

            (orderBy |> List.map fst) @ Option.toList limit @ Option.toList offset
            |> List.fold (fun names expression -> Set.union names (expressionNames expression)) branchNames

    bodyNames body

and private referencedCtes (ctes: CommonTableExpr list) (tableNames: Set<string>) =
    let names = System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)

    if ctes |> List.exists (fun cte -> not (names.Add cte.CteName)) then
        ctes
    else
        let byName = ctes |> List.map (fun cte -> cte.CteName.ToLowerInvariant(), cte) |> Map.ofList

        let rec close required pending =
            match Set.toList pending with
            | [] -> required
            | name :: _ ->
                let pending = Set.remove name pending

                match Map.tryFind name byName with
                | None -> close required pending
                | Some cte when Set.contains name required -> close required pending
                | Some cte ->
                    let dependencies = selectOrUnionTableNames cte.Body
                    close (Set.add name required) (Set.union pending dependencies)

        let required = close Set.empty tableNames
        ctes |> List.filter (fun cte -> Set.contains (cte.CteName.ToLowerInvariant()) required)

and private referencedMutationCtes (ctes: CommonTableExpr list) (joins: Join list) (expressions: Expr list) =
    let query: SelectStmt =
        { Projections = expressions |> List.map (fun expression -> expression, None)
          IntoVariables = []
          Distinct = false
          CalculateFoundRows = false
          StraightJoin = false
          From = None
          Joins = joins
          Where = None
          GroupBy = []
          Rollup = false
          Windows = []
          Ctes = []
          Having = None
          OrderBy = []
          Limit = None
          Offset = None
          Locking = [] }

    referencedCtes ctes (selectOrUnionTableNames (PlainSelect query))

and private tryMergeDirectView
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    : Result<SelectStmt option, QueryResult> =
    let rec mergeablePredicate =
        function
        | Col _
        | Lit _ -> true
        | BinOp(_, left, right)
        | Like(left, right, _, _)
        | Regexp(left, right) -> mergeablePredicate left && mergeablePredicate right
        | Not value
        | IsNull value
        | IsNotNull value
        | IsTrue value
        | IsFalse value
        | Distinct value
        | OrderBy(value, _)
        | Cast(value, _)
        | Collate(value, _) -> mergeablePredicate value
        | In(value, candidates) -> mergeablePredicate value && candidates |> List.forall mergeablePredicate
        | Between(value, lower, upper) ->
            mergeablePredicate value && mergeablePredicate lower && mergeablePredicate upper
        | Case(subject, branches, otherwise) ->
            subject |> Option.forall mergeablePredicate
            && branches |> List.forall (fun (condition, result) -> mergeablePredicate condition && mergeablePredicate result)
            && otherwise |> Option.forall mergeablePredicate
        | _ -> false

    match select.From, select.Joins with
    | Some(FromTable viewRef), [] ->
        let viewDb = viewRef.Database |> Option.defaultValue dbName

        match tryStoredView store viewDb viewRef.Table with
        | None -> Ok None
        | Some view ->
            match Parser.parse view.Definition with
            | Ok((Select definition) as statement) ->
                match updatableViewOfSelect store view definition with
                | Some direct
                    when direct.Predicate |> Option.forall mergeablePredicate
                         && direct.UpdateJoins.IsEmpty
                         && (direct.OrderedColumns
                             |> List.forall (fun column -> Map.containsKey (column.ToLowerInvariant()) direct.Columns)) ->
                    let source =
                        { Database = Some direct.Database
                          Table = direct.Table
                          Alias = None
                          Partitions = [] }

                    match tryPhysicalTableRef store direct.Database source with
                    | Error _
                    | Ok None -> Ok None
                    | Ok(Some _) ->
                        match
                            registryForViewSecurity
                                store
                                registry
                                direct.SecurityType
                                direct.Definer
                                direct.ViewDatabase
                                statement
                        with
                        | Error(code, message) -> Error(Err(code, message))
                        | Ok _ ->
                            let viewQualifier = viewRef.Alias |> Option.defaultValue viewRef.Table
                            let mergedSource = { source with Alias = Some viewQualifier }
                            let outputColumns =
                                direct.OrderedColumns
                                |> List.map (fun output -> output, direct.Columns.[output.ToLowerInvariant()])

                            let rewriteOuter expression =
                                Expression.rewrite
                                    (function
                                    | Col name ->
                                        direct.Columns
                                        |> Map.tryFind (name.ToLowerInvariant())
                                        |> Option.map (fun column -> QualifiedCol(viewQualifier, column))
                                        |> Option.orElseWith (fun () -> Some(QualifiedCol("__fsdb_view", name)))
                                    | QualifiedCol(qualifier, name)
                                        when qualifier.Equals(viewQualifier, System.StringComparison.OrdinalIgnoreCase) ->
                                        direct.Columns
                                        |> Map.tryFind (name.ToLowerInvariant())
                                        |> Option.map (fun column -> QualifiedCol(viewQualifier, column))
                                        |> Option.orElseWith (fun () -> Some(QualifiedCol("__fsdb_view", name)))
                                    | _ -> None)
                                    expression

                            let projections =
                                select.Projections
                                |> List.collect (fun (expression, alias) ->
                                    let directProjection name =
                                        let rewritten = rewriteOuter expression
                                        let inferredAlias =
                                            match rewritten with
                                            | QualifiedCol(_, column)
                                                when not (column.Equals(name, System.StringComparison.OrdinalIgnoreCase)) -> Some name
                                            | _ -> None
                                        rewritten, alias |> Option.orElse inferredAlias

                                    match expression with
                                    | Star None ->
                                        outputColumns
                                        |> List.map (fun (output, column) ->
                                            Col column,
                                            if output.Equals(column, System.StringComparison.OrdinalIgnoreCase) then None else Some output)
                                    | Star(Some qualifier)
                                        when qualifier.Equals(viewQualifier, System.StringComparison.OrdinalIgnoreCase) ->
                                        outputColumns
                                        |> List.map (fun (output, column) ->
                                            Col column,
                                            if output.Equals(column, System.StringComparison.OrdinalIgnoreCase) then None else Some output)
                                    | Col name -> [ directProjection name ]
                                    | QualifiedCol(qualifier, name)
                                        when qualifier.Equals(viewQualifier, System.StringComparison.OrdinalIgnoreCase) ->
                                        [ directProjection name ]
                                    | _ -> [ rewriteOuter expression, alias ])

                            let outerPredicate = select.Where |> Option.map rewriteOuter
                            let predicate =
                                match direct.Predicate, outerPredicate with
                                | Some viewPredicate, Some outerPredicate -> Some(BinOp(And, viewPredicate, outerPredicate))
                                | Some predicate, None
                                | None, Some predicate -> Some predicate
                                | None, None -> None

                            Ok(
                                Some
                                    { select with
                                        From = Some(FromTable mergedSource)
                                        Projections = projections
                                        Where = predicate
                                        GroupBy = select.GroupBy |> List.map rewriteOuter
                                        Having = select.Having |> Option.map rewriteOuter
                                        OrderBy = select.OrderBy |> List.map (fun (expression, direction) -> rewriteOuter expression, direction) }
                            )
                | _ -> Ok None
            | _ -> Ok None
    | _ -> Ok None

and private withCteScope
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (ctes: CommonTableExpr list)
    (outer: EvalContext option)
    (body: unit -> QueryResult * ColumnMetadata list * Value[] list)
    : QueryResult * ColumnMetadata list * Value[] list =
    if ctes.IsEmpty then
        body ()
    else

    let names = System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)

    match ctes |> List.tryFind (fun cte -> not (names.Add cte.CteName)) with
    | Some duplicate -> Err(1066, sprintf "Not unique table/alias: '%s'" duplicate.CteName), [], []
    | None ->

        let saved = currentCteScope ()

        try
            let rec bind (remaining: CommonTableExpr list) =
                match remaining with
                | [] -> Ok()
                | cte :: rest ->
                    materializeCte store registry dbName cte outer
                    |> Result.bind (fun materialized ->
                        cteScope.Value <- currentCteScope () |> Map.add (cte.CteName.ToLowerInvariant()) materialized
                        bind rest)

            match bind ctes with
            | Error err -> err, [], []
            | Ok() -> body ()
        finally
            cteScope.Value <- saved

and private withCteQueryResult
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (ctes: CommonTableExpr list)
    (body: unit -> QueryResult)
    : QueryResult =
    let mutable bodyResult = None

    let scopeResult, _, _ =
        withCteScope store registry dbName ctes None (fun () ->
            bodyResult <- Some(body ())
            Affected 0UL, [], [])

    bodyResult |> Option.defaultValue scopeResult

/// One `WITH` binding's rows. A `WITH RECURSIVE` name that actually
/// references itself iterates its `UNION` branches semi-naively (each pass
/// sees only the previous pass's new rows) until a pass adds nothing;
/// everything else is an ordinary derived table.
/// The recursion ceiling is the current session's effective
/// `cte_max_recursion_depth`; zero permits unbounded expansion.
/// A second, narrower gap: MySQL fixes each
/// recursive column's *type* from the anchor row and then errors (1406) when
/// a later pass overflows it — a literal `NULL` anchor column is
/// `VARCHAR(0)` there and rejects everything — while these rows stay
/// dynamically typed and just accept the wider value.
and private materializeCte
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (cte: CommonTableExpr)
    (outer: EvalContext option)
    : Result<ColumnDef list * Value[] list, QueryResult> =
    let selfReferenced =
        let rec inSelect (select: SelectStmt) =
            (select.From |> Option.map inFrom |> Option.defaultValue false)
            || select.Joins |> List.exists (fun j -> inFrom j.Table)

        and inFrom (item: FromItem) =
            match item with
            | FromTable tref ->
                tref.Database.IsNone
                && System.String.Equals(tref.Table, cte.CteName, System.StringComparison.OrdinalIgnoreCase)
            | FromSubquery(body, _)
            | FromLateral(body, _) -> inBody body
            | FromJsonTable _ -> false

        and inBody (body: SelectOrUnion) =
            match body with
            | PlainSelect select -> inSelect select
            | UnionSelect(first, rest, _, _, _) -> inSelect first || rest |> List.exists (snd >> inSelect)

        cte.Recursive && inBody cte.Body

    // `WITH x (a, b) AS (...)` renames the body's output columns; a count
    // mismatch is MySQL's 1353.
    let renamed (columns: ColumnDef list) : Result<ColumnDef list, QueryResult> =
        if cte.CteColumns.IsEmpty then
            Ok columns
        elif List.length cte.CteColumns <> List.length columns then
            Error(
                Err(
                    1353,
                    "In definition of view, derived table or common table expression, SELECT list and column names list have different column counts"
                )
            )
        else
            Ok(List.map2 (fun (c: ColumnDef) name -> { c with Name = name }) columns cte.CteColumns)

    if not selfReferenced then
        resolveFromSubquery store registry dbName (FromSubquery(cte.Body, cte.CteName)) outer
        |> Result.bind (fun (columns, rows) -> renamed columns |> Result.map (fun columns -> columns, rows))
    else

    match cte.Body with
    | PlainSelect _ ->
        Error(Err(3573, sprintf "Recursive Common Table Expression '%s' should contain a UNION" cte.CteName))
    | UnionSelect(anchor, recursiveBranches, _, _, _) ->
        let runBranch (select: SelectStmt) =
            match runSelectStmt store registry dbName select outer with
            | Err(code, message), _, _ -> Error(Err(code, message))
            | _, _, typedRows -> Ok typedRows

        resolveFromSubquery store registry dbName (FromSubquery(PlainSelect anchor, cte.CteName)) outer
        |> Result.bind (fun (anchorColumns, anchorRows) ->
            renamed anchorColumns
            |> Result.map (fun columns -> columns, anchorRows))
        |> Result.bind (fun (columns, anchorRows) ->
            let key (row: Value[]) = row |> Array.map (fun v -> Value.toText v |> Option.defaultValue "\u0000NULL") |> List.ofArray
            let distinctUnion = recursiveBranches |> List.exists (fun (op, _) -> match op with OpUnion all -> not all | _ -> false)
            let seen = System.Collections.Generic.HashSet<string list>(anchorRows |> List.map key)
            let accumulated = ResizeArray<Value[]>(anchorRows)
            let saved = currentCteScope ()
            let mutable working = anchorRows
            let mutable passes = 0
            let mutable failure = None

            try
                while failure.IsNone && not working.IsEmpty do
                    let recursionLimit = cteRecursionDepth.Value |> Option.defaultValue Limits.cteMaxRecursionDepth

                    if recursionLimit <> 0L && int64 passes >= recursionLimit then
                        failure <-
                            Some(
                                Err(
                                    3636,
                                    sprintf
                                        "Recursive query aborted after %d iterations. Try increasing @@cte_max_recursion_depth to a larger value."
                                        (recursionLimit + 1L)
                                )
                            )
                    else
                        cteScope.Value <- saved |> Map.add (cte.CteName.ToLowerInvariant()) (columns, working)

                        match recursiveBranches |> traverse (snd >> runBranch) with
                        | Error err -> failure <- Some err
                        | Ok branchRows ->
                            let fresh =
                                branchRows
                                |> List.concat
                                |> List.filter (fun row -> not distinctUnion || seen.Add(key row))

                            accumulated.AddRange fresh
                            working <- fresh
                            passes <- passes + 1
            finally
                cteScope.Value <- saved

            match failure with
            | Some err -> Error err
            | None -> Ok(columns, List.ofSeq accumulated))

and private compatibleSemiJoinColumns (left: ColumnDef) (right: ColumnDef) =
    let sameTextDomain =
        match left.Type with
        | TChar _
        | TVarchar _
        | TTinyText
        | TText
        | TMediumText
        | TLongText
        | TEnum _
        | TSet _ -> left.Charset = right.Charset && left.Collation = right.Collation
        | _ -> true

    left.Type = right.Type && sameTextDomain

and private orderedEqualityValues (table: Table) (index: Storage.EqualityIndex) (columnNames: string list) (values: Value list) =
    if columnNames.Length <> values.Length then
        None
    else
        columnNames
        |> traverse (resolveColumn table.Columns)
        |> Result.toOption
        |> Option.bind (fun requested ->
            let byColumn = List.zip requested values |> Map.ofList
            let ordered = index.ColumnIndices |> List.choose (fun columnIndex -> Map.tryFind columnIndex byColumn)
            if ordered.Length = index.ColumnIndices.Length then Some ordered else None)

and private tryIndexedSemiJoin
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (tableRef: TableRef)
    : Result<(ColumnDef list * Value[] seq * SelectStmt) option, QueryResult> =
    let qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table

    let inPredicate =
        let directColumn =
            function
            | Col name -> Some name
            | QualifiedCol(owner, name)
                when System.String.Equals(owner, qualifier, System.StringComparison.OrdinalIgnoreCase) ->
                Some name
            | _ -> None

        let classify =
            function
            | InSubquery(expression, subquery)
            | QuantifiedComparison(expression, Eq, Any, subquery) ->
                let expressions =
                    match expression with
                    | Row values -> values
                    | value -> [ value ]

                let columns = expressions |> List.choose directColumn
                if columns.Length = expressions.Length then Some(columns, subquery) else None
            | _ -> None

        select.Where
        |> Option.toList
        |> List.collect flattenAnd
        |> List.tryPick classify

    match inPredicate with
    | Some(columnNames, subquery) ->
        tryPhysicalTableRef store dbName tableRef
        |> Result.bind (function
            | None -> Ok None
            | Some table ->
                let outerColumns =
                    columnNames
                    |> List.map (fun name ->
                        table.Columns
                        |> List.tryFind (fun column -> System.String.Equals(column.Name, name, System.StringComparison.OrdinalIgnoreCase)))

                let rightColumns = selectProjectionColumns store dbName subquery

                let compatible =
                    outerColumns.Length = rightColumns.Length
                    && List.forall2
                        (fun left right -> Option.map2 compatibleSemiJoinColumns left right |> Option.defaultValue false)
                        outerColumns
                        rightColumns

                let index = Storage.tryEqualityIndexForColumns table columnNames

                if
                    not (storedValuesMatchReadValues store)
                    || not (isStatementStableSelect store registry dbName emptySubqueryScope subquery)
                    || not compatible
                then
                    Ok None
                else
                    let context = contextFactory store registry dbName Map.empty Map.empty None [||]
                    let materialized = runExpressionSubquery context subquery subquery

                    match materialized.Result with
                    | Err(code, message) -> Error(Err(code, message))
                    | Affected _ -> Ok None
                    | MultipleResults _ -> Error(nestedResultsError "an IN subquery")
                    | ResultSet(columns, _) when columns.Length <> columnNames.Length -> Ok None
                    | ResultSet(_, _) ->
                        let values = materialized.Rows |> List.map Array.toList

                        match index with
                        | None -> Ok None
                        | Some index ->
                            let rec probe acc =
                                function
                                | [] -> Some acc
                                | tuple :: rest when tuple |> List.contains VNull -> probe acc rest
                                | tuple :: rest ->
                                    orderedEqualityValues table index columnNames tuple
                                    |> Option.bind (Storage.tryEqualityLookupForIndex store table index)
                                    |> Option.bind (fun rows -> probe (List.fold (fun found row -> row :: found) acc rows) rest)

                            match probe [] values with
                            | None -> Ok None
                            | Some candidates ->
                                let rows =
                                    candidates
                                    |> Map.ofList
                                    |> Map.toSeq
                                    |> Seq.map snd

                                Ok(Some(table.Columns, rows, select)))
    | None -> Ok None

and private prepareLockingRead
    (store: Store)
    (dbName: string)
    (select: SelectStmt)
    : Result<Map<string, Set<RowId>>, QueryResult> =
    let sourceItems =
        (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)

    let physicalSource =
        function
        | FromTable tableRef ->
            tryPhysicalTableRef store dbName tableRef
            |> Result.map (Option.map (fun table ->
                { Qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table
                  Reference = tableRef
                  Table = table }))
        | _ -> Ok None

    sourceItems
    |> traverse physicalSource
    |> Result.bind (fun resolved ->
        let sources = resolved |> List.choose id

        let byQualifier =
            sources
            |> List.map (fun source -> source.Qualifier.ToLowerInvariant(), source)
            |> Map.ofList

        let targets =
            select.Locking
            |> List.collect (fun locking ->
                let names =
                    if locking.Tables.IsEmpty then
                        sources |> List.map _.Qualifier
                    else
                        locking.Tables

                names |> List.map (fun name -> name, locking))

        match
            targets
            |> List.tryPick (fun (name, _) ->
                if Map.containsKey (name.ToLowerInvariant()) byQualifier then None else Some name)
        with
        | Some name -> Error(Err(3568, sprintf "Unresolved table name `%s` in locking clause." name))
        | None ->
            match
                targets
                |> List.countBy (fun (name, _) -> name.ToLowerInvariant())
                |> List.tryFind (fun (_, count) -> count > 1)
            with
            | Some(name, _) -> Error(Err(3569, sprintf "Table `%s` appears in multiple locking clauses." name))
            | None ->
                let rowIdsFor (source: LockingReadSource) =
                    if sources.Length = 1 && select.Joins.IsEmpty then
                        tryEqualityAccess store dbName source.Reference select.Where
                        |> Option.map (fun plan -> plan.Rows |> List.map fst)
                        |> Option.orElseWith (fun () ->
                            tryLiteralInAccess store dbName source.Reference select.Where
                            |> Option.map (fun plan -> plan.Rows |> List.map fst))
                        |> Option.orElseWith (fun () ->
                            tryRangeLookup store dbName source.Reference select.Where
                            |> Option.map (fun (_, rows) -> rows |> List.map fst))
                        |> Option.defaultWith (fun () -> source.Table.RowsArray.Indexed |> Seq.map fst |> List.ofSeq)
                    else
                        source.Table.RowsArray.Indexed |> Seq.map fst |> List.ofSeq

                Storage.withTransactionLockCheckpoint store (fun () ->
                    targets
                    |> traverse (fun (name, locking) ->
                        let source = byQualifier.[name.ToLowerInvariant()]
                        let database = source.Reference.Database |> Option.defaultValue dbName
                        let rowIds = rowIdsFor source

                        let acquired =
                            Storage.acquireTransactionReadTargets
                                (lockingReadTimeout.Value |> Option.defaultValue (Limits.lockWaitTimeout ()))
                                store
                                database
                                source.Reference.Table
                                locking.Strength
                                locking.Wait
                                rowIds

                        Ok(source.Qualifier.ToLowerInvariant(), Set.ofList acquired))
                    |> Result.map Map.ofList))

and private runSelectStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    if not select.IntoVariables.IsEmpty then
        Err(1064, "SELECT INTO is only valid as a top-level statement"), [], []
    elif not select.Ctes.IsEmpty then
        let body = { select with Ctes = [] }
        let ctes = referencedCtes select.Ctes (selectOrUnionTableNames (PlainSelect body))
        withCteScope store registry dbName ctes outer (fun () -> runSelectStmt store registry dbName body outer)
    elif outer.IsSome then
        runUnmergedSelectStmt store registry dbName select outer
    else
    match tryMergeDirectView store registry dbName select with
    | Error error -> error, [], []
    | Ok(Some merged) -> runSelectStmt store registry dbName merged outer
    | Ok None -> runUnmergedSelectStmt store registry dbName select outer

and private runUnmergedSelectStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    DynamicScope.withValue lockingReadRows Map.empty (fun () ->
        if select.Locking.IsEmpty then
            runUnlockedSelectStmt store registry dbName select outer
        else
            let initial = lockingReadStore.Value |> Option.map (fun current -> current ()) |> Option.defaultValue store

            match prepareLockingRead initial dbName select with
            | Error error -> error, [], []
            | Ok rows ->
                let current = lockingReadStore.Value |> Option.map (fun refresh -> refresh ()) |> Option.defaultValue initial

                DynamicScope.withValue lockingReadRows rows (fun () ->
                    runUnlockedSelectStmt current registry dbName select outer))

and private runUnlockedSelectStmt
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    let matchNodes =
        (select.Projections |> List.collect (fst >> collectMatchAgainst))
        @ (select.Where |> Option.map collectMatchAgainst |> Option.defaultValue [])
        @ (select.Having |> Option.map collectMatchAgainst |> Option.defaultValue [])
        @ (select.OrderBy |> List.collect (fst >> collectMatchAgainst))
        @ (select.GroupBy |> List.collect collectMatchAgainst)
        @ (select.Joins |> List.collect (_.On >> collectMatchAgainst))
        |> List.distinct

    match select.From with
    | _ when not matchNodes.IsEmpty -> runFullTextSelect store registry dbName select matchNodes outer
    | None -> runSelect store registry dbName [] Map.empty [ [||] ] false select outer
    | Some fromItem ->
        // A single real table, no `JOIN`, narrows to its PK/UNIQUE index's
        // candidates instead of a full `resolveFromItem` scan when the
        // WHERE clause allows it (see `tryPointLookup`'s doc) — pure
        // narrowing, so everything below (`applyJoin`, `runSelect`'s own
        // WHERE/ORDER BY/LIMIT/GROUP BY) runs completely unmodified over
        // whatever this produces.
        let runResolvedWithGroupOrder groupInputOrdered (baseColumns: ColumnDef list) (baseRows: Value[] seq) (select: SelectStmt) =
            let baseQualifier = fromItemQualifier fromItem

            let initial : Result<((string * ColumnDef list) list * Value[] seq) * string list list, QueryResult> =
                Ok(([ baseQualifier, baseColumns ], baseRows), [])

            match
                planJoinOrder store dbName select
                |> List.fold
                    (fun acc join ->
                        acc
                        |> Result.bind (fun ((sources, rows), namesPerJoin) ->
                            applyJoin store registry dbName outer Map.empty (sources, rows) join
                            |> Result.map (fun (sources', rows', names) -> (sources', rows'), names :: namesPerJoin)))
                    initial
            with
            | Error e -> e, [], []
            | Ok((sources, rows), namesPerJoinRev) ->
                let namesPerJoin = List.rev namesPerJoinRev

                let select' =
                    if namesPerJoin |> List.forall List.isEmpty then
                        select
                    else
                        rewriteNaturalSelect select sources select.Joins namesPerJoin

                runSelect store registry dbName (sources |> List.collect snd) (qualifierRanges sources) rows groupInputOrdered select' outer

        let runResolved columns (rows: Value[] seq) resolvedSelect =
            runResolvedWithGroupOrder false columns rows resolvedSelect

        match fromItem, select.Joins with
        | FromTable _, _ when not select.Locking.IsEmpty ->
            match resolveFromItem store registry dbName fromItem with
            | Error e -> e, [], []
            | Ok(columns, rows) -> runResolved columns rows select
        | FromTable tref, [] ->
            match tryGroupIndexOrder store dbName tref select with
            | Some plan -> runResolvedWithGroupOrder true plan.Columns plan.Rows select
            | None ->
                match tryIndexedSemiJoin store registry dbName select tref with
                | Error error -> error, [], []
                | Ok(Some(columns, rows, narrowed)) -> runResolved columns rows narrowed
                | Ok None ->
                  match tryIndexedLookup store dbName tref select.Where with
                  | Some(columns, rows) -> runResolved columns (rows |> Seq.map snd) select
                  | None ->
                    match tryCorrelatedEqualityLookup store dbName tref select.Where outer with
                    | Some(columns, rows) -> runResolved columns (rows |> Seq.map snd) select
                    | None ->
                        match tryIndexOrder store registry dbName tref select with
                        | Some plan -> runResolved plan.Columns plan.Rows { select with OrderBy = [] }
                        | None ->
                            let resolved =
                                tryRangeLookup store dbName tref select.Where
                                |> Option.map (fun (cols, rows) -> Ok(cols, rows |> List.map snd))
                                |> Option.orElseWith (fun () -> tryInformationSchemaNarrow store registry dbName tref select.Where |> Option.map Ok)
                                |> Option.defaultWith (fun () -> resolveFromItem store registry dbName fromItem)

                            match resolved with
                            | Error e -> e, [], []
                            | Ok(columns, rows) -> runResolved columns rows select
        | FromTable tref, _ ->
            let rangeLookup =
                if select.Limit.IsSome && select.OrderBy.IsEmpty then
                    None
                else
                    tryQualifiedRangeLookup store dbName tref select.Where

            match rangeLookup with
            | Some(columns, rows) -> runResolved columns (rows |> Seq.map snd) select
            | None ->
                match resolveFromItem store registry dbName fromItem with
                | Error e -> e, [], []
                | Ok(columns, rows) -> runResolved columns rows select
        | FromLateral _, _ ->
            match resolveFromSubquery store registry dbName fromItem outer with
            | Error e -> e, [], []
            | Ok(columns, rows) -> runResolved columns rows select
        | _ ->
            match resolveFromItem store registry dbName fromItem with
            | Error e -> e, [], []
            | Ok(columns, rows) -> runResolved columns rows select

/// Flattens a top-level `AND` chain into conjuncts.
and private flattenAnd (expr: Expr) : Expr list =
    match expr with
    | BinOp(And, l, r) -> flattenAnd l @ flattenAnd r
    | e -> [ e ]

/// Literal equalities eligible for a single-table index candidate path.
and private pointLookupEqualities (tref: TableRef) (whereExpr: Expr option) : PointEquality list =
    match whereExpr with
    | None -> []
    | Some whereExpr ->
        flattenAnd whereExpr
        |> List.choose (function
            | BinOp(Eq, indexed, Lit value)
            | BinOp(Eq, Lit value, indexed) ->
                indexedColumnFor tref indexed
                |> Option.map (fun (column, transform) ->
                    { Column = column
                      Transform = transform
                      Value = value })
            | _ -> None)

and private indexedColumnFor (tref: TableRef) =
    let selfQualifier = tref.Alias |> Option.defaultValue tref.Table

    function
    | Col name -> Some(name, None)
    | QualifiedCol(qualifier, name) when System.String.Equals(qualifier, selfQualifier, System.StringComparison.OrdinalIgnoreCase) ->
        Some(name, None)
    | FuncCall(name, [ Col column ]) when name.Equals("LOWER", System.StringComparison.OrdinalIgnoreCase) ->
        Some(column, Some Lowercase)
    | FuncCall(name, [ QualifiedCol(qualifier, column) ])
        when
            name.Equals("LOWER", System.StringComparison.OrdinalIgnoreCase)
            && System.String.Equals(qualifier, selfQualifier, System.StringComparison.OrdinalIgnoreCase) ->
        Some(column, Some Lowercase)
    | _ -> None

and private literalInProbes (tref: TableRef) (whereExpr: Expr option) : LiteralInProbe list =
    whereExpr
    |> Option.toList
    |> List.collect flattenAnd
    |> List.choose (function
        | In(indexed, candidates) ->
            let indexedExpressions =
                match indexed with
                | Row expressions -> expressions
                | expression -> [ expression ]

            let candidateExpressions =
                candidates
                |> List.map (fun candidate ->
                    match indexedExpressions, candidate with
                    | [ _ ], expression -> [ expression ]
                    | _ :: _ :: _, Row expressions -> expressions
                    | _ -> [])

            let columns = indexedExpressions |> List.map (indexedColumnFor tref)

            let values =
                candidateExpressions
                |> List.map (List.map (function Lit value -> Some value | _ -> None))

            match columns with
            | _ when columns |> List.exists Option.isNone -> None
            | _ when candidateExpressions |> List.exists (fun expressions -> expressions.Length <> indexedExpressions.Length) -> None
            | _ when values |> List.collect id |> List.exists Option.isNone -> None
            | _ ->
                Some
                    { Columns = columns |> List.choose id
                      Values = values |> List.map (List.choose id) }
        | _ -> None)

and private rangeLookupBounds (scope: RangeColumnScope) (tref: TableRef) (whereExpr: Expr option) : RangeLookupBounds list =
    match whereExpr with
    | None -> []
    | Some whereExpr ->
        let selfQualifier = tref.Alias |> Option.defaultValue tref.Table

        let columnName = function
            | Col name when scope = BareOrQualifiedRange -> Some name
            | QualifiedCol(qualifier, name) when System.String.Equals(qualifier, selfQualifier, System.StringComparison.OrdinalIgnoreCase) -> Some name
            | _ -> None

        let addBound bounds name lower upper =
            match Map.tryFind name bounds with
            | None -> Map.add name (lower, upper) bounds
            | Some(existingLower, existingUpper) ->
                Map.add name (Option.orElse existingLower lower, Option.orElse existingUpper upper) bounds

        flattenAnd whereExpr
        |> List.fold
            (fun bounds expression ->
                match expression with
                | BinOp((Gt | Gte as op), column, Lit value)
                | BinOp((Lt | Lte as op), Lit value, column) ->
                    columnName column
                    |> Option.map (fun name -> addBound bounds name (Some(value, op = Gte || op = Lte)) None)
                    |> Option.defaultValue bounds
                | BinOp((Lt | Lte as op), column, Lit value)
                | BinOp((Gt | Gte as op), Lit value, column) ->
                    columnName column
                    |> Option.map (fun name -> addBound bounds name None (Some(value, op = Lte || op = Gte)))
                    |> Option.defaultValue bounds
                | _ -> bounds)
            Map.empty
        |> Map.toList
        |> List.map (fun (name, (lower, upper)) ->
            { Column = name
              Lower = lower
              Upper = upper })

and private tryEqualityAccess (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) : EqualityAccessPlan option =
    if not (storedValuesMatchReadValues store) then
        None
    else
        let tableDb = tref.Database |> Option.defaultValue dbName
        let equalities = pointLookupEqualities tref whereExpr
        let storedEqualities =
            equalities
            |> List.choose (fun equality ->
                if equality.Transform.IsNone then Some(equality.Column, equality.Value) else None)

        let composite =
            Storage.tryCompositeEqualityLookup store tableDb tref.Table storedEqualities
            |> Option.map (fun lookup ->
                { KeyName = lookup.IndexName
                  ColumnIndices = lookup.ColumnIndices
                  PrefixLengths = lookup.PrefixLengths
                  Columns = lookup.LookupColumns
                  Unique = lookup.Unique
                  Rows = lookup.LookupRows })

        composite
        |> Option.orElseWith (fun () ->
            equalities
            |> List.tryPick (fun equality ->
                Storage.tryEqualityKeyProbeForTransform
                    store
                    tableDb
                    tref.Table
                    equality.Column
                    equality.Transform
                    equality.Value
                |> Option.bind (fun (table, index) ->
                    (if equality.Transform.IsSome then
                         Storage.tryProjectedEqualityLookupForIndex store table index [ equality.Value ]
                     else
                         Storage.tryEqualityLookupForIndex store table index [ equality.Value ])
                    |> Option.map (fun rows ->
                        { KeyName = index.Name
                          ColumnIndices = index.ColumnIndices
                          PrefixLengths = index.PrefixLengths
                          Columns = table.Columns
                          Unique = index.Unique
                          Rows = rows }))))

and private tryLiteralInAccess (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) : EqualityAccessPlan option =
    if not (storedValuesMatchReadValues store) then
        None
    else
        let tableDb = tref.Database |> Option.defaultValue dbName

        literalInProbes tref whereExpr
        |> List.tryPick (fun probe ->
            let values =
                probe.Values
                |> List.filter (List.contains VNull >> not)
                |> List.distinct

            values
            |> List.tryHead
            |> Option.bind (fun first ->
                let access =
                    match probe.Columns, first with
                    | [ (column, transform) ], [ value ] when transform.IsSome ->
                        Storage.tryEqualityKeyProbeForTransform store tableDb tref.Table column transform value
                    | columns, _ when columns |> List.forall (snd >> Option.isNone) ->
                        tableSnapshot store tableDb tref.Table
                        |> Result.toOption
                        |> Option.bind (fun table ->
                            Storage.tryEqualityIndexForColumns table (columns |> List.map fst)
                            |> Option.map (fun index -> table, index))
                    | _ -> None

                access
                |> Option.bind (fun (table, index) ->
                    let lookup (tuple: Value list) =
                        orderedEqualityValues table index (probe.Columns |> List.map fst) tuple
                        |> Option.bind (fun ordered ->
                            if probe.Columns |> List.exists (snd >> Option.isSome) then
                                Storage.tryProjectedEqualityLookupForIndex store table index ordered
                            else
                                Storage.tryEqualityLookupForIndex store table index ordered)

                    let lookups = values |> List.map lookup

                    if lookups |> List.forall Option.isSome then
                        Some
                            { KeyName = index.Name
                              ColumnIndices = index.ColumnIndices
                              PrefixLengths = index.PrefixLengths
                              Columns = table.Columns
                              Unique = index.Unique
                              Rows = lookups |> List.choose id |> List.collect id |> List.distinctBy fst |> List.sortBy fst }
                    else
                        None)))

and private tryIndexedLookup (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) =
    tryEqualityAccess store dbName tref whereExpr
    |> Option.orElseWith (fun () -> tryLiteralInAccess store dbName tref whereExpr)
    |> Option.map (fun plan -> plan.Columns, plan.Rows)

and private tryCorrelatedEqualityLookup
    (store: Store)
    (dbName: string)
    (tref: TableRef)
    (whereExpr: Expr option)
    (outer: EvalContext option)
    : (ColumnDef list * (RowId * Value[]) list) option =
    let selfQualifier = tref.Alias |> Option.defaultValue tref.Table

    let innerColumn = function
        | Col name -> Some name
        | QualifiedCol(qualifier, name) when qualifier.Equals(selfQualifier, System.StringComparison.OrdinalIgnoreCase) -> Some name
        | _ -> None

    let outerValue context = function
        | QualifiedCol(qualifier, _) as expression when not (qualifier.Equals(selfQualifier, System.StringComparison.OrdinalIgnoreCase)) ->
            evalExpr context expression |> Result.toOption
        | _ -> None

    let transientLookup (tableDb: string) (column: string) (value: Value) =
        let key = tableDb.ToLowerInvariant(), tref.Table.ToLowerInvariant(), column.ToLowerInvariant()
        let lookups = (currentStatementMemo ()).CorrelatedEqualities

        match lookups.TryGetValue key with
        | true, lookup -> lookup
        | _ ->
            let lookup = Storage.tryBuildTransientEqualityLookup store tableDb tref.Table column
            lookups.[key] <- lookup
            lookup
        |> Option.bind (fun lookup ->
            lookup.FindRows value
            |> Option.map (fun rows -> lookup.TableColumns, rows))

    (if storedValuesMatchReadValues store then outer else None)
    |> Option.bind (fun context ->
        whereExpr
        |> Option.toList
        |> List.collect flattenAnd
        |> List.choose (function
            | BinOp(Eq, left, right) ->
                match innerColumn left, outerValue context right with
                | Some column, Some value -> Some(column, value)
                | _ ->
                    match innerColumn right, outerValue context left with
                    | Some column, Some value -> Some(column, value)
                    | _ -> None
            | _ -> None)
        |> fun equalities ->
            let tableDb = tref.Database |> Option.defaultValue dbName

            Storage.tryCompositeEqualityLookup store tableDb tref.Table equalities
            |> Option.map (fun lookup -> lookup.LookupColumns, lookup.LookupRows)
            |> Option.orElseWith (fun () ->
                equalities
                |> List.tryPick (fun (column, value) ->
                    Storage.tryEqualityLookup store tableDb tref.Table column value
                    |> Option.orElseWith (fun () -> transientLookup tableDb column value))))

and private tryRangeLookup (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) : (ColumnDef list * (RowId * Value[]) list) option =
    let tableDb = tref.Database |> Option.defaultValue dbName

    (if storedValuesMatchReadValues store then rangeLookupBounds BareOrQualifiedRange tref whereExpr else [])
    |> List.tryPick (fun bounds ->
        Storage.trySecondaryRangeLookup store tableDb tref.Table bounds.Column bounds.Lower bounds.Upper
        |> Option.map (fun lookup -> lookup.RangeColumns, lookup.RangeRows))

and private tryQualifiedRangeLookup (store: Store) (dbName: string) (tref: TableRef) (whereExpr: Expr option) : (ColumnDef list * (RowId * Value[]) list) option =
    let tableDb = tref.Database |> Option.defaultValue dbName

    (if storedValuesMatchReadValues store then rangeLookupBounds QualifiedRange tref whereExpr else [])
    |> List.tryPick (fun bounds ->
        Storage.trySecondaryRangeLookup store tableDb tref.Table bounds.Column bounds.Lower bounds.Upper
        |> Option.map (fun lookup -> lookup.RangeColumns, lookup.RangeRows))

and private directOrderColumns (tref: TableRef) (select: SelectStmt) : (string * Direction) list option =
    let selfQualifier = tref.Alias |> Option.defaultValue tref.Table

    let directBareColumn name =
        let directProjections =
            select.Projections
            |> List.map (function
                | Col projection, None when System.String.Equals(projection, name, System.StringComparison.OrdinalIgnoreCase) -> 1
                | Star None, None -> 1
                | Col _, None -> 0
                | _ -> 2)

        if directProjections |> List.forall ((<>) 2) && List.sum directProjections <= 1 then Some name else None

    let directColumn = function
        | Col name -> directBareColumn name
        | QualifiedCol(qualifier, name) when System.String.Equals(qualifier, selfQualifier, System.StringComparison.OrdinalIgnoreCase) -> Some name
        | _ -> None

    select.OrderBy
    |> traverse (fun (expression, direction) ->
        match directColumn expression with
        | Some column -> Ok(column, direction)
        | None -> Error())
    |> Result.toOption

and private tryIndexOrder
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (tref: TableRef)
    (select: SelectStmt)
    : IndexOrderPlan option =
    let tableDb = tref.Database |> Option.defaultValue dbName

    let canUseIndexOrder =
        not select.Distinct
        && select.GroupBy.IsEmpty
        && select.Having.IsNone
        && not (select.Projections |> List.exists (fst >> containsAggregate registry))
        && not (select.Projections |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not))

    if not (storedValuesMatchReadValues store) || not canUseIndexOrder then
        None
    else
        directOrderColumns tref select
        |> Option.bind (fun orderedColumns ->
            let plan (keyName: string) (indices: int list) (columns: ColumnDef list) (count: int) (rows: Value[] seq) =
                let unsupported =
                    indices
                    |> List.exists (fun index ->
                        match columns.[index].Type with
                        | TEnum _
                        | TSet _ -> true
                        | _ -> false)

                if unsupported then
                    None
                else
                    Some
                        { KeyName = keyName
                          ColumnIndices = indices
                          Columns = columns
                          EstimatedRows = count
                          Rows = rows }

            match orderedColumns with
            | [ column, direction ] ->
                let lower, upper =
                    rangeLookupBounds BareOrQualifiedRange tref select.Where
                    |> List.tryFind (fun bounds -> System.String.Equals(bounds.Column, column, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun bounds -> bounds.Lower, bounds.Upper)
                    |> Option.defaultValue (None, None)

                Storage.trySecondaryOrderedLookup store tableDb tref.Table column lower upper direction
                |> Option.bind (fun (keyName, index, columns, count, rows) -> plan keyName [ index ] columns count rows)
                |> Option.orElseWith (fun () ->
                    Storage.tryCompositeOrderedLookup store tableDb tref.Table [ column, direction ]
                    |> Option.bind (fun lookup ->
                        plan lookup.OrderedIndexName lookup.OrderedColumnIndices lookup.OrderedColumns lookup.OrderedRowCount lookup.OrderedRows))
            | columns ->
                Storage.tryCompositeOrderedLookup store tableDb tref.Table columns
                |> Option.bind (fun lookup ->
                    plan lookup.OrderedIndexName lookup.OrderedColumnIndices lookup.OrderedColumns lookup.OrderedRowCount lookup.OrderedRows))

/// Per-column MySQL wire type for a freshly-projected resultset, read off
/// the first non-NULL `Value` in each column across `rows` — a plain
/// data-driven read of the same typed values the row already carries
/// (see `Value.mysqlTypeOf`), not a separate static type-inference pass,
/// so it's correct for a literal, a cast, or an aggregate the same way it
/// is for a bare column reference. Falls back to VAR_STRING for a column
/// that's NULL in every row (or there are no rows at all) — NULL
/// round-trips the same regardless of the declared type, so there's
/// nothing to lose by guessing wrong there.
and private columnMetadataOf (colCount: int) (rows: (string * Value) list list) : ColumnMetadata list =
    [ for i in 0 .. colCount - 1 ->
          rows
          |> List.tryPick (fun row ->
              match snd row.[i] with
              | VNull -> None
              | v -> Some(Value.mysqlMetadataOf v))
          |> Option.defaultValue (Value.columnMetadata Value.TypeVarString) ]

and private rowCount =
    function
    | Lit(VInt count) -> int (min (int64 System.Int32.MaxValue) (max 0L count))
    | Lit(VUInt count) -> int (min (uint64 System.Int32.MaxValue) count)
    | Lit value -> int (min (float System.Int32.MaxValue) (max 0.0 (Value.toDouble value)))
    | Col name ->
        match tryRoutineVariable name with
        | Some variable -> rowCount (Lit variable.Value)
        | None -> raise (SqlError(1210, "Incorrect arguments to LIMIT"))
    | _ -> raise (SqlError(1210, "Incorrect arguments to LIMIT"))

and private applyLimitOffset (limit: int option) (offset: int option) (rows: 'a list) : 'a list =
    let afterOffset =
        match offset with
        | Some o -> rows |> List.skip (min o (List.length rows))
        | None -> rows

    match limit with
    | Some l -> afterOffset |> List.truncate (max 0 l)
    | None -> afterOffset

/// The one comparator every `ORDER BY` sort site (plain
/// `SELECT`, grouped, windowed, `UNION`) shares, instead of each carrying
/// its own copy of the same fold.
and private compareByOrderKeys (dirs: Direction list) (ka: (Value * Collation.Collation option) list) (kb: (Value * Collation.Collation option) list) : int =
    let rec compare
        (dirs: Direction list)
        (left: (Value * Collation.Collation option) list)
        (right: (Value * Collation.Collation option) list)
        =
        match dirs, left, right with
        | [], [], [] -> 0
        | dir :: remainingDirs, (leftValue, collation) :: remainingLeft, (rightValue, _) :: remainingRight ->
            let result =
                match leftValue, rightValue, collation with
                | VString leftText, VString rightText, Some collation -> collation.Compare leftText rightText
                | _ -> Value.compareTotal leftValue rightValue

            let directed = if dir = Asc then result else -result

            if directed = 0 then
                compare remainingDirs remainingLeft remainingRight
            else
                directed
        | _ -> invalidArg "keys" "ORDER BY keys and directions must have equal lengths"

    compare dirs ka kb

/// A `UNION` statement's combined resultset, plus its column types —
/// pulled out of `execute`'s `Union` arm (which just calls this and
/// discards the second half) so `QueryHandler.executeStatement` can call
/// it directly too for the wire-level types. Public (unlike
/// `runSelectStmt`): it has no `EvalContext` in its own signature, so
/// nothing stops `QueryHandler` from calling it straight instead of
/// through a `runTopLevelSelect`-style wrapper.
///
/// Each branch runs as an independent, uncorrelated `SELECT` (`outer =
/// None`) — `Union` only ever occurs as a top-level statement (see
/// `Ast.Union`'s doc), never nested inside another query's expression, so
/// there's no outer row to thread through.
///
/// MySQL's UNION type reconciliation, at wire-type granularity: all-integer
/// columns aggregate to LONGLONG; a DECIMAL promotes the numeric result;
/// FLOAT/DOUBLE promote it further; a string anywhere poisons the column to
/// VAR_STRING (verified: `UNION` of '10', '9', 2, 1.5 sorts as strings —
/// '1.5,10,2,9', not numerically); DATE + DATETIME lands on DATETIME;
/// anything else falls back to text.
and private unionAggregateType (columns: ColumnMetadata list) : ColumnMetadata =
    let types = columns |> List.map _.TypeId
    let isInt t =
        t = Value.TypeTiny
        || t = Value.TypeShort
        || t = Value.TypeLong
        || t = Value.TypeLongLong
    let isDate t = t = Value.TypeDate
    let isDateTime t = t = Value.TypeDateTime || t = Value.TypeTimestamp

    if types |> List.exists (fun t -> t = Value.TypeVarString || t = Value.TypeString || t = Value.TypeVarchar || t = Value.TypeBlob) then
        Value.columnMetadata Value.TypeVarString
    elif types |> List.exists (fun t -> t = Value.TypeDouble) then
        Value.columnMetadata Value.TypeDouble
    elif types |> List.exists (fun t -> t = Value.TypeFloat) then
        Value.columnMetadata Value.TypeFloat
    elif types |> List.exists (fun t -> t = Value.TypeNewDecimal) then
        Value.columnMetadata Value.TypeNewDecimal
    elif types |> List.forall isInt then
        { Value.columnMetadata Value.TypeLongLong with
            Flags =
                if columns |> List.exists (fun column -> column.Flags &&& Value.UnsignedFlag <> 0us) then
                    Value.UnsignedFlag
                else
                    0us }
    elif types |> List.forall (fun t -> isDate t || isDateTime t) then
        Value.columnMetadata (if types |> List.exists isDateTime then Value.TypeDateTime else Value.TypeDate)
    else
        Value.columnMetadata Value.TypeVarString

/// Coerces one combined-UNION value to the column's reconciled wire type,
/// so `ORDER BY` (and the wire types) see what MySQL's own reconciliation
/// produces instead of each branch's original per-branch type. Temporal and
/// unrecognized types pass through unchanged; NULL stays NULL.
and private coerceUnionValue (metadata: ColumnMetadata) (v: Value) : Value =
    let ty = metadata.TypeId

    match v with
    | VNull -> VNull
    | _ ->
        match ty with
        | t when t = Value.TypeTiny || t = Value.TypeShort || t = Value.TypeLong || t = Value.TypeLongLong ->
            match v with
            | VUInt _ when metadata.Flags &&& Value.UnsignedFlag <> 0us -> v
            | _ -> VInt(int64 (Value.toDouble v))
        | t when t = Value.TypeNewDecimal -> VDecimal(decimal (Value.toDouble v))
        | t when t = Value.TypeDouble || t = Value.TypeFloat -> VDouble(Value.toDouble v)
        | t when t = Value.TypeVarString || t = Value.TypeString || t = Value.TypeVarchar || t = Value.TypeBlob ->
            VString(Value.toText v |> Option.defaultValue "")
        | _ -> v

and private runUnionStmtWithOuter
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (first: SelectStmt)
    (rest: (SetOp * SelectStmt) list)
    (orderBy: OrderKey list)
    (limitExpr: Expr option)
    (offsetExpr: Expr option)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    // A `WITH` clause ahead of a UNION is parsed onto the first branch (see
    // `Parser.withClause`) but scopes over every branch.
    if not first.Ctes.IsEmpty then
        let body = UnionSelect({ first with Ctes = [] }, rest, orderBy, limitExpr, offsetExpr)
        let ctes = referencedCtes first.Ctes (selectOrUnionTableNames body)
        withCteScope store registry dbName ctes outer (fun () ->
            runUnionStmtWithOuter store registry dbName { first with Ctes = [] } rest orderBy limitExpr offsetExpr outer)
    else

    let limit = Option.map rowCount limitExpr
    let offset = Option.map rowCount offsetExpr

    let runBranch (select: SelectStmt) = runSelectStmt store registry dbName select outer

    // Each branch's text row paired with its own typed row, kept aligned
    // through combining so the `ORDER BY` below can compare typed values
    // instead of re-wrapping the text back into a lexicographically-
    // comparing `VString` (`SELECT n FROM t UNION SELECT n FROM t ORDER BY
    // n` sorting "10" before "2" otherwise).
    //
    // MySQL reconciles every branch's column type across the whole UNION
    // first and only *then* compares for DISTINCT (`SELECT 1.0 UNION
    // SELECT 1` is one row, `1.0` — a dedup keyed on each branch's own
    // pre-reconciliation text, `"1.0"` vs `"1"`, would keep two). So this
    // pass just concatenates every branch's raw rows plus an `(op,
    // cumulative length)` boundary marker per branch, and the actual set
    // operations run after `reconciled`/`coerceColumn` below, replayed over
    // those boundaries.
    // MySQL reconciles a temporal UNION column's fsp the same way it does
    // types/collations: the max declared fsp across branches wins, and every
    // row renders exactly that many digits (a whole-second DATETIME row
    // unioned with a DATETIME(6) one shows `.000000`). Declared, not
    // value-derived — so it comes from each branch's column/CAST fsp.
    let unionFsp (a: int option) (b: int option) =
        match a, b with
        | Some x, Some y -> Some(max x y)
        | Some x, None
        | None, Some x -> Some x
        | None, None -> None

    let combine
        (acc: Result<string list * ((string option list) * Value[]) list * ColumnMetadata list list * Collation.Collation list * int option list * (SetOp * int) list, QueryResult>)
        (setOp: SetOp, select: SelectStmt)
        =
        acc
        |> Result.bind (fun (cols, rowsSoFar, typesSoFar, collationsSoFar, fspsSoFar, boundaries) ->
            match runBranch select with
            | Err(code, message), _, _ -> Error(Err(code, message))
            | Affected _, _, _ -> Error(Err(1064, "UNION branch did not return a resultset"))
            | MultipleResults _, _, _ -> Error(nestedResultsError "a UNION branch")
            | ResultSet(branchCols, _), _, _ when List.length branchCols <> List.length cols ->
                Error(Err(1222, "The used SELECT statements have a different number of columns"))
            | ResultSet(branchCols, branchRows), branchTypes, branchTyped ->
                let branchPaired = List.zip branchRows branchTyped

                let branchCollations = selectColumnCollations store registry dbName select branchCols
                let collations =
                    if collationsSoFar.IsEmpty then
                        branchCollations
                    else
                        List.map2 strictestUnionCollation collationsSoFar branchCollations

                let branchFsps = selectColumnFsps store registry dbName select branchCols
                let fsps = List.map2 unionFsp fspsSoFar branchFsps

                let combined = rowsSoFar @ branchPaired
                Ok(cols, combined, branchTypes :: typesSoFar, collations, fsps, boundaries @ [ setOp, List.length combined ]))

    match runSelectStmt store registry dbName first None with
    | Err(code, message), _, _ -> Err(code, message), [], []
    | Affected _, _, _ -> Err(1064, "UNION branch did not return a resultset"), [], []
    | MultipleResults _, _, _ -> nestedResultsError "a UNION branch", [], []
    | ResultSet(firstCols, firstRows), firstTypes, firstTyped ->
        let firstCollations = selectColumnCollations store registry dbName first firstCols
        let firstFsps = selectColumnFsps store registry dbName first firstCols
        let firstPaired = List.zip firstRows firstTyped
        let firstLen = List.length firstPaired

        match rest |> List.fold combine (Ok(firstCols, firstPaired, [ firstTypes ], firstCollations, firstFsps, [])) with
        | Error e -> e, [], []
        | Ok(cols, allPaired, typesSoFar, collations, fsps, boundaries) ->
            // MySQL's union type reconciliation across every branch, and
            // every row's values coerced to it — an `ORDER BY` over the
            // mixed-typed union then sorts exactly as MySQL does.
            let reconciled =
                cols
                |> List.mapi (fun i _ -> typesSoFar |> List.map (fun ts -> ts.[i]) |> unionAggregateType)

            // A DECIMAL-reconciled column renders at the union's scale —
            // MySQL shows `SELECT 2 UNION SELECT 1.5` as 1.5 / 2.0, not 2.
            // The scale is the widest fraction any branch's value carries.
            let decimalScale (v: Value) =
                match v with
                | VDecimal _ ->
                    match Value.toText v with
                    | Some text ->
                        match text.IndexOf '.' with
                        | -1 -> 0
                        | dot -> text.Length - dot - 1
                    | None -> 0
                | _ -> 0

            let rescale (scale: int) (v: Value) : Value =
                match v with
                | VDecimal d when scale > 0 ->
                    VDecimal(
                        System.Decimal.Parse(
                            d.ToString("F" + string scale, System.Globalization.CultureInfo.InvariantCulture),
                            System.Globalization.CultureInfo.InvariantCulture
                        )
                    )
                | _ -> v

            let scales =
                cols
                // `List.fold max 0`, not `List.max`: an all-empty union
                // (`SELECT 1 WHERE FALSE UNION SELECT 2 WHERE FALSE`) has no
                // rows to take a scale from, and `List.max` throws on the
                // empty list — a scale of 0 is correct there.
                |> List.mapi (fun i _ -> allPaired |> List.fold (fun acc (_, typed) -> max acc (decimalScale typed.[i])) 0)

            let coerceColumn (i: int) (v: Value) : Value =
                let coerced = coerceUnionValue reconciled.[i] v
                if reconciled.[i].TypeId = Value.TypeNewDecimal then rescale scales.[i] coerced else coerced

            let coercedPairedRaw =
                allPaired
                |> List.map (fun (_, typed) ->
                    let coerced = typed |> Array.mapi coerceColumn
                    // Re-render the text row from the coerced values, so the
                    // wire text carries the reconciliation too (2 -> 2.0), and
                    // a DATETIME-reconciled column at the union's fsp so every
                    // branch's rows show the same digit count.
                    let text =
                        coerced
                        |> Array.mapi (fun i v ->
                            match v with
                            | VNull -> None
                            | v ->
                                match (if reconciled.[i].TypeId = Value.TypeDateTime then fsps.[i] else None) with
                                | Some fsp -> Value.toTextFsp fsp v
                                | None -> Value.toText v)
                        |> List.ofArray
                    text, coerced)

            // Row identity for every set operation, keyed on the reconciled
            // text/collation rather than each branch's own pre-reconciliation
            // one — collation-aware, so åge/age fold under the aggregated
            // collation (strictest branch wins — bin never folds).
            let dedupeKey (text: string option list) =
                List.map2 (fun (col: Collation.Collation) (cell: string option) -> cell |> Option.map col.KeyOf) collations text

            let keyOf (row: string option list * Value[]) = dedupeKey (fst row)

            // One operator applied to two already-materialized row lists.
            // `ALL` is multiset arithmetic (INTERSECT ALL takes the lesser of
            // the two multiplicities, EXCEPT ALL subtracts them); without it
            // the result is distinct. Oracle-pinned on MySQL 8.4.11 with
            // left = [1,1,2,3], right = [1,2,2]: INTERSECT [1,2],
            // INTERSECT ALL [1,2], EXCEPT [3], EXCEPT ALL [1,3].
            let applySetOp (op: SetOp) (left: (string option list * Value[]) list) (right: (string option list * Value[]) list) =
                let distinct rows = rows |> List.distinctBy keyOf

                // How many times each key occurs on the right — the budget
                // INTERSECT ALL draws down and EXCEPT ALL subtracts.
                let counts (rows: _ list) =
                    rows
                    |> List.countBy keyOf
                    |> List.fold (fun m (k, n) -> Map.add k n m) Map.empty

                let takeByBudget (budget: Map<_, int>) (keep: int -> bool) rows =
                    rows
                    |> List.mapFold
                        (fun (remaining: Map<_, int>) row ->
                            let k = keyOf row
                            let left = remaining |> Map.tryFind k |> Option.defaultValue 0
                            (if keep left then Some row else None), Map.add k (max 0 (left - 1)) remaining)
                        budget
                    |> fst
                    |> List.choose id

                match op with
                | OpUnion true -> left @ right
                | OpUnion false -> distinct (left @ right)
                | OpIntersect false ->
                    let rightKeys = right |> List.map keyOf |> Set.ofList
                    distinct left |> List.filter (fun row -> rightKeys.Contains(keyOf row))
                | OpIntersect true -> takeByBudget (counts right) (fun remaining -> remaining > 0) left
                | OpExcept false ->
                    let rightKeys = right |> List.map keyOf |> Set.ofList
                    distinct left |> List.filter (fun row -> not (rightKeys.Contains(keyOf row)))
                | OpExcept true -> takeByBudget (counts right) (fun remaining -> remaining <= 0) left

            // Each branch's own coerced row slice, recovered from the
            // cumulative boundary offsets `combine` recorded.
            let slices =
                boundaries
                |> List.mapFold
                    (fun prevUpto (op, upto) -> (op, coercedPairedRaw |> List.skip prevUpto |> List.take (upto - prevUpto)), upto)
                    firstLen
                |> fst

            // INTERSECT binds tighter than UNION/EXCEPT (see `Ast.SetOp`), so
            // this is a two-pass reduction rather than a left fold: collapse
            // every INTERSECT into the branch it attaches to first, then
            // combine what's left strictly left to right.
            let grouped =
                slices
                |> List.fold
                    (fun acc (op, rows) ->
                        match op, acc with
                        | (OpIntersect _), ((prevOp, prevRows) :: earlier) -> (prevOp, applySetOp op prevRows rows) :: earlier
                        | _ -> (op, rows) :: acc)
                    [ OpUnion true, coercedPairedRaw |> List.truncate firstLen ]
                |> List.rev

            let coercedPaired =
                match grouped with
                | (_, head) :: tail -> tail |> List.fold (fun acc (op, rows) -> applySetOp op acc rows) head
                | [] -> []

            // `ORDER BY`/`LIMIT` on the combined result uses ordinary
            // alias/positional resolution and typed values rather than
            // re-parsing rendered text.
            let projections = cols |> List.map (fun c -> Col c, None)
            let resolveOrder = resolvePositionalOrAlias projections

            // The combined result's own synthetic columns (name + reconciled
            // wire type), so an `ORDER BY` expression beyond a bare output
            // column (`ORDER BY -v`, `ORDER BY UPPER(name)`, ...) can be
            // evaluated for real against a row.
            // Only `Name` matters here — `columnIndexOf` reads nothing else,
            // and `ctxForOrder`'s empty `Qualifiers` means the `Type`-aware
            // helpers (`resolvedCollation`/`enumOrdinalFor`) never look this
            // `ColumnDef` up anyway, so the rest of the record is filler.
            let orderColumns: ColumnDef list =
                cols
                |> List.map (fun c ->
                    { Name = c
                      Type = TVarchar 255
                      NumericDisplay = None
                      Nullable = true
                      Default = None
                      AutoIncrement = false
                      PrimaryKey = false
                      Unique = false
                      Generated = None
                      Comment = ""
                      Collation = None
                      Charset = None
                      OnUpdateCurrentTimestamp = false })

            let ctxForOrder = contextFactory store registry dbName (columnIndexOf orderColumns) Map.empty None

            let orderKeyOf (typedRow: Value[]) (expr: Expr) : Result<Value * Collation.Collation option, EvalError> =
                match resolveOrder expr with
                | Col name ->
                    match cols |> List.tryFindIndex (fun c -> System.String.Equals(c, name, System.StringComparison.OrdinalIgnoreCase)) with
                    | Some i when i < typedRow.Length -> Ok(typedRow.[i], None)
                    | _ -> Ok(VNull, None)
                | resolved -> evalExpr (ctxForOrder typedRow) resolved |> Result.map (fun v -> v, None)

            let sortedResult =
                if orderBy.IsEmpty then
                    Ok coercedPaired
                else
                    coercedPaired
                    |> traverse (fun (text, typed) ->
                        orderBy
                        |> traverse (fun (expr, _) -> orderKeyOf typed expr)
                        |> Result.map (fun keys -> keys, (text, typed)))
                    |> Result.map (
                        List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys (orderBy |> List.map snd) ka kb)
                        >> List.map snd
                    )

            match sortedResult with
            | Error(code, message) -> Err(code, message), [], []
            | Ok sortedPaired ->
                let limitedPaired = sortedPaired |> applyLimitOffset limit offset
                ResultSet(cols, limitedPaired |> List.map fst), reconciled, limitedPaired |> List.map snd

/// One projection's `(column name, value)` pairs — a list because `SELECT
/// *` expands to every column of the row.
and private evalProjection (ctx: EvalContext) (columns: ColumnDef list) (proj: Projection) : Result<(string * Value) list, EvalError> =
    match proj with
    | Star None, _ -> Ok(columns |> List.mapi (fun i column -> column.Name, readColumnValue ctx.Store column ctx.Row.[i]))
    | Star(Some qualifier), _ -> resolveStarQualifier ctx qualifier
    | expr, aliasOpt ->
        evalExpr ctx expr
        |> Result.map (fun v -> [ aliasOpt |> Option.defaultValue (exprLabel expr), v ])

/// An all-`VNull` row shaped like `columns` — used only to type-check a
/// statement's expressions (unknown column/function) independent of the
/// actual data, since those errors are about the schema, not row values.
/// Without this, a table with zero matching (or zero total) rows would
/// silently skip evaluating its WHERE/ORDER BY/projection at all and never
/// surface a real error.
and private probeRow (columns: ColumnDef list) : Value[] = Array.create (List.length columns) VNull

/// One aggregate call's value over the rows a `WHERE` already filtered to.
/// `COUNT(*)` counts rows directly (`*` isn't a valid `Expr`, so there's
/// nothing to evaluate per row); every other form evaluates its one
/// argument per row first, drops the `NULL`s (SQL's aggregate rule), then
/// folds via whatever `registry` has registered for `name` (see
/// `Functions.Registry.Aggregates`) — except `COUNT(expr)`, which (unlike
/// `SUM`/`AVG`/`MIN`/`MAX`) yields `0` rather than `NULL` on an empty
/// non-NULL set, so it still folds even when there's nothing left to fold.
and private evalAggregate
    (registry: Registry)
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (name: string)
    (args: Expr list)
    : Result<Value, EvalError> =
    let isCount = System.String.Equals(name, "COUNT", System.StringComparison.OrdinalIgnoreCase)
    let isGroupConcat = System.String.Equals(name, "GROUP_CONCAT", System.StringComparison.OrdinalIgnoreCase)
    let upper = name.ToUpperInvariant()

    // `COUNT(DISTINCT x)`/`SUM(DISTINCT x)`/... all unwrap the same way:
    // dedupe the per-row values (after dropping `NULL`s) before folding,
    // regardless of which aggregate wraps the `DISTINCT`.
    let unwrapDistinct =
        function
        | Distinct e -> true, e
        | e -> false, e

    match args with
    | [ Star _ ] when isCount -> Ok(VInt(int64 (List.length rows)))
    | [ innerExpr ]
        when (upper = "COUNT" || upper = "SUM" || upper = "AVG")
             && (match innerExpr with Distinct _ -> false | _ -> true)
             && Functions.isUnmodifiedBuiltinAggregate name registry ->
        let mutable count = 0L
        let mutable exactTotal = 0M
        let mutable total: Value option = None
        let mutable failure: EvalError option = None

        let add value =
            count <- count + 1L

            if upper <> "COUNT" then
                match total, value with
                | None, VInt integer -> exactTotal <- exactTotal + decimal integer
                | None, VUInt unsigned -> exactTotal <- exactTotal + decimal unsigned
                | None, VDecimal number -> exactTotal <- exactTotal + number
                | None, value when count = 1L -> total <- Some value
                | None, value -> total <- Some(Value.add (VDecimal exactTotal) value)
                | Some current, value -> total <- Some(Value.add current value)

        for row in rows do
            if failure.IsNone then
                let ctx = ctxFor row

                match evalExpr ctx innerExpr with
                | Error error -> failure <- Some error
                | Ok VNull -> ()
                | Ok value ->
                    add (if upper = "COUNT" then value else enumNumericOperand ctx innerExpr value)

        match failure with
        | Some error -> Error error
        | None when upper = "COUNT" -> Ok(VInt count)
        | None when count = 0L -> Ok VNull
        | None when upper = "SUM" -> Ok(total |> Option.defaultValue (VDecimal exactTotal))
        | None ->
            total
            |> Option.defaultValue (VDecimal exactTotal)
            |> fun sum -> Ok(Value.div sum (VInt count))
    | arg :: rest when isGroupConcat ->
        // `GROUP_CONCAT` folds entirely here rather than through
        // `registry.Aggregates` — see `isAggregateCall`'s doc. `rest` holds
        // zero or more `OrderBy` markers (see `Parser.groupConcatAtom`)
        // followed by an optional `SEPARATOR` literal.
        let distinct, innerExpr = unwrapDistinct arg

        let orderKeys = rest |> List.choose (function OrderBy(e, d) -> Some(e, d) | _ -> None)

        let separator =
            match rest |> List.tryPick (function Lit(VString s) -> Some s | _ -> None) with
            | Some s -> s
            | None -> ","

        let evalRow (row: Value[]) : Result<(Value * Value * (Value * Collation.Collation option) list) option, EvalError> =
            let ctx = ctxFor row
            evalExpr ctx innerExpr
            |> Result.bind (function
                | VNull -> Ok None
                | v -> orderKeys |> traverse (fst >> evalOrderKey ctx) |> Result.map (fun keys -> Some(v, collationKeyOf ctx innerExpr v, keys)))

        rows
        |> traverseSeq evalRow
        |> Result.map (fun present ->
            let ordered =
                if orderKeys.IsEmpty then
                    present
                else
                    present |> List.sortWith (fun (_, _, ka) (_, _, kb) -> compareByOrderKeys (List.map snd orderKeys) ka kb)
            // Collation-aware dedupe: åge/age fold to one value under an
            // ai_ci column, stay distinct under bin.
            let deduped = if distinct then List.distinctBy (fun (_, key, _) -> key) ordered else ordered

            if deduped.IsEmpty then
                VNull
            else
                let limit = groupConcatMaxLen.Value |> Option.defaultValue 1024
                let result = System.Text.StringBuilder(min limit 4096)
                let mutable remaining = limit

                let appendWithinLimit (text: string) =
                    let bytes = System.Text.Encoding.UTF8.GetBytes text

                    if bytes.Length <= remaining then
                        result.Append text |> ignore
                        remaining <- remaining - bytes.Length
                        false
                    elif remaining = 0 then
                        true
                    else
                        let mutable cut = remaining

                        while cut > 0 && bytes.[cut] &&& 0xc0uy = 0x80uy do
                            cut <- cut - 1

                        result.Append(System.Text.Encoding.UTF8.GetString(bytes, 0, cut)) |> ignore
                        remaining <- 0
                        true

                let mutable truncatedAt = None

                deduped
                |> List.iteri (fun i (v, _, _) ->
                    let cutBySeparator = i > 0 && appendWithinLimit separator
                    let cutByValue = appendWithinLimit (v |> toText |> Option.defaultValue "")

                    if truncatedAt.IsNone && (cutBySeparator || cutByValue) then
                        truncatedAt <- Some(i + 1))

                truncatedAt
                |> Option.iter (fun row -> Diagnostics.warning 1260 (sprintf "Row %d was cut by GROUP_CONCAT()" row))

                VString(result.ToString()))
    // Both JSON aggregates are NULL over an empty group but keep the NULLs
    // *inside* a non-empty one, so neither can route through the
    // NULL-filtered fold below.
    | [ arg ] when upper = "JSON_ARRAYAGG" ->
        if rows.IsEmpty then
            Ok VNull
        else
            rows
            |> traverse (fun row -> evalExpr (ctxFor row) arg)
            |> Result.map Functions.jsonArrayAggregate
    | [ keyExpr; valueExpr ] when upper = "JSON_OBJECTAGG" ->
        if rows.IsEmpty then
            Ok VNull
        else
            rows
            |> traverse (fun row ->
                let ctx = ctxFor row
                evalExpr ctx keyExpr |> Result.bind (fun k -> evalExpr ctx valueExpr |> Result.map (fun v -> k, v)))
            |> Result.map Functions.jsonObjectAggregate
    | [ arg ] ->
        let distinct, innerExpr = unwrapDistinct arg
        let isMin = System.String.Equals(name, "MIN", System.StringComparison.OrdinalIgnoreCase)
        let isMax = System.String.Equals(name, "MAX", System.StringComparison.OrdinalIgnoreCase)

        // Every aggregate but COUNT/MIN/MAX folds numerically, and an ENUM in
        // numeric context is its declaration ordinal — `SUM(status)` adds
        // ordinals, while `MAX(status)` still returns the label.
        let foldsNumerically = not (isCount || isMin || isMax)

        match Functions.lookupAggregate name registry with
        | None -> Error(unknownFunction name)
        | Some fold ->
            rows
            |> traverse (fun row ->
                let ctx = ctxFor row

                evalExpr ctx innerExpr
                |> Result.map (fun v ->
                    let key = collationKeyOf ctx innerExpr v

                    let v = if foldsNumerically then enumNumericOperand ctx innerExpr v else v

                    v, key))
            |> Result.map (fun keyed ->
                let nonNull = keyed |> List.filter (fst >> function VNull -> false | _ -> true)
                // `DISTINCT` folds by the expression's own collation
                // (MySQL-verified: COUNT(DISTINCT name) over åge/age/ÅGE
                // is 1 under ai_ci, 3 under bin).
                let deduped = if distinct then nonNull |> List.distinctBy snd |> List.map fst else nonNull |> List.map fst

                if isCount || not deduped.IsEmpty then
                    // MIN/MAX over strings compare by the expression's own
                    // collation weights, with primary-equal values keeping
                    // the first-seen one (MySQL-verified: MAX('ÅGE','age')
                    // and MAX('age','ÅGE') each return whichever came
                    // first) — `Value.compare`'s folded server-default
                    // order would pick wrong.
                    if (isMin || isMax) && deduped |> List.forall (function VString _ -> true | _ -> false) then
                        let col = keyCollation (ctxFor (List.tryHead rows |> Option.defaultValue [||])) innerExpr

                        let text (v: Value) =
                            match v with
                            | VString s -> s
                            | _ -> ""

                        if isMax then
                            List.reduce (fun best v -> if col.ComparePrimary (text best) (text v) >= 0 then best else v) deduped
                        else
                            List.reduce (fun best v -> if col.ComparePrimary (text best) (text v) <= 0 then best else v) deduped
                    else
                        fold deduped
                else
                    Functions.tryEmptyAggregate name |> Option.defaultValue VNull)
    | Distinct firstExpr :: rest when isCount ->
        // `COUNT(DISTINCT a, b)` — `distinctArg` (the call-argument parser)
        // attaches `Distinct` only to the first comma-separated argument,
        // but MySQL's `DISTINCT` here scopes over the whole tuple `(a, b)`,
        // not just `a`. Evaluate every argument per row, drop a row if
        // *any* column of it is NULL (SQL's usual "NULL drops the row from
        // an aggregate" rule, applied to the whole tuple), dedupe the
        // tuples — by each element's own collation — and count what's
        // left.
        let allArgs = firstExpr :: rest

        rows
        |> traverse (fun row ->
            let ctx = ctxFor row

            allArgs
            |> traverse (fun e -> evalExpr ctx e |> Result.map (fun v -> v, collationKeyOf ctx e v)))
        |> Result.map (fun tuples ->
            tuples
            |> List.filter (List.exists (fst >> function VNull -> true | _ -> false) >> not)
            |> List.distinctBy (List.map snd)
            |> List.length
            |> int64
            |> VInt)
    // `isAggregateCall` narrows this to single-argument aggregate calls,
    // except `GROUP_CONCAT`'s optional `SEPARATOR` and
    // `COUNT(DISTINCT a, b)`. Anything else
    // multi-argument (e.g. `SUM(DISTINCT a, b)`, which MySQL itself
    // rejects) is a syntax error, not a silent NULL.
    | _ -> Error(1064, sprintf "Incorrect parameter count in the call to native function '%s'" name)

/// Pre-evaluates every aggregate subtree of `expr` (anywhere it appears —
/// nested in arithmetic, a function argument, ...) against `rows` into a
/// `Lit`, so the caller can evaluate what's left as an ordinary per-row
/// expression against one representative row. Same shape as
/// `substituteValuesFunc`'s rewrite walk. Without this, `SELECT COUNT(*) +
/// 1 FROM t` fails: the top-level node is `BinOp(Add, FuncCall("COUNT",
/// [Star]), Lit 1)`, not a bare `FuncCall`, so plain per-row evaluation
/// would try (and fail) to look `COUNT` up as a scalar function.
and private rewriteAggregates
    (registry: Registry)
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (expr: Expr)
    : Result<Expr, EvalError> =
    let sub = rewriteAggregates registry ctxFor rows

    match expr with
    | Placeholder _ -> Ok expr
    | UserVariable _
    | SystemVariable _ -> Ok expr
    | MatchAgainst(cols, q, mode) -> sub q |> Result.map (fun q2 -> MatchAgainst(cols, q2, mode))
    | FuncCall(name, args) when isAggregateCall registry expr -> evalAggregate registry ctxFor rows name args |> Result.map Lit
    | FuncCall(name, args) -> args |> traverse sub |> Result.map (fun args' -> FuncCall(name, args'))
    | Row values -> values |> traverse sub |> Result.map Row
    | BinOp(op, a, b) -> sub a |> Result.bind (fun a' -> sub b |> Result.map (fun b' -> BinOp(op, a', b')))
    | AssignUserVariable(name, value) -> sub value |> Result.map (fun value' -> AssignUserVariable(name, value'))
    | Not e -> sub e |> Result.map Not
    | IsNull e -> sub e |> Result.map IsNull
    | IsNotNull e -> sub e |> Result.map IsNotNull
    | IsTrue e -> sub e |> Result.map IsTrue
    | IsFalse e -> sub e |> Result.map IsFalse
    | Distinct e -> sub e |> Result.map Distinct
    | OrderBy(e, dir) -> sub e |> Result.map (fun e' -> OrderBy(e', dir))
    | Like(e, p, cs, esc) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs, esc)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | QuantifiedComparison(e, op, quantifier, select) -> sub e |> Result.map (fun e' -> QuantifiedComparison(e', op, quantifier, select))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Collate(e, name) -> sub e |> Result.map (fun e' -> Collate(e', name))
    | Case(subject, whens, elseBranch) ->
        let subOpt = function
            | Some e -> sub e |> Result.map Some
            | None -> Ok None

        subOpt subject
        |> Result.bind (fun subject' ->
            whens
            |> traverse (fun (c, r) -> sub c |> Result.bind (fun c' -> sub r |> Result.map (fun r' -> c', r')))
            |> Result.bind (fun whens' -> subOpt elseBranch |> Result.map (fun else' -> Case(subject', whens', else'))))
    | Lit _
    | Col _
    | QualifiedCol _
    | Star _
    // A `RowNumberOver`/`LagOver` never reaches a grouped SELECT —
    // `runSelect` sends any select with one to `runWindowedSelect` before
    // the GROUP BY/aggregate check that would otherwise land here even gets
    // evaluated (see `runSelect`'s dispatch) — but a leaf passthrough here
    // is the same "nothing to pre-evaluate" answer `Star`'s already is if
    // that ever changes.
    | WindowOver _
    // A subquery is its own scope with its own grouping — nothing inside it
    // is one of *this* query's aggregate calls to pre-evaluate, even though
    // (via `EvalContext.Outer`) it can still read this query's columns.
    | Exists _
    | Subquery _
    | InSubquery _ -> Ok expr

/// Resolves an `ORDER BY`/`GROUP BY` key that names a `SELECT ... AS alias`
/// (`... ORDER BY n`) or a 1-based projection position (`... ORDER BY 2`,
/// `GROUP BY 1`) against `projections`, falling back to the expression
/// as-is for anything else (an ordinary column, or an expression that just
/// happens not to match any alias).
and private resolvePositionalOrAlias (projections: Projection list) (expr: Expr) : Expr =
    match expr with
    | Lit(VInt n) when n >= 1L && n <= int64 (List.length projections) -> fst projections.[int n - 1]
    | Col name ->
        projections
        |> List.tryPick (function
            | e, Some alias when System.String.Equals(alias, name, System.StringComparison.OrdinalIgnoreCase) -> Some e
            | _ -> None)
        |> Option.defaultValue expr
    | _ -> expr

/// `resolvePositionalOrAlias`'s recursive counterpart — `GROUP BY`/`ORDER
/// BY` keys are almost always a bare alias/column/position at the top
/// level, but `HAVING`'s condition is a full boolean expression with the
/// alias nested somewhere inside it (`HAVING c > 1`, not just `HAVING c`),
/// so a shallow top-level check misses it entirely: `Col "c"` there isn't a
/// real column at all, only the `SELECT` list's own alias, and evaluating
/// it unresolved fails with 1054.
///
/// GROUP BY / HAVING's own column-name priority — the mirror image of
/// ORDER BY's (see `resolveOrderKey`'s doc): a bare name is checked against
/// the FROM-table columns first, and only falls back to the SELECT list's
/// own alias/position when it isn't a FROM-table column at all (real MySQL
/// documents this FROM-first order for GROUP BY/HAVING). A FROM-table match
/// present in more than one joined table is error 1052 "group statement",
/// same wording for both clauses.
and private resolveGroupOrHavingCol (columnIndex: Map<string, int list>) (projections: Projection list) (name: string) : Result<Expr, EvalError> =
    match Map.tryFind (name.ToLowerInvariant()) columnIndex with
    | Some [ _ ] -> Ok(Col name)
    | Some(_ :: _ :: _) -> Error(1052, sprintf "Column '%s' in group statement is ambiguous" name)
    | Some [] | None -> Ok(resolvePositionalOrAlias projections (Col name))

/// `GROUP BY`'s key list: each key is a bare top-level expression, never
/// searched inside a larger tree (unlike `HAVING`'s condition — see
/// `resolveHavingRef`), so a `Col` only ever needs the shallow check above;
/// anything else (a position number, or an expression that's neither) goes
/// through `resolvePositionalOrAlias` unchanged.
and private resolveGroupByRef (columnIndex: Map<string, int list>) (projections: Projection list) (expr: Expr) : Result<Expr, EvalError> =
    match expr with
    | Col name -> resolveGroupOrHavingCol columnIndex projections name
    | _ -> Ok(resolvePositionalOrAlias projections expr)

/// `resolveGroupByRef`'s recursive counterpart for `HAVING`: `HAVING c > 1`'s
/// alias `c` is nested inside a `BinOp`, not bare, so a shallow top-level
/// check misses it. Same shape as `substituteValuesFunc`'s rewrite, but
/// `Result`-threaded because resolving a `Col` can return the
/// ambiguous-FROM-table error 1052.
and private resolveHavingRef (columnIndex: Map<string, int list>) (projections: Projection list) (expr: Expr) : Result<Expr, EvalError> =
    let sub = resolveHavingRef columnIndex projections

    match expr with
    | Placeholder _ -> Ok expr
    | UserVariable _
    | SystemVariable _ -> Ok expr
    | MatchAgainst(cols, q, mode) -> sub q |> Result.map (fun q2 -> MatchAgainst(cols, q2, mode))
    | Col name -> resolveGroupOrHavingCol columnIndex projections name
    | FuncCall(name, args) -> args |> traverse sub |> Result.map (fun args' -> FuncCall(name, args'))
    | Row values -> values |> traverse sub |> Result.map Row
    | BinOp(op, a, b) -> sub a |> Result.bind (fun a' -> sub b |> Result.map (fun b' -> BinOp(op, a', b')))
    | AssignUserVariable(name, value) -> sub value |> Result.map (fun value' -> AssignUserVariable(name, value'))
    | Not e -> sub e |> Result.map Not
    | IsNull e -> sub e |> Result.map IsNull
    | IsNotNull e -> sub e |> Result.map IsNotNull
    | IsTrue e -> sub e |> Result.map IsTrue
    | IsFalse e -> sub e |> Result.map IsFalse
    | Distinct e -> sub e |> Result.map Distinct
    | OrderBy(e, dir) -> sub e |> Result.map (fun e' -> OrderBy(e', dir))
    | Like(e, p, cs, esc) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Like(e', p', cs, esc)))
    | Regexp(e, p) -> sub e |> Result.bind (fun e' -> sub p |> Result.map (fun p' -> Regexp(e', p')))
    | In(e, xs) -> sub e |> Result.bind (fun e' -> xs |> traverse sub |> Result.map (fun xs' -> In(e', xs')))
    | QuantifiedComparison(e, op, quantifier, select) -> sub e |> Result.map (fun e' -> QuantifiedComparison(e', op, quantifier, select))
    | Between(e, lo, hi) ->
        sub e |> Result.bind (fun e' -> sub lo |> Result.bind (fun lo' -> sub hi |> Result.map (fun hi' -> Between(e', lo', hi'))))
    | Cast(e, ty) -> sub e |> Result.map (fun e' -> Cast(e', ty))
    | Collate(e, name) -> sub e |> Result.map (fun e' -> Collate(e', name))
    | Case(subject, whens, elseBranch) ->
        let subOpt =
            function
            | Some e -> sub e |> Result.map Some
            | None -> Ok None

        subOpt subject
        |> Result.bind (fun subject' ->
            whens
            |> traverse (fun (c, r) -> sub c |> Result.bind (fun c' -> sub r |> Result.map (fun r' -> c', r')))
            |> Result.bind (fun whens' -> subOpt elseBranch |> Result.map (fun else' -> Case(subject', whens', else'))))
    | Lit _
    | QualifiedCol _
    | Star _
    | WindowOver _
    // A subquery is its own scope — nothing inside it can be *this*
    // query's projection alias.
    | Exists _
    | Subquery _
    | InSubquery _ -> Ok expr

/// `ORDER BY`'s 1-based projection position (`ORDER BY 2`) — separate from
/// `resolvePositionalOrAlias` because aliases go through
/// `resolveOrderKey`'s output-column matching (which needs
/// to see the ambiguous-alias case `resolvePositionalOrAlias`'s
/// first-match `tryPick` would otherwise hide).
and private resolveOrderPosition (projections: Projection list) (expr: Expr) : Expr =
    match expr with
    | Lit(VInt n) when n >= 1L && n <= int64 (List.length projections) -> fst projections.[int n - 1]
    | _ -> expr

/// `ORDER BY`'s alias-then-FROM-table priority (see `resolveGroupOrHavingCol`'s
/// doc for the opposite order GROUP BY/HAVING use): tries the bare name
/// against `outputCols` — the SELECT list's own output columns, explicit
/// aliases and every name `*`/`t.*` expanded into, in row order — first;
/// exactly one match binds directly to that column's already-computed
/// value, more than one is error 1052 "order clause", and zero falls
/// through to `resolveCol` against the FROM-table columns instead (also
/// tagged `OrderClause`, not `FieldList`).
and private resolveOrderKey
    (ctx: EvalContext)
    (projections: Projection list)
    (outputCols: (string * Value) list)
    (expr: Expr)
    : Result<Value * Collation.Collation option, EvalError> =
    match expr with
    | Col name ->
        match outputCols |> List.filter (fun (n, _) -> System.String.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) with
        | [ (_, v) ] ->
            // An output alias retains its source expression's declared
            // type for sorting (`SELECT role AS r ... ORDER BY r`). A
            // computed alias has no direct ENUM column and stays lexical.
            let sourceExpr =
                projections
                |> List.choose (fun (projectionExpr, alias) ->
                    alias
                    |> Option.filter (fun aliasName ->
                        System.String.Equals(aliasName, name, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun _ -> projectionExpr))
                |> function
                    | [ projectionExpr ] -> projectionExpr
                    | _ -> expr

            Ok(orderValueForExpr { ctx with Clause = OrderClause } sourceExpr v)
        | _ :: _ :: _ -> Error(1052, sprintf "Column '%s' in order clause is ambiguous" name)
        | [] -> evalOrderKey ctx (Col name)
    | e -> evalOrderKey ctx e

/// Only direct columns can map a GROUP BY key to a stored index key.
and private groupByColumnNames (groupExprs: Expr list) : string list option =
    let asColumnName =
        function
        | Col name
        | QualifiedCol(_, name) -> Some name
        | _ -> None

    let names = groupExprs |> List.map asColumnName
    if names |> List.forall Option.isSome then Some(names |> List.map Option.get) else None

/// Only literal equalities prove that a preceding index key is constant.
and private whereEqualityPinnedColumns (whereExpr: Expr option) : Set<string> =
    let rec walk expr acc =
        match expr with
        | BinOp(And, l, r) -> walk r (walk l acc)
        | BinOp(Eq, Col name, Lit _)
        | BinOp(Eq, Lit _, Col name)
        | BinOp(Eq, QualifiedCol(_, name), Lit _)
        | BinOp(Eq, Lit _, QualifiedCol(_, name)) -> Set.add (name.ToLowerInvariant()) acc
        | _ -> acc

    whereExpr |> Option.map (fun e -> walk e Set.empty) |> Option.defaultValue Set.empty

and private groupByIndexPrefix (pinned: Set<string>) (groupColumns: string list) (indexColumns: string list) : string list option =
    let normalized = indexColumns |> List.map (fun column -> column.ToLowerInvariant())

    let rec pinnedCount count =
        function
        | column :: rest when Set.contains column pinned -> pinnedCount (count + 1) rest
        | _ -> count

    let leadingPinned = pinnedCount 0 normalized
    let remaining = List.skip leadingPinned normalized

    if List.length remaining >= List.length groupColumns && List.take groupColumns.Length remaining = groupColumns then
        Some(List.take (leadingPinned + groupColumns.Length) indexColumns)
    else
        None

and private tryGroupIndexOrder (store: Store) (dbName: string) (tref: TableRef) (select: SelectStmt) : IndexOrderPlan option =
    if select.GroupBy.IsEmpty || not (storedValuesMatchReadValues store) then
        None
    else
        groupByColumnNames select.GroupBy
        |> Option.bind (fun groupColumns ->
            let tableDb = tref.Database |> Option.defaultValue dbName

            InformationSchema.findTable store.Catalog tableDb tref.Table
            |> Result.toOption
            |> Option.bind (fun table ->
                let pinned = whereEqualityPinnedColumns select.Where
                let groupColumns = groupColumns |> List.map (fun column -> column.ToLowerInvariant())
                let primaryColumns = Storage.primaryKeyColumns table
                let primary = if primaryColumns.IsEmpty then [] else [ primaryColumns ]

                let secondary =
                    table.Indexes
                    |> List.filter (fun index -> not (System.String.Equals(index.Name, "PRIMARY", System.StringComparison.OrdinalIgnoreCase)))
                    |> List.map _.Columns

                primary @ secondary
                |> List.choose (groupByIndexPrefix pinned groupColumns)
                |> List.tryPick (fun prefix ->
                    Storage.tryCompositeOrderedLookup store tableDb tref.Table (prefix |> List.map (fun column -> column, Asc))))
            |> Option.map (fun lookup ->
                { KeyName = lookup.OrderedIndexName
                  ColumnIndices = lookup.OrderedColumnIndices
                  Columns = lookup.OrderedColumns
                  EstimatedRows = lookup.OrderedRowCount
                  Rows = lookup.OrderedRows }))

and private validateOnlyFullGroupBy
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (select: SelectStmt)
    : Result<unit, EvalError> =
    let columnIndex = columnIndexOf columns

    let tryQualifiedPosition (qualifier: string) (name: string) =
        qualifiers
        |> Map.tryFind (qualifier.ToLowerInvariant())
        |> Option.bind (fun (sourceColumns, offset) ->
            sourceColumns
            |> List.tryFindIndex (fun column -> System.String.Equals(column.Name, name, System.StringComparison.OrdinalIgnoreCase))
            |> Option.map ((+) offset))

    let tryColumnPosition =
        function
        | QualifiedCol(qualifier, name) -> tryQualifiedPosition qualifier name
        | Col name ->
            columns
            |> List.indexed
            |> List.filter (fun (_, column) -> System.String.Equals(column.Name, name, System.StringComparison.OrdinalIgnoreCase))
            |> function
                | [ (position, _) ] -> Some position
                | _ -> None
        | _ -> None

    let expressionColumns expr =
        Expression.fold
            (fun found node ->
                match node with
                | Col _
                | QualifiedCol _ -> Expression.Prune(node :: found)
                | _ -> Expression.Descend found)
            []
            expr
        |> List.rev

    let addEquality (left, right) pairs =
        match tryColumnPosition left, tryColumnPosition right with
        | Some leftPosition, Some rightPosition -> (leftPosition, rightPosition) :: pairs
        | _ -> pairs

    let rec equalityPairs expr pairs =
        match expr with
        | BinOp(And, left, right) -> equalityPairs right (equalityPairs left pairs)
        | BinOp(Eq, left, right) -> addEquality (left, right) pairs
        | _ -> pairs

    let isConstant expr = expressionColumns expr |> List.isEmpty

    let rec singletonColumns expr found =
        match expr with
        | BinOp(And, left, right) -> singletonColumns right (singletonColumns left found)
        | BinOp(Eq, left, right) ->
            match tryColumnPosition left, tryColumnPosition right with
            | Some position, None when isConstant right -> Set.add position found
            | None, Some position when isConstant left -> Set.add position found
            | _ -> found
        | _ -> found

    let physicalSources =
        (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)
        |> List.choose (function
            | FromTable tableRef ->
                let qualifier = tableRef.Alias |> Option.defaultValue tableRef.Table
                let tableDb = tableRef.Database |> Option.defaultValue dbName

                match Map.tryFind (qualifier.ToLowerInvariant()) qualifiers, InformationSchema.findTable store.Catalog tableDb tableRef.Table with
                | Some(sourceColumns, offset), Ok table -> Some(sourceColumns, offset, table)
                | _ -> None
            | _ -> None)

    let uniqueKeys =
        physicalSources
        |> List.collect (fun (sourceColumns, offset, table) ->
            table.Indexes
            |> List.filter (fun index -> index.Unique || System.String.Equals(index.Name, "PRIMARY", System.StringComparison.OrdinalIgnoreCase))
            |> List.choose (fun index ->
                let keyColumns =
                    index.Columns
                    |> List.choose (fun name ->
                        sourceColumns
                        |> List.tryFindIndex (fun column -> System.String.Equals(column.Name, name, System.StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun position -> offset + position, sourceColumns.[position]))

                if keyColumns.Length = index.Columns.Length && keyColumns |> List.forall (snd >> _.Nullable >> not) then
                    Some(keyColumns |> List.map fst, [ offset .. offset + sourceColumns.Length - 1 ])
                else
                    None))

    let resolveGroupExprs () = select.GroupBy |> traverse (resolveGroupByRef columnIndex select.Projections)

    let expandDetermined equalities keys initial =
        let rec expand determined =
            let throughEqualities =
                equalities
                |> List.fold (fun found (left, right) ->
                    if Set.contains left found then Set.add right found
                    elif Set.contains right found then Set.add left found
                    else found) determined

            let throughKeys =
                keys
                |> List.fold (fun found (key, source) ->
                    if key |> List.forall (fun position -> Set.contains position found) then
                        Set.union found (Set.ofList source)
                    else
                        found) throughEqualities

            if throughKeys = determined then determined else expand throughKeys

        expand initial

    let resolveOrderExpr expr =
        match resolveOrderPosition select.Projections expr with
        | Col name as column ->
            select.Projections
            |> List.choose (fun (projection, alias) ->
                alias
                |> Option.filter (fun alias -> System.String.Equals(alias, name, System.StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun _ -> projection))
            |> function
                | [ projection ] -> projection
                | _ -> column
        | resolved -> resolved

    let columnLabel position =
        qualifiers
        |> Map.toList
        |> List.tryPick (fun (qualifier, (sourceColumns, offset)) ->
            let local = position - offset
            sourceColumns |> List.tryItem local |> Option.map (fun column -> sprintf "%s.%s.%s" dbName qualifier column.Name))
        |> Option.defaultValue columns.[position].Name

    let invalidColumn groupExprs determined expr =
        Expression.fold
            (fun invalid node ->
                match invalid with
                | Some _ -> Expression.Prune invalid
                | None when groupExprs |> List.contains node -> Expression.Prune None
                | None when isAggregateCall registry node -> Expression.Prune None
                | None ->
                    match node with
                    | FuncCall(name, _) when System.String.Equals(name, "ANY_VALUE", System.StringComparison.OrdinalIgnoreCase) -> Expression.Prune None
                    | Star None ->
                        [ 0 .. columns.Length - 1 ]
                        |> List.tryFind (fun position -> not (Set.contains position determined))
                        |> Expression.Prune
                    | Star(Some qualifier) ->
                        qualifiers
                        |> Map.tryFind (qualifier.ToLowerInvariant())
                        |> Option.bind (fun (sourceColumns, offset) ->
                            [ offset .. offset + sourceColumns.Length - 1 ]
                            |> List.tryFind (fun position -> not (Set.contains position determined)))
                        |> Expression.Prune
                    | Col _
                    | QualifiedCol _ ->
                        tryColumnPosition node
                        |> Option.filter (fun position -> not (Set.contains position determined))
                        |> Expression.Prune
                    | _ -> Expression.Descend None)
            None
            expr

    let groupingError clause index groupExprs determined expr =
        match invalidColumn groupExprs determined expr with
        | None -> Ok()
        | Some position when groupExprs.IsEmpty && clause = "SELECT list" ->
            Error(
                1140,
                sprintf
                    "In aggregated query without GROUP BY, expression #%d of SELECT list contains nonaggregated column '%s'; this is incompatible with sql_mode=only_full_group_by"
                    index
                    (columnLabel position)
            )
        | Some position ->
            Error(
                1055,
                sprintf
                    "Expression #%d of %s is not in GROUP BY clause and contains nonaggregated column '%s' which is not functionally dependent on columns in GROUP BY clause; this is incompatible with sql_mode=only_full_group_by"
                    index
                    clause
                    (columnLabel position)
            )

    if not store.ExecutionSettings.SqlMode.OnlyFullGroupBy then
        Ok()
    else
        resolveGroupExprs ()
        |> Result.bind (fun groupExprs ->
            let equalities =
                (select.Where |> Option.map (fun expr -> equalityPairs expr []) |> Option.defaultValue [])
                @ (select.Joins |> List.collect (fun join -> equalityPairs join.On []))

            let initial =
                groupExprs
                |> List.collect expressionColumns
                |> List.choose tryColumnPosition
                |> Set.ofList
                |> fun grouped ->
                    select.Where
                    |> Option.map (fun expr -> singletonColumns expr grouped)
                    |> Option.defaultValue grouped

            let determined = expandDetermined equalities uniqueKeys initial

            select.Projections
            |> List.mapi (fun index (expr, _) -> groupingError "SELECT list" (index + 1) groupExprs determined expr)
            |> traverse id
            |> Result.bind (fun _ ->
                select.Having
                |> Option.map (resolveHavingRef columnIndex select.Projections)
                |> Option.defaultValue (Ok(Lit(VInt 1L)))
                |> Result.bind (groupingError "HAVING clause" 1 groupExprs determined))
            |> Result.bind (fun _ ->
                select.OrderBy
                |> List.mapi (fun index (expr, _) -> groupingError "ORDER BY clause" (index + 1) groupExprs determined (resolveOrderExpr expr))
                |> traverse id
                |> Result.map ignore))

and private runGroupedSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (groupInputOrdered: bool)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    let columnIndex = columnIndexOf columns

    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

    let matches = whereMatches ctxFor select.Where

    let representativeOf (groupRows: Value[] list) : Value[] = groupRows |> List.tryHead |> Option.defaultValue (probeRow columns)

    let projectGroup (rollup: Expr -> Expr) (groupRows: Value[] list) : Result<(string * Value) list, EvalError> =
        let representative = representativeOf groupRows

        select.Projections
        |> traverse (fun (expr, aliasOpt) ->
            match expr with
            | Star None -> Ok(columns |> List.mapi (fun i c -> c.Name, representative.[i]))
            | Star(Some qualifier) -> resolveStarQualifier (ctxFor representative) qualifier
            | _ ->
                rewriteAggregates registry ctxFor groupRows (rollup expr)
                |> Result.bind (evalExpr (ctxFor representative))
                |> Result.map (fun v -> [ aliasOpt |> Option.defaultValue (exprLabel expr), v ]))
        |> Result.map List.concat

    let havingOk (rollup: Expr -> Expr) (groupRows: Value[] list) : Result<bool, EvalError> =
        match select.Having with
        | None -> Ok true
        | Some h ->
            // `resolveHavingRef` resolves a `SELECT ... AS alias` anywhere
            // inside the condition (`HAVING`'s condition is a full boolean
            // expression, not just a bare alias — MySQL allows a projection
            // alias nested anywhere inside it, e.g. Eloquent's
            // `having('aggregate_alias', ...)`), FROM-table columns first.
            resolveHavingRef columnIndex select.Projections h
            |> Result.map rollup
            |> Result.bind (rewriteAggregates registry ctxFor groupRows)
            |> Result.bind (evalExpr { ctxFor (representativeOf groupRows) with Clause = GroupStatement })
            |> Result.map (fun v -> truthy v = Some true)

    // ORDER BY's alias-first priority (the opposite of GROUP BY/HAVING's
    // FROM-first one — see `resolveOrderKey`'s doc) resolves against this
    // group's own already-projected output columns (`outputCols`, from
    // `projectGroup`) rather than the group's raw rows.
    let orderKeysOf (rollup: Expr -> Expr) (outputCols: (string * Value) list) (groupRows: Value[] list) : Result<(Value * Collation.Collation option) list, EvalError> =
        let representative = representativeOf groupRows
        let ctx = ctxFor representative

        // WITH ROLLUP materializes every grouped column into a nullable
        // temporary that no longer carries its ENUM type, so MySQL sorts it
        // lexically instead of by declaration ordinal (`ORDER BY status+0`
        // still sees ordinals — that is an expression, not a column ref).
        let orderKeyOf (keyCtx: EvalContext) (expr: Expr) (value: Value) =
            if select.Rollup then
                match value with
                | VString _ -> value, Some(keyCollation keyCtx expr)
                | _ -> value, None
            else
                orderValueForExpr keyCtx expr value

        let evalKey (keyCtx: EvalContext) (expr: Expr) =
            let orderCtx = { keyCtx with Clause = OrderClause }
            evalExpr orderCtx expr |> Result.map (orderKeyOf orderCtx expr)

        select.OrderBy
        |> traverse (fun (expr, _) ->
            match resolveOrderPosition select.Projections expr with
            | Col name ->
                match outputCols |> List.filter (fun (n, _) -> System.String.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) with
                | [ (_, v) ] ->
                    let sourceExpr =
                        select.Projections
                        |> List.choose (fun (projectionExpr, alias) ->
                            alias
                            |> Option.filter (fun aliasName ->
                                System.String.Equals(aliasName, name, System.StringComparison.OrdinalIgnoreCase))
                            |> Option.map (fun _ -> projectionExpr))
                        |> function
                            | [ projectionExpr ] -> projectionExpr
                            | _ -> Col name

                    Ok(orderKeyOf { ctx with Clause = OrderClause } sourceExpr v)
                | _ :: _ :: _ -> Error(1052, sprintf "Column '%s' in order clause is ambiguous" name)
                | [] ->
                    rewriteAggregates registry ctxFor groupRows (rollup (Col name))
                    |> Result.bind (evalKey ctx)
            | e -> rewriteAggregates registry ctxFor groupRows (rollup e) |> Result.bind (evalKey ctx))

    // Schema probe: type-checks WHERE/GROUP BY/HAVING/ORDER BY/projections
    // against an all-NULL row first, the same reasoning as `probeRow`'s
    // other use — an unknown column/function is a schema error independent
    // of whether any row happens to match, or a real `GROUP BY` happens to
    // produce zero groups.
    match select.GroupBy |> traverse (resolveGroupByRef columnIndex select.Projections) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok groupExprs ->

    // `GROUPING(k, ...)` reports, per output row, which of its arguments this
    // row rolled up: bit (argCount - 1 - i) for argument i, so the last
    // argument is the low bit — MySQL's own encoding. Every argument must be
    // a `GROUP BY` key (3602), and the whole function only exists under
    // WITH ROLLUP (1111).
    let groupingCalls =
        (select.Projections |> List.map fst)
        @ (select.OrderBy |> List.map fst)
        @ Option.toList select.Having
        |> List.collect (collectCallsNamed "GROUPING")
        |> List.distinct

    let groupingValue (rolledCount: int) (args: Expr list) : Result<Value, EvalError> =
        let total = List.length groupExprs

        args
        |> List.mapi (fun i arg -> i, arg)
        |> traverse (fun (i, arg) ->
            match groupExprs |> List.tryFindIndex ((=) arg) with
            | Some keyIndex ->
                let rolled = keyIndex >= total - rolledCount
                Ok(if rolled then 1L <<< (List.length args - 1 - i) else 0L)
            | None -> Error(3602, sprintf "Argument #%d of GROUPING function is not in GROUP BY" (i + 1)))
        |> Result.map (List.fold (|||) 0L >> VInt)

    // The per-group expression rewrite: a key this row rolled up reads back
    // as NULL (that is what a super-aggregate row *is*), and each GROUPING
    // call collapses to its computed bitmask.
    let rollupRewrite (rolledCount: int) : Result<Expr -> Expr, EvalError> =
        if not select.Rollup then
            if groupingCalls.IsEmpty then
                Ok id
            else
                Error(1111, "Invalid use of group function")
        else
            groupingCalls
            |> traverse (fun call ->
                match call with
                | FuncCall(_, []) -> Error(1210, "Incorrect arguments to GROUPING function")
                | FuncCall(_, args) -> groupingValue rolledCount args |> Result.map (fun v -> call, Lit v)
                | _ -> Error(1105, "GROUPING collector returned a non-call"))
            |> Result.map (fun groupingPairs ->
                let rolledKeys =
                    groupExprs
                    |> List.mapi (fun i key -> i, key)
                    |> List.filter (fun (i, _) -> i >= List.length groupExprs - rolledCount)
                    |> List.map (fun (_, key) -> key, Lit VNull)

                substituteExprs (groupingPairs @ rolledKeys))

    match rollupRewrite 0 with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeRewrite ->

    match validateOnlyFullGroupBy store registry dbName columns qualifiers select with
    | Error(code, message) -> Err(code, message), [], []
    | Ok() ->

    match
        withMetadataProbe (fun () ->
            matches (probeRow columns)
            |> Result.bind (fun _ -> groupExprs |> traverse (evalExpr (ctxFor (probeRow columns))) |> Result.map ignore)
            |> Result.bind (fun _ -> havingOk probeRewrite [])
            |> Result.bind (fun _ -> projectGroup probeRewrite [])
            |> Result.bind (fun probeProjected ->
                orderKeysOf probeRewrite probeProjected []
                |> Result.map (fun _ -> probeProjected)))
    with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeProjected ->
        let colNames = probeProjected |> List.map fst

        match rows |> traverseSeq (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
        | Error(code, message) -> Err(code, message), [], []
        | Ok matched ->
            let buildGroups () : Result<(Value list * Value[] list) list, EvalError> =
                if groupExprs.IsEmpty then
                    Ok [ [], matched ]
                else
                    let collations = groupExprs |> List.map (keyCollation (ctxFor (probeRow columns)))
                    let comparer = SqlValueKeyComparer(collations, false)
                    let equalityComparer = comparer :> IEqualityComparer<Value[]>
                    let groupIndex = Dictionary<Value[], int>(comparer)
                    let groups = ResizeArray<Value[] * ResizeArray<Value[]>>()
                    let mutable failure = None

                    let addGroup key row =
                        let groupRows = ResizeArray()
                        groupRows.Add row
                        groups.Add(key, groupRows)

                    let addOrdered key row =
                        if groups.Count > 0 && equalityComparer.Equals(fst groups.[groups.Count - 1], key) then
                            let _, groupRows = groups.[groups.Count - 1]
                            groupRows.Add row
                        else
                            addGroup key row

                    let addUnordered key row =
                        match groupIndex.TryGetValue key with
                        | true, index ->
                            let _, groupRows = groups.[index]
                            groupRows.Add row
                        | false, _ ->
                            groupIndex.Add(key, groups.Count)
                            addGroup key row

                    for row in matched do
                        if failure.IsNone then
                            match groupExprs |> traverse (evalExpr (ctxFor row)) with
                            | Error error -> failure <- Some error
                            | Ok values ->
                                let key = Array.ofList values
                                if groupInputOrdered then addOrdered key row else addUnordered key row

                    match failure with
                    | Some error -> Error error
                    | None ->
                        groups
                        |> Seq.map (fun (key, rows) -> List.ofArray key, List.ofSeq rows)
                        |> List.ofSeq
                        |> Ok

            // `WITH ROLLUP` adds one super-aggregate row per dropped GROUP BY
            // suffix. MySQL emits them in key order with each subtotal right
            // after the rows it summarizes and the grand total last, which is
            // exactly what walking the key-sorted groups prefix by prefix
            // produces — so the rollup expansion also fixes the output order
            // that a plain GROUP BY leaves to first-occurrence.
            let expandRollup (groups: (Value list * Value[] list) list) : (int * Value list * Value[] list) list =
                let probeCtx = ctxFor (probeRow columns)
                let tagged keys = List.map2 (orderValueForExpr probeCtx) groupExprs keys
                let ascending = groupExprs |> List.map (fun _ -> Asc)

                let sortedGroups =
                    groups |> List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys ascending (tagged ka) (tagged kb))

                let total = List.length groupExprs

                let rec emit (level: int) (groups: (Value list * Value[] list) list) =
                    if level = total then
                        groups |> List.map (fun (key, rows) -> 0, key, rows)
                    else
                        groups
                        |> List.groupBy (fun (key, _) -> List.truncate (level + 1) key)
                        |> List.collect (fun (prefix, subgroups) ->
                            emit (level + 1) subgroups
                            @ (if level + 1 = total then
                                   []
                               else
                                   [ total - (level + 1), prefix, subgroups |> List.collect snd ]))

                emit 0 sortedGroups @ [ total, [], groups |> List.collect snd ]

            match buildGroups () with
            | Error(code, message) -> Err(code, message), [], []
            | Ok baseGroups ->
                let groups =
                    if select.Rollup then
                        expandRollup baseGroups
                    else
                        baseGroups |> List.map (fun (key, rows) -> 0, key, rows)

                let processGroup
                    (rolledCount: int, key: Value list, groupRows: Value[] list)
                    : Result<((string * Value) list * (Value * Collation.Collation option) list * Value list) option, EvalError> =
                    rollupRewrite rolledCount
                    |> Result.bind (fun rollup ->
                        havingOk rollup groupRows
                        |> Result.bind (fun keep ->
                            if not keep then
                                Ok None
                            else
                                projectGroup rollup groupRows
                                |> Result.bind (fun proj ->
                                    orderKeysOf rollup proj groupRows |> Result.map (fun keys -> Some(proj, keys, key)))))

                match groups |> traverseSeq processGroup with
                | Error(code, message) -> Err(code, message), [], []
                | Ok kept ->

                    let sorted =
                        if select.OrderBy.IsEmpty then
                            // Same reasoning as the plain-`SELECT` `sortRows`
                            // above: an empty `ORDER BY` makes every
                            // comparison a no-op, so skip the sort outright.
                            kept
                        else
                            kept |> List.sortWith (fun (_, ka, _) (_, kb, _) -> compareByOrderKeys (List.map snd select.OrderBy) ka kb)

                    // Declared fsp per output column, same as the plain path
                    // — a bare grouped temporal column (`SELECT dt ... GROUP
                    // BY dt`) still renders its precision; an aggregate over
                    // one (`MAX(dt)`) has no resolvable column type and falls
                    // back to `toText`.
                    let groupCtx = ctxFor (probeRow columns)
                    let groupFormats = outputColumnFormats groupCtx columns select.Projections
                    let groupWireOverrides =
                        outputColumnWireOverridesFor select.Rollup groupCtx columns select

                    let paired =
                        sorted
                        |> List.map (fun (proj, _, _) -> renderOutputCols groupFormats proj, proj |> List.map snd |> Array.ofList)

                    let dedupedPaired = if select.Distinct then paired |> List.distinctBy fst else paired

                    let types =
                        columnMetadataOf (List.length colNames) (sorted |> List.map (fun (proj, _, _) -> proj))
                        |> applyWireOverrides groupWireOverrides
                    let limited =
                        dedupedPaired
                        |> applyLimitOffset (Option.map rowCount select.Limit) (Option.map rowCount select.Offset)
                    ResultSet(colNames, limited |> List.map fst), types, limited |> List.map snd

/// `SELECT ..., ROW_NUMBER() OVER (...) | LAG(expr) OVER (...) [AS alias],
/// ... FROM ...` (see `Ast.Expr.RowNumberOver`/`LagOver`'s docs) — every
/// distinct window function `collectWindowFuncs` finds anywhere among the
/// projections is computed once here, against the WHERE-filtered rows (real
/// MySQL computes a window function after `WHERE`, before
/// `SELECT`/`ORDER BY`/`LIMIT`), then handed back to the ordinary
/// (non-windowed) `runSelect` path as one more real column each: a
/// synthetic trailing `ColumnDef` per window function, appended to
/// `columns`/each row, with every projection's `Star` expanded and every
/// window-function occurrence (bare, or nested inside arithmetic/`CASE`/...)
/// substituted for the matching synthetic `Col` reference — expanding `Star`
/// explicitly here (rather than leaving it for `runSelect`'s own `Star`
/// handling) keeps the synthetic columns out of a bare `SELECT *`'s
/// expansion, so they show up only where a window function itself was
/// written.
/// `SELECT ..., SUM(COUNT(*)) OVER (...) ... GROUP BY ...` — a window
/// function over *grouped* rows. MySQL evaluates windows after grouping, so
/// this splits the query in two: an inner grouped SELECT projecting every
/// group-level leaf (the GROUP BY keys and every aggregate call, including
/// the ones inside a window function's arguments) as a synthetic column,
/// then the ordinary window pass over those grouped rows with each leaf
/// substituted for its synthetic column reference.
and private runGroupedWindowSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (groupInputOrdered: bool)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    let columnIndex = columnIndexOf columns

    match validateOnlyFullGroupBy store registry dbName columns qualifiers select with
    | Error(code, message) -> Err(code, message), [], []
    | Ok() ->

    match select.GroupBy |> traverse (resolveGroupByRef columnIndex select.Projections) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok groupExprs ->

    let aggregates =
        (select.Projections |> List.collect (fst >> collectAggregateCalls registry))
        @ (select.OrderBy |> List.collect (fst >> collectAggregateCalls registry))

    let leaves = (groupExprs @ aggregates) |> List.distinct

    if leaves.IsEmpty then
        // Nothing to group on and nothing to aggregate — not this path's
        // shape; the plain window pass handles it.
        runWindowedSelect store registry dbName columns qualifiers rows select outer
    else

    let leafNames = leaves |> List.mapi (fun i _ -> sprintf "__fsdb_group_%d__" i)
    let replacements = List.map2 (fun leaf name -> leaf, Col name) leaves leafNames

    let innerSelect =
        { select with
            Projections = List.map2 (fun leaf name -> leaf, Some name) leaves leafNames
            Distinct = false
            OrderBy = []
            Limit = None
            Offset = None
            GroupBy = groupExprs }

    let grouped = runGroupedSelect store registry dbName columns qualifiers rows groupInputOrdered innerSelect outer

    match grouped with
    | Err(code, message), _, _ -> Err(code, message), [], []
    | _, groupedMetadata, groupedRows ->
        let groupedColumns =
            deriveColumns
                leafNames
                (List.replicate leafNames.Length store.ExecutionSettings.ConnectionCollation)
                groupedMetadata

        // Each projection keeps the column name it would have had before the
        // rewrite — the substitution below turns its expression into
        // synthetic column references, which must not become its header.
        let rewrite (expr: Expr, alias: string option) =
            substituteExprs replacements expr, Some(alias |> Option.defaultValue (exprLabel expr))

        let outerSelect =
            { select with
                Projections = select.Projections |> List.map rewrite
                From = None
                Joins = []
                Where = None
                GroupBy = []
                Rollup = false
                Having = None
                OrderBy = select.OrderBy |> List.map (fun (e, d) -> substituteExprs replacements e, d) }

        runWindowedSelect
            store
            registry
            dbName
            groupedColumns
            (singleQualifier "__fsdb_grouped__" groupedColumns)
            groupedRows
            outerSelect
            outer

and private runWindowedSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] list)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    // MySQL allows a window function in `ORDER BY` even when no projection
    // carries one (`SELECT v FROM t ORDER BY LAG(v) OVER (...)`), so
    // collection spans both lists — each becomes a synthetic column either
    // way, and the `select'` rewrite below substitutes both lists too.
    let windowFuncs =
        (select.Projections |> List.collect (fst >> collectWindowFuncs))
        @ (select.OrderBy |> List.collect (fst >> collectWindowFuncs))
        |> List.distinct

    if windowFuncs.IsEmpty then
        Err(1064, "runWindowedSelect called without a window-function projection"), [], []
    else

    // MySQL rejects a `HAVING` referencing a windowed projection's alias
    // with a dedicated error (3594, ER_WINDOW_INVALID_WINDOW_FUNC_ALIAS_USE
    // — its errmsg really does end with a stray `.'`) rather than resolving
    // the alias.
    let windowedAliases =
        select.Projections
        |> List.choose (fun (expr, alias) ->
            match alias with
            | Some a when not (collectWindowFuncs expr |> List.isEmpty) -> Some(a.ToLowerInvariant())
            | _ -> None)
        |> Set.ofList

    let havingWindowAlias =
        select.Having
        |> Option.map collectColRefs
        |> Option.defaultValue []
        |> List.tryFind (fun name -> windowedAliases.Contains(name.ToLowerInvariant()))

    match havingWindowAlias with
    | Some alias ->
        Err(
            3594,
            sprintf "You cannot use the alias '%s' of an expression containing a window function in this context.'" alias
        ),
        [],
        []
    | None ->

    let columnIndex = columnIndexOf columns
    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer
    let matches = whereMatches ctxFor select.Where

    match rows |> traverseSeq (fun row -> matches row |> Result.map (fun keep -> if keep then Some row else None)) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok matched ->

        // One partitioned-and-ordered pass per distinct window function.
        // Original row indexes break equal ORDER BY keys so peers retain
        // their input order without requiring a second stable-sort buffer.
        // Every window function's `OVER` clause, resolved: a named window
        // binds to this SELECT's own `WINDOW w AS (...)` list, an inline
        // one is already a spec. MySQL's own error (3579) for an undefined
        // name.
        let rec resolveWindow (windowName: string) (visited: Set<string>) (spec: WindowSpec) : Result<WindowSpec, EvalError> =
            match spec.Inherit with
            | None -> Ok spec
            | Some name ->
                resolveNamedWindow visited name
                |> Result.bind (fun inherited ->
                    if not spec.PartitionBy.IsEmpty then
                        Error(3581, "A window which depends on another cannot define partitioning.")
                    elif inherited.Frame.IsSome then
                        Error(3582, sprintf "Window '%s' has a frame definition, so cannot be referenced by another window." name)
                    elif not inherited.OrderBy.IsEmpty && not spec.OrderBy.IsEmpty then
                        Error(3583, sprintf "Window '%s' cannot inherit '%s' since both contain an ORDER BY clause." windowName name)
                    else
                        Ok
                            { Inherit = None
                              PartitionBy = inherited.PartitionBy
                              OrderBy = if spec.OrderBy.IsEmpty then inherited.OrderBy else spec.OrderBy
                              Frame = spec.Frame })

        and resolveNamedWindow (visited: Set<string>) (name: string) : Result<WindowSpec, EvalError> =
            let key = name.ToLowerInvariant()

            if visited.Contains key then
                Error(3580, "There is a circularity in the window dependency graph.")
            else
                select.Windows
                |> List.tryFind (fun (candidate, _) -> System.String.Equals(candidate, name, System.StringComparison.OrdinalIgnoreCase))
                |> Option.map (fun (candidate, spec) -> resolveWindow candidate (visited.Add key) spec)
                |> Option.defaultValue (Error(3579, sprintf "Window name '%s' is not defined." name))

        let resolveOver (over: OverClause) : Result<WindowSpec, EvalError> =
            match over with
            | OverSpec spec -> resolveWindow "<unnamed window>" Set.empty spec
            | OverName name -> resolveNamedWindow Set.empty name

        // A frame offset (`ROWS BETWEEN <n> PRECEDING ...`) must be a
        // constant — MySQL rejects a column reference there — so it
        // evaluates once, in a no-columns literal context.
        let literalCtx = contextFactory store registry dbName Map.empty Map.empty None [||]

        // A window function's own integer argument (NTILE's bucket count,
        // NTH_VALUE's n, LAG/LEAD's offset) — MySQL reports a bad one as
        // 1210 naming the function.
        let constantInt (funcName: string) (e: Expr) : Result<int64, EvalError> =
            evalExpr literalCtx e
            |> Result.bind (fun v ->
                match v with
                | VInt n -> Ok n
                | VUInt n -> Ok(int64 n)
                | _ -> Error(1210, sprintf "Incorrect arguments to %s" funcName))

        // The numeric position a RANGE frame measures distances along.
        let rangeKeyOf (v: Value) : decimal option =
            match v with
            | VInt i -> Some(decimal i)
            | VUInt u -> Some(decimal u)
            | VDecimal d -> Some d
            | VDouble d when System.Double.IsFinite d && abs d < 7.9e28 -> Some(decimal d)
            | _ -> None

        let computeColumn (windowFunc: Expr) : Result<Value[], EvalError> =
            match windowFunc with
            | WindowOver(fn, over) ->

            resolveOver over
            |> Result.bind (fun spec ->

            let partitionBy = spec.PartitionBy
            let windowOrderBy = spec.OrderBy
            let dirs = windowOrderBy |> List.map snd

            // MySQL's default frame: a running one when the window is
            // ordered, the whole partition when it isn't.
            let frame =
                spec.Frame
                |> Option.defaultValue (
                    if windowOrderBy.IsEmpty then
                        { Unit = FrameRows; Start = UnboundedPreceding; End = UnboundedFollowing }
                    else
                        { Unit = FrameRange; Start = UnboundedPreceding; End = CurrentRow })

            // MySQL names the offending window in every frame error;
            // an inline `OVER (...)` is reported as `<unnamed window>`.
            let windowName =
                match over with
                | OverName name -> name
                | OverSpec _ -> "<unnamed window>"

            let badOffset () : Result<'a, EvalError> =
                Error(
                    3586,
                    sprintf "Window '%s': frame start or end is negative, NULL or of non-integral type" windowName
                )

            let badRangeOrderType () : Result<'a, EvalError> =
                Error(
                    3587,
                    sprintf
                        "Window '%s' with RANGE N PRECEDING/FOLLOWING frame requires exactly one ORDER BY expression, of numeric or temporal type"
                        windowName
                )

            // A `ROWS` offset counts rows: a non-negative integer only.
            let rowsOffset (e: Expr) : Result<int64, EvalError> =
                evalExpr literalCtx e
                |> Result.bind (function
                    | VInt n when n >= 0L -> Ok n
                    | VUInt n -> Ok(int64 n)
                    | _ -> badOffset ())

            let rangeOffset (e: Expr) : Result<RangeOffset, EvalError> =
                evalExpr literalCtx e
                |> Result.bind (fun v ->
                    match rangeKeyOf v, tryIntervalArgument v with
                    | Some distance, _ when distance >= 0M -> Ok(NumericRangeOffset distance)
                    | _, Some(amount, _) when amount >= 0.0 -> Ok(TemporalRangeOffset v)
                    | _ -> badOffset ())

            let hasRangeOffset =
                frame.Unit = FrameRange
                && [ frame.Start; frame.End ]
                   |> List.exists (function BoundPreceding _ | BoundFollowing _ -> true | _ -> false)

            // Oracle-pinned frame validation, all before any row is read.
            let validateFrame () : Result<unit, EvalError> =
                match frame.Start, frame.End with
                | UnboundedFollowing, _ ->
                    Error(3584, sprintf "Window '%s': frame start cannot be UNBOUNDED FOLLOWING." windowName)
                | _, UnboundedPreceding ->
                    Error(3585, sprintf "Window '%s': frame end cannot be UNBOUNDED PRECEDING." windowName)
                // A frame that runs backwards (start after end in bound
                // order) is MySQL's 3586, not an empty result — except
                // between two bounds of the same kind, where `2 PRECEDING
                // AND 1 PRECEDING` is legal and just frames fewer rows.
                | CurrentRow, BoundPreceding _
                | BoundFollowing _, BoundPreceding _
                | BoundFollowing _, CurrentRow -> badOffset () |> Result.map ignore
                | _ when hasRangeOffset && List.length windowOrderBy <> 1 ->
                    Error(
                        3587,
                        sprintf
                            "Window '%s' with RANGE N PRECEDING/FOLLOWING frame requires exactly one ORDER BY expression, of numeric or temporal type"
                            windowName
                    )
                | _ -> Ok()

            let validateRangeOrderType () : Result<unit, EvalError> =
                if not hasRangeOffset then
                    Ok()
                else
                    let offsets =
                        [ frame.Start; frame.End ]
                        |> List.choose (function BoundPreceding expression | BoundFollowing expression -> Some expression | _ -> None)
                        |> traverse rangeOffset

                    offsets
                    |> Result.bind (fun resolvedOffsets ->
                        matched
                        |> traverse (fun row -> windowOrderBy |> List.map fst |> traverse (evalExpr (ctxFor row)))
                        |> Result.bind (fun keys ->
                            match keys |> List.collect id |> List.tryFind (fun value -> value <> VNull) with
                            | Some(VDate _ | VDateTime _ | VTime _)
                                when resolvedOffsets |> List.forall (function TemporalRangeOffset _ -> true | _ -> false) ->
                                Ok()
                            | Some value
                                when rangeKeyOf value |> Option.isSome
                                     && resolvedOffsets |> List.forall (function NumericRangeOffset _ -> true | _ -> false) ->
                                Ok()
                            | None -> Ok()
                            | _ -> badRangeOrderType ()))

            let keyOf (exprs: Expr list) (row: Value[]) : Result<Value list, EvalError> =
                exprs |> traverse (evalExpr (ctxFor row))

            let orderKeyOf (exprs: Expr list) (row: Value[]) : Result<(Value * Collation.Collation option) list, EvalError> =
                exprs |> traverse (evalOrderKey (ctxFor row))

            validateFrame ()
            |> Result.bind validateRangeOrderType
            |> Result.bind (fun () ->
            matched
            |> traverse (fun row ->
                keyOf partitionBy row
                |> Result.bind (fun partKey ->
                    orderKeyOf (windowOrderBy |> List.map fst) row |> Result.map (fun ordKey -> partKey, ordKey, row)))
            |> Result.bind (fun keyed ->
                let partitionCollations = partitionBy |> List.map (keyCollation (ctxFor (probeRow columns)))
                let partitionIndex = Dictionary<Value[], int>(SqlValueKeyComparer(partitionCollations, false))
                let grouped = ResizeArray<ResizeArray<WindowRow>>()

                for item in List.indexed keyed do
                    let _, (partitionKey, _, _) = item
                    let key = Array.ofList partitionKey

                    match partitionIndex.TryGetValue key with
                    | true, index -> grouped.[index].Add item
                    | false, _ ->
                        let group = ResizeArray()
                        group.Add item
                        partitionIndex.Add(key, grouped.Count)
                        grouped.Add group

                let partitions =
                    grouped
                    |> Seq.map (fun group ->
                        let ordered = group.ToArray()

                        ordered
                        |> Array.sortInPlaceWith (fun (leftIndex, (_, leftKey, _)) (rightIndex, (_, rightKey, _)) ->
                            let compared = compareByOrderKeys dirs leftKey rightKey
                            if compared <> 0 then compared else Operators.compare leftIndex rightIndex)

                        ordered)
                    |> List.ofSeq

                // `RANK`'s number for each row in an ORDER BY-sorted partition
                // group: the 1-based position of the first row in its tie
                // group (so ties share a rank and the next distinct value
                // skips ahead by the tie-group's size); an empty window
                // ORDER BY ties every row in the partition together, same as
                // MySQL. `PERCENT_RANK`/`CUME_DIST` reuse this (always
                // non-dense) rather than a separate walk.
                let ranksOf (group: WindowRow[]) : int[] =
                    group
                    |> Array.mapi (fun pos (_, (_, ordKey, _)) -> pos, ordKey)
                    |> Array.mapFold
                        (fun (prevKey, lastRank) (pos, ordKey) ->
                            let tie =
                                match prevKey with
                                | Some pk -> compareByOrderKeys dirs pk ordKey = 0
                                | None -> false

                            let rank = if tie then lastRank else pos + 1
                            rank, (Some ordKey, rank))
                        (None, 0)
                    |> fst

                let ordKeyAt (group: WindowRow[]) (pos: int) =
                    let (_, (_, ordKey, _)) = group.[pos]
                    ordKey

                let rowAt (group: WindowRow[]) (pos: int) =
                    let (_, (_, _, row)) = group.[pos]
                    row

                let peerBounds =
                    System.Collections.Generic.Dictionary<WindowRow[], int[] * int[]>(HashIdentity.Reference)

                let boundsFor group =
                    match peerBounds.TryGetValue group with
                    | true, bounds -> bounds
                    | false, _ ->
                        let lows = Array.zeroCreate group.Length
                        let highs = Array.zeroCreate group.Length
                        let mutable runStart = 0

                        for afterRun in 1 .. group.Length do
                            let runEnded =
                                afterRun = group.Length
                                || compareByOrderKeys dirs (ordKeyAt group (afterRun - 1)) (ordKeyAt group afterRun) <> 0

                            if runEnded then
                                for peer in runStart .. afterRun - 1 do
                                    lows.[peer] <- runStart
                                    highs.[peer] <- afterRun - 1

                                runStart <- afterRun

                        let bounds = lows, highs
                        peerBounds.Add(group, bounds)
                        bounds

                let peerLow group pos =
                    let lows, _ = boundsFor group
                    lows.[pos]

                let peerHigh group pos =
                    let _, highs = boundsFor group
                    highs.[pos]

                let rangeBounds group pos startBound endBound =
                    let selfKey = ordKeyAt group pos |> List.tryHead |> Option.map fst

                    match selfKey with
                    | None -> Ok(peerLow group pos, peerHigh group pos)
                    | Some VNull -> Ok(peerLow group pos, peerHigh group pos)
                    | Some current ->
                        let descending = dirs |> List.tryHead |> Option.map ((=) Desc) |> Option.defaultValue false

                        let fixedIntervalTicks interval =
                            tryIntervalArgument interval
                            |> Option.bind (fun (amount, unit) ->
                                let multiplier =
                                    match unit.ToUpperInvariant() with
                                    | "MICROSECOND" -> Some 10.0
                                    | "SECOND" -> Some(float System.TimeSpan.TicksPerSecond)
                                    | "MINUTE" -> Some(float System.TimeSpan.TicksPerMinute)
                                    | "HOUR" -> Some(float System.TimeSpan.TicksPerHour)
                                    | "DAY" -> Some(float System.TimeSpan.TicksPerDay)
                                    | "WEEK" -> Some(float (System.TimeSpan.TicksPerDay * 7L))
                                    | _ -> None

                                multiplier |> Option.map (fun ticks -> decimal (amount * ticks)))

                        let compareRangeValues left right =
                            match left, right with
                            | VTime value, VDecimal ticks -> System.Decimal.Compare(decimal (Fsdb.Temporal.timeTicks value), ticks)
                            | VDecimal ticks, VTime value -> System.Decimal.Compare(ticks, decimal (Fsdb.Temporal.timeTicks value))
                            | _ -> Value.compare left right

                        let boundary bound =
                            let applyOffset preceding expression =
                                rangeOffset expression
                                |> Result.bind (function
                                    | NumericRangeOffset distance ->
                                        match rangeKeyOf current with
                                        | None -> badRangeOrderType ()
                                        | Some value ->
                                            let direction =
                                                if preceding <> descending then -1M else 1M

                                            Ok(Some(VDecimal(value + direction * distance)))
                                    | TemporalRangeOffset interval ->
                                        let direction = if preceding <> descending then -1.0 else 1.0

                                        match current with
                                        | VTime value ->
                                            fixedIntervalTicks interval
                                            |> Option.map (fun ticks ->
                                                VDecimal(decimal (Fsdb.Temporal.timeTicks value) + decimal direction * ticks)
                                                |> Some
                                                |> Ok)
                                            |> Option.defaultWith badOffset
                                        | _ ->
                                            match tryDateIntervalBinOp direction current interval with
                                            | Some VNull
                                            | None -> badOffset ()
                                            | Some value -> Ok(Some value))

                            match bound with
                            | UnboundedPreceding
                            | UnboundedFollowing -> Ok None
                            | CurrentRow -> Ok(Some current)
                            | BoundPreceding expression -> applyOffset true expression
                            | BoundFollowing expression -> applyOffset false expression

                        boundary startBound
                        |> Result.bind (fun startValue ->
                            boundary endBound
                            |> Result.map (fun endValue ->
                                let within other =
                                    let startsAfter =
                                        startValue
                                        |> Option.forall (fun start ->
                                            let compared = compareRangeValues other start
                                            if descending then compared <= 0 else compared >= 0)

                                    let endsBefore =
                                        endValue
                                        |> Option.forall (fun finish ->
                                            let compared = compareRangeValues other finish
                                            if descending then compared >= 0 else compared <= 0)

                                    startsAfter && endsBefore

                                let inFrame =
                                    group
                                    |> Array.map (fun (_, (_, key, _)) ->
                                        key
                                        |> List.tryHead
                                        |> Option.map fst
                                        |> Option.exists (function VNull -> false | value -> within value))

                                match inFrame |> Array.tryFindIndex id, inFrame |> Array.tryFindIndexBack id with
                                | Some low, Some high -> low, high
                                | _ -> pos, pos - 1))

                // [lo, hi] row indexes (inclusive; `hi < lo` means an empty
                // frame) this row's frame covers within its partition.
                let frameRange group pos : Result<int * int, EvalError> =
                    let last = Array.length group - 1

                    let boundIndex (bound: FrameBound) (isStart: bool) : Result<int, EvalError> =
                        match frame.Unit, bound with
                        | _, UnboundedPreceding -> Ok 0
                        | _, UnboundedFollowing -> Ok last
                        | FrameRows, CurrentRow -> Ok pos
                        | FrameRange, CurrentRow -> Ok(if isStart then peerLow group pos else peerHigh group pos)
                        // Unclamped on purpose: a frame that starts past
                        // the last row (`ROWS BETWEEN 1 FOLLOWING AND 2
                        // FOLLOWING` on the final row) must come out empty,
                        // which clamping to `last` would silently turn into
                        // a one-row frame.
                        | FrameRows, BoundPreceding e -> rowsOffset e |> Result.map (fun n -> pos - int n)
                        | FrameRows, BoundFollowing e -> rowsOffset e |> Result.map (fun n -> pos + int n)
                        | FrameRange, _ -> Ok pos // handled by `rangeBounds` below

                    match frame.Unit, frame.Start, frame.End with
                    | FrameRange, (BoundPreceding _ | BoundFollowing _), _
                    | FrameRange, _, (BoundPreceding _ | BoundFollowing _) ->
                        rangeBounds group pos frame.Start frame.End
                    | _ ->
                        boundIndex frame.Start true
                        |> Result.bind (fun lo ->
                            boundIndex frame.End false |> Result.map (fun hi -> max 0 lo, min last hi))

                let frameRows group pos =
                    frameRange group pos
                    |> Result.map (fun (lo, hi) -> [ for i in lo .. hi -> rowAt group i ])

                // The frame-relative row `FIRST_VALUE`/`LAST_VALUE`/
                // `NTH_VALUE` read, or None when the frame is too short.
                let frameRowAt group pos (pick: int -> int -> int option) =
                    frameRange group pos
                    |> Result.map (fun (lo, hi) -> pick lo hi |> Option.filter (fun i -> i >= lo && i <= hi) |> Option.map (rowAt group))

                let perRow (compute: WindowRow[] -> int -> Result<Value, EvalError>) =
                    partitions
                    |> traverse (fun group ->
                        group
                        |> Array.mapi (fun pos (origIdx, _) -> compute group pos |> Result.map (fun v -> origIdx, v))
                        |> Array.toList
                        |> traverse id)
                    |> Result.map (List.collect id >> Array.ofList)

                let aggregateOver (name: string) (args: Expr list) group pos =
                    frameRows group pos |> Result.bind (fun rows -> evalAggregate registry ctxFor rows name args)

                match fn with
                | WinRowNumber -> perRow (fun _ pos -> Ok(VInt(int64 pos + 1L)))
                | WinRank dense ->
                    partitions
                    |> List.collect (fun group ->
                        if dense then
                            // Dense rank increments by 1 at every tie-group
                            // boundary instead of jumping to the leader's
                            // 1-based position, so it never skips a number.
                            group
                            |> Array.mapFold
                                (fun (prevKey, denseRank) (origIdx, (_, ordKey, _)) ->
                                    let newGroup =
                                        match prevKey with
                                        | Some pk -> compareByOrderKeys dirs pk ordKey <> 0
                                        | None -> true

                                    let rank = if newGroup then denseRank + 1 else denseRank
                                    (origIdx, VInt(int64 rank)), (Some ordKey, rank))
                                (None, 0)
                            |> fst
                            |> Array.toList
                        else
                            Array.map2 (fun (origIdx, _) rank -> origIdx, VInt(int64 rank)) group (ranksOf group)
                            |> Array.toList)
                    |> Array.ofList
                    |> Ok
                | WinPercentRank ->
                    partitions
                    |> List.collect (fun group ->
                        let n = group.Length
                        let ranks = ranksOf group

                        Array.map2
                            (fun (origIdx, _) rank ->
                                origIdx, VDouble(if n <= 1 then 0.0 else float (rank - 1) / float (n - 1)))
                            group
                            ranks
                        |> Array.toList)
                    |> Array.ofList
                    |> Ok
                | WinCumeDist ->
                    // Rows at or before the current row's peer group, over
                    // the partition size — so every peer shares one value.
                    perRow (fun group pos -> Ok(VDouble(float (peerHigh group pos + 1) / float (Array.length group))))
                | WinNTile buckets ->
                    constantInt "ntile" buckets
                    |> Result.bind (fun buckets ->
                        if buckets <= 0L then
                            Error(1210, "Incorrect arguments to ntile")
                        else
                            partitions
                            |> List.collect (fun group ->
                                let n = int64 group.Length
                                let baseSize = n / buckets
                                let remainder = n % buckets

                                // Earlier buckets absorb the remainder (a 10-row
                                // partition into 3 buckets is 4/3/3), matching
                                // MySQL. Computed per row in closed form rather
                                // than materializing a `buckets`-sized array — a
                                // huge NTILE(n) over a tiny table must stay O(rows),
                                // not O(n).
                                let boundary = remainder * (baseSize + 1L)

                                group
                                |> Array.mapi (fun pos (origIdx, _) ->
                                    let p = int64 pos

                                    let bucket =
                                        if p < boundary then p / (baseSize + 1L)
                                        else remainder + (p - boundary) / baseSize

                                    origIdx, VInt(bucket + 1L))
                                |> Array.toList)
                            |> Array.ofList
                            |> Ok)
                | WinLagLead(lead, valueExpr, offsetExpr, deflt) ->
                    // `pos - offset` indexes within the same partition's
                    // ORDER BY-sorted rows — backward for `LAG`, forward for
                    // `LEAD`. Outside the partition is the `default`
                    // argument, or NULL when it was omitted; the frame never
                    // applies (MySQL ignores it for this family).
                    offsetExpr
                    |> Option.map (constantInt "lag/lead")
                    |> Option.defaultValue (Ok 1L)
                    |> Result.bind (fun offset ->
                        let step = if lead then int offset else -(int offset)

                        perRow (fun group pos ->
                            let srcPos = pos + step

                            if srcPos < 0 || srcPos >= Array.length group then
                                match deflt with
                                | Some d -> evalExpr (ctxFor (rowAt group pos)) d
                                | None -> Ok VNull
                            else
                                evalExpr (ctxFor (rowAt group srcPos)) valueExpr))
                | WinFirstValue e ->
                    perRow (fun group pos ->
                        frameRowAt group pos (fun lo _ -> Some lo)
                        |> Result.bind (function
                            | Some row -> evalExpr (ctxFor row) e
                            | None -> Ok VNull))
                | WinLastValue e ->
                    perRow (fun group pos ->
                        frameRowAt group pos (fun _ hi -> Some hi)
                        |> Result.bind (function
                            | Some row -> evalExpr (ctxFor row) e
                            | None -> Ok VNull))
                | WinNthValue(e, nExpr) ->
                    constantInt "nth_value" nExpr
                    |> Result.bind (fun n ->
                        if n < 1L then
                            Error(1210, "Incorrect arguments to nth_value")
                        else
                            perRow (fun group pos ->
                                frameRowAt group pos (fun lo _ -> Some(lo + int n - 1))
                                |> Result.bind (function
                                    | Some row -> evalExpr (ctxFor row) e
                                    | None -> Ok VNull)))
                | WinAggregate(name, args) ->
                    // Oracle-pinned refusals: MySQL 8.4 rejects both of
                    // these as 1235 rather than evaluating them.
                    if System.String.Equals(name, "GROUP_CONCAT", System.StringComparison.OrdinalIgnoreCase) then
                        Error(1235, "This version of MySQL doesn't yet support 'group_concat as window function'")
                    elif args |> List.exists (function Distinct _ -> true | _ -> false) then
                        Error(1235, "This version of MySQL doesn't yet support '<window function>(DISTINCT ..)'")
                    else
                        perRow (aggregateOver name args))
            // Back into `matched`'s own row order: every branch above
            // computes `(original index, value)` pairs partition by
            // partition.
            |> Result.map (fun pairs ->
                let ordered = Array.zeroCreate matched.Length

                for index, value in pairs do
                    ordered.[index] <- value

                ordered)))
            | _ -> Error(1105, "window pre-pass collected a non-window node")

        match windowFuncs |> traverse computeColumn with
        | Error(code, message) -> Err(code, message), [], []
        | Ok computedColumns ->
            let synthetic =
                windowFuncs |> List.mapi (fun i wf -> wf, sprintf "__fsdb_window_%d__" i)

            // The ranking family always produces a number; every other
            // window function can land on NULL (an offset outside the
            // partition, an empty frame, an aggregate over no rows).
            let syntheticColumns =
                synthetic
                |> List.map (fun (wf, name) ->
                    let nullable =
                        match wf with
                        | WindowOver((WinRowNumber | WinRank _ | WinPercentRank | WinCumeDist | WinNTile _), _) -> false
                        | _ -> true

                    metadataOfExpr (ctxFor [||]) wf
                    |> Option.map (fun metadata ->
                        let column =
                            deriveColumns [ name ] [ store.ExecutionSettings.ConnectionCollation ] [ metadata ]
                            |> List.head
                        { column with Nullable = nullable })
                    |> Option.defaultValue (syntheticColumn name (TBigInt false) nullable))

            let extendedColumns = columns @ syntheticColumns

            let extendedRows =
                matched
                |> Seq.mapi (fun idx row -> Array.append row (computedColumns |> List.map (fun col -> col.[idx]) |> Array.ofList))

            let rewriteProjection (expr: Expr, aliasOpt: string option) : (Expr * string option) list =
                match expr with
                | Star None -> columns |> List.map (fun c -> Col c.Name, None)
                | Star(Some qualifier) ->
                    match Map.tryFind (qualifier.ToLowerInvariant()) qualifiers with
                    | Some(cols, _) -> cols |> List.map (fun c -> Col c.Name, None)
                    | None -> [ expr, aliasOpt ]
                | _ ->
                    // A bare (unwrapped) window-function projection with no
                    // explicit alias labels itself like MySQL's function-call
                    // headers (`lead(v) over ()`), never the internal
                    // `__fsdb_window_N__` synthetic column name — anything
                    // wrapping one falls through to `runSelect`'s ordinary
                    // unaliased-label handling instead.
                    let alias =
                        aliasOpt
                        |> Option.orElse (
                            synthetic
                            |> List.tryFind (fun (wf, _) -> wf = expr)
                            |> Option.map (fun _ -> exprLabel expr)
                        )

                    [ substituteWindowFuncs synthetic expr, alias ]

            let select' =
                { select with
                    Projections = select.Projections |> List.collect rewriteProjection
                    OrderBy = select.OrderBy |> List.map (fun (e, dir) -> substituteWindowFuncs synthetic e, dir) }

            let extendedQualifiers =
                qualifiers
                |> Map.add "__fsdb_window__" (syntheticColumns, columns.Length)

            runSelect store registry dbName extendedColumns extendedQualifiers extendedRows false select' outer

and private fullTextScoresForTable (table: Table) (matchNodes: Expr list) =
    let indexColumns =
        table.Indexes
        |> List.filter (fun index -> index.Kind = FullTextIndex && index.Visible)
        |> List.map (fun index ->
            index,
            (index.Columns |> List.map (fun column -> column.ToLowerInvariant()) |> Set.ofList))

    let scoreNode node =
        match node with
        | MatchAgainst(columns, Lit queryValue, mode) ->
            let columns = columns |> List.map (fun column -> column.Name.ToLowerInvariant()) |> Set.ofList

            match indexColumns |> List.tryFind (snd >> (=) columns), Value.toText queryValue with
            | Some(index, _), Some queryText ->
                let fullTextIndex = Map.find index.Name table.FullTextIndexes
                let scores =
                    match mode with
                    | NaturalLanguage -> FullText.naturalScores fullTextIndex queryText
                    | BooleanMode -> FullText.booleanScores fullTextIndex queryText
                    | QueryExpansion -> FullText.expansionScores fullTextIndex queryText

                Ok(node, mode, scores)
            | None, _ -> Error(1191, "Can't find FULLTEXT index matching the column list")
            | _, None -> Error(1210, "Incorrect arguments to AGAINST")
        | MatchAgainst _ -> Error(1210, "Incorrect arguments to AGAINST")
        | _ -> Error(1105, "fulltext pre-pass collected a non-MATCH node")

    matchNodes |> traverse scoreNode

and private fullTextCandidateIds (computed: (Expr * MatchMode * Map<RowId, float>) list) expression =
    let scoreMap node =
        computed
        |> List.tryFind (fun (candidate, _, _) -> candidate = node)
        |> Option.map (fun (_, _, scores) -> scores |> Map.keys |> Set.ofSeq)

    let rec candidates expression =
        match scoreMap expression with
        | Some candidates -> Some candidates
        | None ->
            match expression with
            | BinOp(And, left, right) ->
                match candidates left, candidates right with
                | Some left, Some right -> Some(Set.intersect left right)
                | Some candidates, None
                | None, Some candidates -> Some candidates
                | None, None -> None
            | BinOp(Or, left, right) ->
                match candidates left, candidates right with
                | Some left, Some right -> Some(Set.union left right)
                | _ -> None
            | _ -> None

    candidates expression

and private fullTextPredicatePlan (table: Table) (predicate: Expr) =
    let matchNodes = collectMatchAgainst predicate |> List.distinct

    if matchNodes.IsEmpty then
        Ok None
    else
        fullTextScoresForTable table matchNodes
        |> Result.map (fun computed ->
            let rewrite scoreFor =
                computed
                |> List.map (fun (node, _, scores) -> node, Lit(VDouble(scoreFor scores)))
                |> fun replacements -> substituteExprs replacements predicate

            let rowIds =
                fullTextCandidateIds computed predicate
                |> Option.defaultWith (fun () -> table.RowsArray.Indexed |> Seq.map fst |> Set.ofSeq)

            Some
                { Rows =
                    rowIds
                    |> Set.toList
                    |> List.choose (fun rowId -> table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row))
                  PredicateFor = fun rowId -> rewrite (fun scores -> scores |> Map.tryFind rowId |> Option.defaultValue 0.0)
                  ProbePredicate = rewrite (fun _ -> 0.0) })

/// The fulltext pre-pass: like `runWindowedSelect`, each distinct
/// `MATCH ... AGAINST` becomes a synthetic score column computed over its
/// owning physical table ahead of WHERE. The augmented sources then enter
/// the ordinary join and select pipeline, so score evaluation does not
/// duplicate SQL execution semantics.
and private runFullTextSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    (matchNodes: Expr list)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    let unsupported () = Err(1191, "Can't find FULLTEXT index matching the column list"), [], []
    let sourceItems = (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)

    let physicalSources : Result<FullTextPhysicalSource list, QueryResult> =
        sourceItems
        |> traverse (fun item ->
            match item with
            | FromTable tableRef ->
                tryPhysicalTableRef store dbName tableRef
                |> Result.map (
                    Option.map (fun table ->
                        { Qualifier = fromItemQualifier item
                          Item = item
                          Table = table })
                )
            | _ -> Ok None)
        |> Result.map (List.choose id)

    let indexMatches (table: Table) (columns: MatchColumn list) =
        let names = columns |> List.map (fun column -> column.Name.ToLowerInvariant()) |> Set.ofList

        table.Indexes
        |> List.exists (fun index ->
            index.Kind = FullTextIndex
            && (index.Columns |> List.map (fun column -> column.ToLowerInvariant()) |> Set.ofList) = names)

    let ownerOf (sources: FullTextPhysicalSource list) node =
        match node with
        | MatchAgainst(columns, _, _) ->
            let unqualified = columns |> List.filter (_.Qualifier.IsNone)
            let qualifiers =
                columns
                |> List.choose _.Qualifier
                |> List.distinctBy (fun qualifier -> qualifier.ToLowerInvariant())

            let ambiguous =
                unqualified
                |> List.tryFind (fun column ->
                    sources
                    |> List.filter (fun source ->
                        source.Table.Columns
                        |> List.exists (fun definition ->
                            System.String.Equals(definition.Name, column.Name, System.StringComparison.OrdinalIgnoreCase)))
                    |> List.length
                    |> fun ownerCount -> ownerCount > 1)

            match ambiguous with
            | Some column -> Error(Err(1052, sprintf "Column '%s' in field list is ambiguous" column.Name))
            | None ->
                let candidates =
                    match qualifiers with
                    | [] -> sources |> List.filter (fun source -> indexMatches source.Table columns)
                    | [ qualifier ] ->
                        sources
                        |> List.filter (fun source ->
                            System.String.Equals(source.Qualifier, qualifier, System.StringComparison.OrdinalIgnoreCase)
                            && indexMatches source.Table columns)
                    | _ -> []

                match candidates with
                | [ source ] -> Ok source
                | _ -> Error(Err(1191, "Can't find FULLTEXT index matching the column list"))
        | _ -> Error(Err(1105, "fulltext pre-pass collected a non-MATCH node"))

    match select.From, physicalSources with
    | None, _ -> unsupported ()
    | _, Error error -> error, [], []
    | Some fromItem, Ok sources ->
        match matchNodes |> traverse (fun node -> ownerOf sources node |> Result.map (fun owner -> owner, node)) with
        | Error error -> error, [], []
        | Ok ownedNodes ->
            let prepared =
                sources
                |> traverse (fun (source: FullTextPhysicalSource) ->
                    let nodes =
                        ownedNodes
                        |> List.choose (fun (owner, node) ->
                            if System.String.Equals(owner.Qualifier, source.Qualifier, System.StringComparison.OrdinalIgnoreCase) then Some node else None)

                    if nodes.IsEmpty then
                        Ok
                            { Source = source
                              Scores = []
                              Synthetic = []
                              Columns = source.Table.Columns
                              Rows = source.Table.RowsArray :> Value[] seq }
                    else
                        fullTextScoresForTable source.Table nodes
                        |> Result.mapError (fun (code, message) -> Err(code, message))
                        |> Result.map (fun computed ->
                            let synthetic =
                                computed
                                |> List.map (fun (node, _, _) ->
                                    let index = matchNodes |> List.findIndex ((=) node)
                                    node, sprintf "__fsdb_match_%d__" index)

                            let rowsForExecution =
                                match select.Where |> Option.bind (fullTextCandidateIds computed) with
                                | Some candidates ->
                                    candidates
                                    |> Set.toList
                                    |> List.choose (fun rowId -> source.Table.RowsArray.TryFind rowId |> Option.map (fun row -> rowId, row))
                                | None ->
                                    match source.Item with
                                    | FromTable tableRef when select.Joins.IsEmpty && select.Locking.IsEmpty ->
                                        tryIndexedLookup store dbName tableRef select.Where
                                        |> Option.map snd
                                        |> Option.defaultWith (fun () -> source.Table.RowsArray.Indexed |> List.ofSeq)
                                    | _ -> source.Table.RowsArray.Indexed |> List.ofSeq

                            let columns = source.Table.Columns @ (synthetic |> List.map (fun (_, name) -> syntheticColumn name (TDouble false) false))
                            let rows =
                                rowsForExecution
                                |> Seq.map (fun (rowId, row) ->
                                    Array.append
                                        row
                                        (computed
                                         |> List.map (fun (_, _, scores) -> VDouble(scores |> Map.tryFind rowId |> Option.defaultValue 0.0))
                                         |> Array.ofList))

                            { Source = source
                              Scores = computed
                              Synthetic = synthetic
                              Columns = columns
                              Rows = rows }))

            match prepared with
            | Error error -> error, [], []
            | Ok preparedSources ->
                let replacements =
                    preparedSources
                    |> List.collect (fun plan ->
                        plan.Synthetic
                        |> List.map (fun (node, name) -> node, QualifiedCol(plan.Source.Qualifier, name)))

                let sub expression = substituteExprs replacements expression
                let originals =
                    preparedSources
                    |> List.map (fun plan -> plan.Source.Qualifier.ToLowerInvariant(), plan.Source.Table.Columns)
                    |> Map.ofList

                let overrides =
                    preparedSources
                    |> List.map (fun plan ->
                        plan.Source.Qualifier.ToLowerInvariant(),
                        { Columns = plan.Columns
                          Rows = plan.Rows
                          PhysicalTable = Some plan.Source.Table })
                    |> Map.ofList

                let resolveBase =
                    match Map.tryFind ((fromItemQualifier fromItem).ToLowerInvariant()) overrides with
                    | Some source -> Ok(source.Columns, source.Rows)
                    | None -> resolveFromItem store registry dbName fromItem |> Result.map (fun (columns, rows) -> columns, rows :> Value[] seq)

                match resolveBase with
                | Error error -> error, [], []
                | Ok(baseColumns, baseRows) ->
                    let initial = Ok(([ fromItemQualifier fromItem, baseColumns ], baseRows), [])

                    let joined =
                        select.Joins
                        |> List.fold
                            (fun state join ->
                                state
                                |> Result.bind (fun ((resolved, rows), namesPerJoin) ->
                                    let rewrittenJoin = { join with On = sub join.On }

                                    applyJoin store registry dbName outer overrides (resolved, rows) rewrittenJoin
                                    |> Result.map (fun (sources, rows, names) -> (sources, rows), names :: namesPerJoin)))
                            initial

                    match joined with
                    | Error error -> error, [], []
                    | Ok((resolvedSources, rows), namesPerJoinRev) ->
                        let originalSources =
                            resolvedSources
                            |> List.map (fun (qualifier, columns) ->
                                qualifier,
                                (Map.tryFind (qualifier.ToLowerInvariant()) originals |> Option.defaultValue columns))

                        let namesPerJoin = List.rev namesPerJoinRev
                        let select =
                            if namesPerJoin |> List.forall List.isEmpty then select
                            else rewriteNaturalSelect select originalSources select.Joins namesPerJoin

                        let rewriteProjection (expression, alias) =
                            match expression with
                            | Star None ->
                                originalSources
                                |> List.collect (fun (qualifier, columns) ->
                                    columns |> List.map (fun column -> QualifiedCol(qualifier, column.Name), None))
                            | Star(Some qualifier) ->
                                originalSources
                                |> List.tryFind (fst >> fun source -> System.String.Equals(source, qualifier, System.StringComparison.OrdinalIgnoreCase))
                                |> Option.map (fun (_, columns) -> columns |> List.map (fun column -> QualifiedCol(qualifier, column.Name), None))
                                |> Option.defaultValue [ expression, alias ]
                            | _ ->
                                let label =
                                    alias
                                    |> Option.orElse (
                                        matchNodes
                                        |> List.tryFind ((=) expression)
                                        |> Option.map exprLabel)

                                [ sub expression, label ]

                        let whereNodes = select.Where |> Option.map collectMatchAgainst |> Option.defaultValue []
                        let computed = preparedSources |> List.collect _.Scores
                        let synthetic = preparedSources |> List.collect _.Synthetic

                        let implicitOrder =
                            if select.OrderBy.IsEmpty && select.GroupBy.IsEmpty && not select.Distinct then
                                computed
                                |> List.tryFind (fun (node, mode, _) -> mode <> BooleanMode && List.contains node whereNodes)
                                |> Option.bind (fun (node, _, _) -> synthetic |> List.tryFind (fst >> (=) node))
                                |> Option.bind (fun (node, name) ->
                                    replacements
                                    |> List.tryFind (fst >> (=) node)
                                    |> Option.map (fun (_, replacement) -> [ replacement, Desc ]))
                                |> Option.defaultValue []
                            else
                                []

                        let rewritten =
                            { select with
                                Projections = select.Projections |> List.collect rewriteProjection
                                Joins = select.Joins |> List.map (fun join -> { join with On = sub join.On })
                                Where = select.Where |> Option.map sub
                                Having = select.Having |> Option.map sub
                                GroupBy = select.GroupBy |> List.map sub
                                OrderBy =
                                    if implicitOrder.IsEmpty then select.OrderBy |> List.map (fun (expression, direction) -> sub expression, direction)
                                    else implicitOrder }

                        runSelect
                            store
                            registry
                            dbName
                            (resolvedSources |> List.collect snd)
                            (qualifierRanges resolvedSources)
                            rows
                            false
                            rewritten
                            outer

and private runSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columns: ColumnDef list)
    (qualifiers: Map<string, ColumnDef list * int>)
    (rows: Value[] seq)
    (groupInputOrdered: bool)
    (select: SelectStmt)
    (outer: EvalContext option)
    : QueryResult * ColumnMetadata list * Value[] list =
    let projections, whereExpr, orderBy, limit, offset =
        select.Projections, select.Where, select.OrderBy, Option.map rowCount select.Limit, Option.map rowCount select.Offset

    // A `SELECT` with no `FROM` at all has no columns to expand `*`/`t.*`
    // against — real MySQL rejects it as 1096 rather than emitting a
    // resultset with zero columns, which isn't a legal text-resultset
    // packet and aborts the client's whole session.
    if select.From.IsNone && projections |> List.exists (fst >> function Star _ -> true | _ -> false) then
        Err(1096, "No tables used"), [], []
    elif
        [ select.Having; select.Where ]
        |> List.exists (Option.map collectWindowFuncs >> Option.exists (List.isEmpty >> not))
        || select.GroupBy |> List.exists (collectWindowFuncs >> List.isEmpty >> not)
    then
        // MySQL rejects a window function in WHERE/GROUP BY/HAVING with a
        // dedicated error (3593, ER_WINDOW_INVALID_WINDOW_FUNC_USE) naming
        // the function — its errmsg really does end with a stray `.'`.
        let name =
            match
                (select.Having |> Option.map collectWindowFuncs |> Option.defaultValue [])
                @ (select.Where |> Option.map collectWindowFuncs |> Option.defaultValue [])
                @ (select.GroupBy |> List.collect collectWindowFuncs)
            with
            | WindowOver(fn, _) :: _ -> (windowFnLabel fn).Split('(').[0]
            | _ -> "row_number"

        Err(3593, sprintf "You cannot use the window function '%s' in this context.'" name), [], []
    elif
        projections |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not)
        || orderBy |> List.exists (fst >> collectWindowFuncs >> List.isEmpty >> not)
    then
        // GROUP BY/window functions are honest barriers — every row must be
        // seen before either can produce anything — so `rows` (lazy when a
        // `JOIN`'s hash-probe fed it straight through) is forced here rather
        // than threading `seq` any further than the paths that can actually
        // stop early. A windowed SELECT that *also* groups runs the window
        // pass over the grouped rows, MySQL's own evaluation order.
        let grouping =
            not select.GroupBy.IsEmpty
            || (select.Having |> Option.exists (containsAggregate registry))
            || projections |> List.exists (fst >> containsAggregate registry)
            || orderBy |> List.exists (fst >> containsAggregate registry)
            || projections |> List.exists (fst >> collectAggregateCalls registry >> List.isEmpty >> not)

        if grouping then
            runGroupedWindowSelect store registry dbName columns qualifiers (List.ofSeq rows) groupInputOrdered select outer
        else
            runWindowedSelect store registry dbName columns qualifiers (List.ofSeq rows) select outer
    elif
        not select.GroupBy.IsEmpty
        || (select.Having |> Option.exists (containsAggregate registry))
        || projections |> List.exists (fst >> containsAggregate registry)
        || orderBy |> List.exists (fst >> containsAggregate registry)
    then
        runGroupedSelect store registry dbName columns qualifiers (List.ofSeq rows) groupInputOrdered select outer
    else

    let columnIndex = columnIndexOf columns

    let ctxFor = contextFactory store registry dbName columnIndex qualifiers outer

    // ORDER BY may name a 1-based projection position (`ORDER BY 1`) —
    // resolve that first against the projection list; `resolveOrderKey`
    // below handles the alias/output-column case (and its `*`/`t.*`
    // expansion) itself.
    let resolveOrderExpr = resolveOrderPosition projections

    // A non-aggregate, GROUP-BY-less `HAVING` (e.g. `HAVING v > 5`) filters
    // per-row exactly like `WHERE` — `runGroupedSelect` only owns the case
    // where `HAVING` actually needs a group's aggregated results (see the
    // `containsAggregate`/`GroupBy.IsEmpty` routing above) — so it's just
    // ANDed onto the same per-row `matches` check here.
    let matches (row: Value[]) : Result<bool, EvalError> =
        whereMatches ctxFor whereExpr row
        |> Result.bind (fun keep ->
            if not keep then
                Ok false
            else
                match select.Having with
                | None -> Ok true
                | Some expr -> evalExpr { ctxFor row with Clause = WhereClause } expr |> Result.map (fun v -> truthy v = Some true))

    let projectRow (row: Value[]) : Result<(string * Value) list, EvalError> =
        projections
        |> traverse (evalProjection (ctxFor row) columns)
        |> Result.map List.concat

    // `outputCols` (the row's own projection) is computed once by the
    // caller and threaded in here, rather than re-run per `ORDER BY`
    // key — re-running per key would call `projectRow` three times over
    // on the same row for `ORDER BY a, b, c`, for the same result.
    let orderKeysOf (row: Value[]) (outputCols: (string * Value) list) : Result<(Value * Collation.Collation option) list, EvalError> =
        orderBy |> traverse (fun (expr, _) -> resolveOrderKey (ctxFor row) projections outputCols (resolveOrderExpr expr))

    // The declared fsp per output column, computed once off the probe
    // context — a temporal column renders exactly its fsp digits (see
    // `renderOutputCols`), independent of row values, so it's stable across
    // every row this select emits.
    let outputFormats = outputColumnFormats (ctxFor (probeRow columns)) columns projections
    let outputWireOverrides = outputColumnWireOverrides (ctxFor (probeRow columns)) columns select

    let pairOf (outputCols: (string * Value) list) : string option list * Value[] =
        renderOutputCols outputFormats outputCols, outputCols |> List.map snd |> Array.ofList

    let probe = probeRow columns

    match
        Diagnostics.suppress (fun () ->
            withMetadataProbe (fun () ->
                withSuppressedVariableAssignments (fun () ->
                    matches probe
                    |> Result.bind (fun _ -> projectRow probe)
                    |> Result.bind (fun outputCols -> orderKeysOf probe outputCols |> Result.map (fun _ -> outputCols))))) with
    | Error(code, message) -> Err(code, message), [], []
    | Ok probeProjection when limit = Some 0 ->
        // `LIMIT 0` still resolves row-dependent result metadata, but stored
        // program bodies cannot run for a statement that returns no rows.
        let colNames = probeProjection |> List.map fst

        match
            withMetadataProbe (fun () ->
                rows
                |> traverseSeq (fun row ->
                    matches row
                    |> Result.bind (fun keep -> if keep then projectRow row |> Result.map Some else Ok None)))
        with
        | Error(code, message) -> Err(code, message), [], []
        | Ok allProjected ->
            ResultSet(colNames, []), applyWireOverrides outputWireOverrides (columnMetadataOf (List.length colNames) allProjected), []
    | Ok probeProjection ->
        let colNames = probeProjection |> List.map fst

        // Column wire types are read off whatever rows actually cross the
        // wire (post `DISTINCT`/`LIMIT`/`OFFSET`), not the full matched set
        // — the same data-driven "first non-NULL value" approximation
        // `columnMetadataOf` always used, narrowed to the rows a client can
        // observe. A column that's NULL in every *returned* row
        // but non-NULL further down the matched set (past `LIMIT`)
        // reports `VAR_STRING` instead of that later type; scanning the
        // full matched set just to pick a wire type would defeat the
        // `LIMIT` short-circuit below for every query. Upgrade to schema-
        // declared (not data-driven) column types if this ever bites.
        let typesOf (finalRows: Value[] list) : ColumnMetadata list =
            columnMetadataOf (List.length colNames) (finalRows |> List.map (List.zip colNames << List.ofArray))
            |> applyWireOverrides outputWireOverrides

        // Shared by both `ORDER BY` branches below: evaluates `WHERE`, the
        // projection, and the sort keys for one row, in that order, short-
        // circuiting to `None` on a `WHERE` miss without projecting it.
        // Collation-aware DISTINCT key: string output columns fold to
        // their collation's canonical key, so åge/age dedupe under ai_ci
        // while the emitted text stays the first row's original value.
        let dedupeKeyOf (row: Value[]) (outputCols: (string * Value) list) : string option list =
            let ctx = ctxFor row

            outputCols
            |> List.filter (fun (name, _) -> not (name.StartsWith(insertSelectSourceAliasPrefix, System.StringComparison.Ordinal)))
            |> List.map (fun (name, v) ->
                match v with
                | VString text -> Some((keyCollation ctx (Col name)).KeyOf text)
                | _ -> Value.toText v)

        let evalKeyed (row: Value[]) : Result<((Value * Collation.Collation option) list * (string option list * Value[]) * (string option list)) option, EvalError> =
            matches row
            |> Result.bind (fun keep ->
                if not keep then
                    Ok None
                else
                    projectRow row
                    |> Result.bind (fun outputCols ->
                        orderKeysOf row outputCols |> Result.map (fun keys -> Some(keys, pairOf outputCols, dedupeKeyOf row outputCols))))

        // No `ORDER BY`: `WHERE`/`DISTINCT`/`LIMIT`/`OFFSET` stream lazily
        // through `streamLimited`, which stops pulling rows the moment
        // enough have survived — verified against a real MySQL oracle
        // (`SELECT ..., CAST(bad_json AS JSON) ... LIMIT n` with the poison
        // row past position `n`): no `ORDER BY` means the error is never
        // raised, because the row is never evaluated. `ORDER BY` (below)
        // forces a full scan either way — same oracle, `ORDER BY` on an
        // unindexed column — so only that path keeps evaluating everything.
        if orderBy.IsEmpty then
            let evalPaired (row: Value[]) : Result<(string option list * (string option list * Value[])) option, EvalError> =
                matches row
                |> Result.bind (fun keep ->
                    if keep then
                        projectRow row |> Result.map (fun outputCols -> dedupeKeyOf row outputCols, pairOf outputCols) |> Result.map Some
                    else
                        Ok None)

            match rows |> streamLimited select.Distinct (Option.defaultValue 0 offset) limit evalPaired with
            | Error(code, message) -> Err(code, message), [], []
            | Ok limited -> ResultSet(colNames, limited |> List.map (snd >> fst)), typesOf (limited |> List.map (snd >> snd)), limited |> List.map (snd >> snd)

        // `ORDER BY` + `LIMIT`, no `DISTINCT`: bounded top-(limit+offset)
        // selection (`boundedTopN`) instead of a full sort — still touches
        // every matched row (see above), but keeps only `limit + offset` of
        // them at a time instead of the whole matched set. The `limit +
        // offset` addition happens in `int64` and is clamped back into
        // `int` afterward: the parser clamps a `LIMIT` up to MySQL's
        // 2^64-1 down to `Int32.MaxValue` (a real idiom — "offset with no
        // limit" pagination emits it verbatim), and adding a nonzero
        // `OFFSET` to that in unchecked 32-bit `int` wraps negative.
        elif limit.IsSome && not select.Distinct then
            let dirs = List.map snd orderBy
            let capacity = int (min (int64 (Option.get limit) + int64 (Option.defaultValue 0 offset)) (int64 System.Int32.MaxValue))

            match rows |> boundedTopN capacity (fun (ka, _, _) (kb, _, _) -> compareByOrderKeys dirs ka kb) evalKeyed with
            | Error(code, message) -> Err(code, message), [], []
            | Ok top ->
                let limited = top |> List.map (fun (_, p, _) -> p) |> applyLimitOffset limit offset
                ResultSet(colNames, limited |> List.map fst), typesOf (limited |> List.map snd), limited |> List.map snd

        // Honest barrier: `ORDER BY` with no `LIMIT` (nothing to bound a
        // top-N by) or `DISTINCT` alongside `ORDER BY` (deduping after a
        // bounded top-N could starve rows just outside the window that a
        // dedupe-first pass would have kept) — full materialize, sort,
        // dedupe.
        else
            match rows |> traverseSeq evalKeyed with
            | Error(code, message) -> Err(code, message), [], []
            | Ok keyed ->
                // No `ORDER BY` means every key list is `[]`, so the
                // comparator always returns 0 — skip the sort outright
                // rather than pay for an O(n log n) pass to discover that.
                let sorted =
                    if orderBy.IsEmpty then
                        keyed
                    else
                        keyed |> List.sortWith (fun (ka, _, _) (kb, _, _) -> compareByOrderKeys (List.map snd orderBy) ka kb)

                // Dedupes on the projected columns while still honoring
                // `ORDER BY`'s row order (first occurrence wins) — deduping
                // post-`LIMIT` would undercount, and deduping on the raw
                // pre-projection row would miss two source rows that only
                // agree on the columns actually selected.
                let paired = sorted |> List.map (fun (_, p, d) -> p, d)
                let dedupedPaired = if select.Distinct then paired |> List.distinctBy snd else paired
                let limited = dedupedPaired |> applyLimitOffset limit offset
                ResultSet(colNames, limited |> List.map (fst >> fst)), typesOf (limited |> List.map (fst >> snd)), limited |> List.map (fst >> snd)

/// A reference-identity set of physical rows — `HashIdentity.Reference`
/// rather than `Value[]`'s own structural equality, so two rows that happen
/// to hold identical values are still distinguished, and so the set can be
/// built from a `scan` snapshot taken *before* `Storage.updateRows`/
/// `deleteRows` re-reads the table under its own lock and still match the
/// exact same array instances (true as long as nothing else writes to the
/// table in between — the same single-statement, single-connection
/// assumption every other `scan`-then-mutate call site here already makes).
let private referenceSet (rows: Value[] list) : System.Collections.Generic.HashSet<Value[]> =
    System.Collections.Generic.HashSet<Value[]>(rows, HashIdentity.Reference)

let evaluateExpression (store: Store) (registry: Registry) (dbName: string) (expression: Expr) : Result<Value, QueryResult> =
    let context = contextFactory store registry dbName Map.empty Map.empty None [||]
    evalExpr context expression |> Result.mapError Err

let evaluateRowPredicate
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (qualifier: string)
    (columns: ColumnDef list)
    (row: Value[])
    (expression: Expr)
    : Result<bool, QueryResult> =
    let context = contextFactory store registry dbName (columnIndexOf columns) (singleQualifier qualifier columns) None row
    evalExpr { context with Clause = WhereClause } expression
    |> Result.map (truthy >> (=) (Some true))
    |> Result.mapError Err

/// The physical rows a single-table `UPDATE`/`DELETE` actually mutates:
/// every row `matches` (the `WHERE`, or everything when there's none),
/// ordered by `orderBy` and capped at `limit` — computed up front, against
/// each row's original values, so `ORDER BY`/`LIMIT` see a stable snapshot
/// rather than a moving target as rows get rewritten. Empty `orderBy` with
/// no `limit` is every matching row in scan order (an ordinary `UPDATE`/
/// `DELETE`'s existing behavior); `limit` alone (no `ORDER BY`) still caps
/// the count, in whatever order `rows` was already in — MySQL calls that
/// order "unspecified" for a `LIMIT` with no `ORDER BY`, so scan order is as
/// legitimate a choice as any.
let private selectMutationTargets
    (ctxFor: Value[] -> EvalContext)
    (rows: Value[] list)
    (matches: Value[] -> Result<bool, EvalError>)
    (orderBy: OrderKey list)
    (limit: int option)
    : Result<Value[] list, EvalError> =
    rows
    |> traverse (fun row -> matches row |> Result.map (fun m -> m, row))
    |> Result.bind (fun flagged ->
        let matched = flagged |> List.filter fst |> List.map snd

        if orderBy.IsEmpty then
            Ok matched
        else
            let dirs = orderBy |> List.map snd

            matched
            |> traverse (fun row ->
                orderBy
                |> traverse (fun (e, _) -> evalOrderKey (ctxFor row) e)
                |> Result.map (fun keys -> keys, row))
            |> Result.map (fun keyed -> keyed |> List.sortWith (fun (ka, _) (kb, _) -> compareByOrderKeys dirs ka kb) |> List.map snd))
    |> Result.map (fun ordered ->
        match limit with
        | Some l -> ordered |> List.truncate (max 0 l)
        | None -> ordered)

/// Assigns `assignments` (already resolved to column indices) to a copy of
/// `row`, left-to-right — each right-hand side is evaluated against the row
/// *as mutated by every earlier assignment in the same statement*, matching
/// MySQL's documented `UPDATE` evaluation order (`SET a = 10, b = a` sets
/// `b` to the *new* `a`, not the pre-statement one). A failing right-hand
/// side propagates as an `Error` (as a `StorageError` so it can travel
/// through `Storage.updateRows`'s `updater`) instead of silently writing
/// `VNull` — the difference between "UPDATE failed" and quiet data
/// corruption once a SET expression can fail per row.
let private applyAssignments
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (columnIndex: Map<string, int list>)
    (qualifiers: Map<string, ColumnDef list * int>)
    (assignments: (int * Expr) list)
    (row: Value[])
    : Result<Value[], StorageError> =
    assignments
    |> List.fold
        (fun acc (idx, expr) ->
            acc
            |> Result.bind (fun current ->
                evalExpr (contextFactory store registry dbName columnIndex qualifiers None current) expr
                |> Result.mapError ExpressionError
                |> Result.map (fun v ->
                    let newRow = Array.copy current
                    newRow.[idx] <- v
                    newRow)))
        (Ok(Array.copy row))

/// `ON UPDATE CURRENT_TIMESTAMP` columns not named in this statement's own
/// `SET` list (`assignedIdxs`) get bumped to the current time, but only when
/// `row` (after the explicit assignments) actually differs from `original`
/// — MySQL's documented rule ("no update takes place if all columns are set
/// to their current values"); an `UPDATE t SET x = x` that touches nothing
/// real leaves the auto column alone too. An explicit assignment to the
/// auto column itself always wins, whether or not it changes the value —
/// `assignedIdxs` excludes it from this pass entirely.
let private applyOnUpdateTimestamps
    (mode: TemporalCoercionMode)
    (columns: ColumnDef list)
    (assignedIdxs: Set<int>)
    (original: Value[])
    (row: Value[])
    : Value[] =
    let autoCols =
        columns
        |> List.indexed
        |> List.filter (fun (i, c) -> c.OnUpdateCurrentTimestamp && not (Set.contains i assignedIdxs))

    if autoCols.IsEmpty || row = original then
        row
    else
        let newRow = Array.copy row

        for i, c in autoCols do
            newRow.[i] <- Storage.currentTimestampForColumn mode c

        newRow

/// Shadows every `DirectOnly` extension with a 3102 raiser for the duration
/// of an engine-driven (indirect) evaluation — the eval-time half of
/// DIRECTONLY enforcement (`firstDirectOnlyCall` below is the DDL-time
/// half). A definition can reach evaluation without ever passing the DDL
/// check — a subquery smuggling the call past that traversal, or an object
/// loaded from a data dir persisted before the function was registered —
/// so whatever shape the expression takes, the moment the engine would
/// actually invoke the function it gets the same 3102 the DDL check gives.
/// `what` names the offending context in the message ("generated column",
/// "trigger").
let private shadowDirectOnly (what: string) (registry: Registry) : Registry =
    registry.Extensions
    |> Map.fold
        (fun r name ext ->
            if ext.DirectOnly then
                registerScalar
                    name
                    (fun _ -> raise (SqlError(3102, sprintf "Expression of %s contains a disallowed function: %s" what name)))
                    r
            else
                r)
        registry

/// Computes every `Generated` column of `row` (`CREATE TABLE ... col AS
/// (expr)`) fresh from its other columns' current values, leaving every
/// other column untouched, then validates every enforced CHECK constraint.
/// INSERT, REPLACE, and UPDATE call this on each final candidate before it
/// lands, so a unique index or check spanning a generated column (e.g.
/// Laravel Pulse's `key_hash BINARY(16) AS
/// (unhex(md5(key)))`) sees its real value at collision-detection time
/// instead of a not-yet-computed NULL. Left-to-right column order lets one
/// generated column reference an earlier one in the same row.
/// MySQL's DDL-time restriction errors (3102 nondeterministic fn,
/// 3106 VIRTUAL as PK, 3107 forward reference, 3109 auto_increment ref)
/// aren't validated — misuse degrades to a stale/NULL value, not
/// corruption; add a CREATE/ALTER validation pass if a real workload hits
/// one.
let private validateCheckRow
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (table: string)
    (columns: ColumnDef list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let checks = storedChecks store dbName table |> List.filter _.Enforced

    if checks.IsEmpty then
        Ok row
    else
        let registry = shadowDirectOnly "check constraint" registry
        let ctx = contextFactory store registry dbName (columnIndexOf columns) (singleQualifier table columns) None row

        checks
        |> traverse (fun check ->
            match Parser.parseExpression check.Clause with
            | Result.Error _ -> Error(ExpressionError(3812, sprintf "Check constraint '%s' is invalid." check.Name))
            | Result.Ok expression ->
                evalExpr ctx expression
                |> Result.mapError ExpressionError
                |> Result.bind (fun value ->
                    if truthy value = Some false then
                        Error(ExpressionError(3819, sprintf "Check constraint '%s' is violated." check.Name))
                    else
                        Ok()))
        |> Result.map (fun _ -> row)

let private computeGeneratedRow
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (table: string)
    (columns: ColumnDef list)
    (row: Value[])
    : Result<Value[], StorageError> =
    let generated = columns |> List.choose (fun c -> c.Generated |> Option.map (fun (e, _) -> c, e))

    if generated.IsEmpty then
        validateCheckRow store registry dbName table columns row
    else
        // Eval-time DIRECTONLY backstop — see `shadowDirectOnly`'s doc.
        let registry = shadowDirectOnly "generated column" registry

        let row' = Array.copy row

        let ctx = contextFactory store registry dbName (columnIndexOf columns) (singleQualifier table columns) None row'

        // `ctx.Row` holds `row'` by reference, so mutating it in place
        // right after each column's evaluated (rather than collecting then
        // applying afterwards) lets a later generated column's expression
        // see an earlier one's freshly computed value.
        generated
        |> traverse (fun (col, expr) ->
            evalExpr ctx expr
            |> Result.mapError ExpressionError
            |> Result.bind (fun v -> coerceValue store.ExecutionSettings.SqlMode.Strict col v)
            |> Result.map (fun v' ->
                match resolveColumn columns col.Name with
                | Ok idx -> row'.[idx] <- v'
                | Error _ -> ()))
        |> Result.bind (fun _ -> validateCheckRow store registry dbName table columns row')

/// Backfills generated columns after ALTER adds or changes one. Ordinary
/// writes prepare only their candidate rows through `computeGeneratedRow`;
/// ALTER is the one operation that deliberately revisits the whole table.
let private recomputeGeneratedColumns
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (db: string)
    (table: string)
    (columns: ColumnDef list)
    : Result<unit, StorageError> =
    if columns |> List.exists (fun c -> c.Generated.IsSome) |> not then
        Ok()
    else
        updateRows store db table None (fun _ -> Ok true) (computeGeneratedRow store registry db table columns)
        |> Result.map ignore

/// Threads the generated-column backfill onto an ALTER result, re-scanning
/// the table for its post-ALTER column definitions.
let private withGeneratedRecomputed
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (db: string)
    (table: string)
    (result: Result<'a, StorageError>)
    : Result<'a, StorageError> =
    result
    |> Result.bind (fun r ->
        match scan store db table with
        | Ok(cols, _) -> recomputeGeneratedColumns store registry dbName db table cols |> Result.map (fun () -> r)
        | Error _ -> Ok r)

let private rewriteExprWith = Expression.rewrite

/// Rewrites `VALUES(col)` calls (MySQL's way of referring, inside an
/// `INSERT ... ON DUPLICATE KEY UPDATE` assignment, to the value that row
/// would have inserted) into the literal `candidate` value for that column —
/// `funcCallAtom` already parses `VALUES(col)` as an ordinary `FuncCall`
/// since it just looks like one syntactically, so this is a plain
/// pre-evaluation rewrite rather than new grammar.
let private substituteValuesFunc (columnIndex: Map<string, int list>) (candidate: Value[]) : Expr -> Expr =
    rewriteExprWith (function
        | FuncCall(name, [ Col c ]) when System.String.Equals(name, "VALUES", System.StringComparison.OrdinalIgnoreCase) ->
            // `candidate` is always the row for the one table this INSERT
            // targets, so there's no cross-table ambiguity to consider here
            // the way `resolveCol` has to for a JOIN — just take the column.
            match Map.tryFind (c.ToLowerInvariant()) columnIndex with
            | Some(i :: _) -> Some(Lit candidate.[i])
            | _ -> None
        | _ -> None)

let private insertSelectSourceReferences (assignments: (string * Expr) list) : Expr list =
    let references = ResizeArray<Expr>()

    let qualifierOf = function
        | FromTable table -> table.Alias |> Option.defaultValue table.Table
        | FromSubquery(_, alias)
        | FromLateral(_, alias)
        | FromJsonTable(_, _, _, alias) -> alias

    let rec collect shadowed expression =
        expression
        |> rewriteExprWith (function
            | FuncCall(name, [ Col _ ]) as values
                when name.Equals("VALUES", System.StringComparison.OrdinalIgnoreCase) ->
                Some values
            | Col _ as reference when shadowed |> Set.isEmpty ->
                references.Add reference
                Some reference
            | Col _ as reference -> Some reference
            | QualifiedCol(qualifier, _) as reference when not (shadowed |> Set.contains (qualifier.ToLowerInvariant())) ->
                references.Add reference
                Some reference
            | QualifiedCol _ as reference -> Some reference
            | (Exists select | Subquery select) as subquery ->
                collectSelect shadowed select
                Some subquery
            | InSubquery(value, select) as subquery ->
                collect shadowed value
                collectSelect shadowed select
                Some subquery
            | QuantifiedComparison(value, _, _, select) as subquery ->
                collect shadowed value
                collectSelect shadowed select
                Some subquery
            | WindowOver(windowFunction, over) as window ->
                Expression.windowExpressions windowFunction @ Expression.overExpressions over
                |> List.iter (collect shadowed)
                Some window
            | _ -> None)
        |> ignore

    and collectSelect inherited select =
        let local =
            (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)
            |> List.map (fun source -> (qualifierOf source).ToLowerInvariant())
            |> Set.ofList

        let shadowed = Set.union inherited local
        select.Projections |> List.iter (fst >> collect shadowed)
        select.Joins |> List.iter (fun join -> collect shadowed join.On)
        select.Where |> Option.iter (collect shadowed)
        select.GroupBy |> List.iter (collect shadowed)
        select.Windows
        |> List.iter (fun (_, spec) ->
            Expression.overExpressions (OverSpec spec) |> List.iter (collect shadowed))
        select.Ctes |> List.iter (fun cte -> collectSelectOrUnion shadowed cte.Body)
        select.Having |> Option.iter (collect shadowed)
        select.OrderBy |> List.iter (fst >> collect shadowed)
        select.Limit |> Option.iter (collect shadowed)
        select.Offset |> Option.iter (collect shadowed)

    and collectSelectOrUnion shadowed = function
        | PlainSelect select -> collectSelect shadowed select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            collectSelect shadowed first
            rest |> List.iter (snd >> collectSelect shadowed)
            orderBy |> List.iter (fst >> collect shadowed)
            limit |> Option.iter (collect shadowed)
            offset |> Option.iter (collect shadowed)

    assignments |> List.iter (snd >> collect Set.empty)

    references |> Seq.distinct |> List.ofSeq

let private prepareInsertSelectSourceBindings
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (targetColumns: ColumnDef list)
    (select: SelectStmt)
    (assignments: (string * Expr) list)
    : Result<SelectStmt * (Expr * ColumnDef option) list, EvalError> =
    let sources =
        (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)
        |> List.map (fun source -> source, selectSourceColumns store dbName source)

    let matchingColumns reference =
        let candidates, name =
            match reference with
            | Col name -> sources, name
            | QualifiedCol(qualifier, name) -> sources |> List.filter (fst >> sourceHasQualifier qualifier), name
            | _ -> [], ""

        candidates
        |> List.collect snd
        |> List.choose id
        |> List.filter (fun column -> column.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))

    let targetHas name =
        targetColumns |> List.exists (fun column -> column.Name.Equals(name, System.StringComparison.OrdinalIgnoreCase))

    let references = insertSelectSourceReferences assignments

    let bindings =
        references
        |> List.choose (fun reference ->
            let matches =
                match reference with
                | QualifiedCol(qualifier, _) -> sources |> List.filter (fst >> sourceHasQualifier qualifier) |> List.length
                | _ -> matchingColumns reference |> List.length

            match reference, matches with
            | Col name, count when count > 0 && targetHas name -> Some(Error(1052, sprintf "Column '%s' in field list is ambiguous" name))
            | _, 1 -> Some(Ok(reference, matchingColumns reference |> List.tryExactlyOne))
            | _, count when count > 1 -> Some(Error(1052, sprintf "Column '%s' in field list is ambiguous" (exprLabel reference)))
            | _ -> None)

    match bindings |> List.tryPick (function Error error -> Some error | Ok _ -> None) with
    | Some error -> Error error
    | None ->
        let sourceBindings = bindings |> List.choose (function Ok binding -> Some binding | Error _ -> None)

        let grouped =
            not select.GroupBy.IsEmpty
            || (select.Projections |> List.exists (fst >> containsAggregate registry))
            || (select.Having |> Option.exists (containsAggregate registry))
            || (select.OrderBy |> List.exists (fst >> containsAggregate registry))

        match sourceBindings with
        | (first, _) :: _ when grouped -> Error(1054, sprintf "Unknown column '%s' in 'field list'" (exprLabel first))
        | _ ->
            let hidden =
                sourceBindings
                |> List.mapi (fun index (reference, _) -> reference, Some(insertSelectSourceAliasPrefix + string index))

            Ok({ select with Projections = select.Projections @ hidden }, sourceBindings)

let private withInsertSelectSources
    (target: EvalContext)
    (bindings: (Expr * ColumnDef option * Value) list)
    : EvalContext =
    let boundColumn reference column =
        let name =
            match reference with
            | Col name
            | QualifiedCol(_, name) -> name
            | _ -> insertSelectSourceAliasPrefix

        column |> Option.defaultValue (syntheticColumn name (TVarchar 255) true)

    let known = bindings |> List.map (fun (reference, column, value) -> reference, boundColumn reference column, value)

    let groupKey = function
        | QualifiedCol(qualifier, _) -> qualifier.ToLowerInvariant()
        | _ -> insertSelectSourceAliasPrefix

    let groups = known |> List.groupBy (fun (reference, _, _) -> groupKey reference)

    let columns, values, qualifiers, _ =
        groups
        |> List.fold
            (fun (allColumns, allValues, qualifiers, offset) (qualifier, entries) ->
                let columns = entries |> List.map (fun (_, column, _) -> column)
                let values = entries |> List.map (fun (_, _, value) -> value)

                allColumns @ columns,
                allValues @ values,
                qualifiers |> Map.add qualifier (columns, offset),
                offset + columns.Length)
            ([], [], Map.empty, 0)

    if columns.IsEmpty then
        target
    else
        contextFactory target.Store target.Registry target.DbName (columnIndexOf columns) qualifiers (Some target) (Array.ofList values)

let private tryUpdatableView (store: Store) (dbName: string) (viewName: string) : UpdatableView option =
    match tryStoredView store dbName viewName with
    | None -> None
    | Some view ->
        match Parser.parse view.Definition with
        | Ok(Select select) -> updatableViewOfSelect store view select
        | _ -> None

let private rewriteViewExpression (view: UpdatableView) (expression: Expr) =
    rewriteExprWith (function
        | FuncCall(name, [ Col column ]) when name.Equals("VALUES", System.StringComparison.OrdinalIgnoreCase) ->
            view.Columns
            |> Map.tryFind (column.ToLowerInvariant())
            |> Option.map (fun baseColumn -> FuncCall(name, [ Col baseColumn ]))
        | Col name ->
            match Map.tryFind (name.ToLowerInvariant()) view.Expressions with
            | Some expression -> Some expression
            | None -> Some(QualifiedCol("__fsdb_view", name))
        | QualifiedCol(_, name) ->
            match Map.tryFind (name.ToLowerInvariant()) view.Expressions with
            | Some expression -> Some expression
            | None -> Some(QualifiedCol("__fsdb_view", name))
        | _ -> None) expression

let private resolveViewColumn (view: UpdatableView) (column: string) =
    match Map.tryFind (column.ToLowerInvariant()) view.Columns with
    | Some baseColumn -> Ok baseColumn
    | None when view.OrderedColumns |> List.exists (fun name -> name.Equals(column, System.StringComparison.OrdinalIgnoreCase)) ->
        Error(Err(1348, sprintf "Column '%s' is not updatable" column))
    | None -> Error(Err(1054, sprintf "Unknown column '%s' in field list" column))

let private resolveViewInsertTarget (view: UpdatableView) (columns: string list) =
    columns
    |> traverse (fun column ->
        match Map.tryFind (column.ToLowerInvariant()) view.Targets with
        | Some target -> Ok target
        | None when view.OrderedColumns |> List.exists (fun name -> name.Equals(column, System.StringComparison.OrdinalIgnoreCase)) ->
            Error(Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" view.ViewName))
        | None -> Error(Err(1054, sprintf "Unknown column '%s' in field list" column)))
    |> Result.bind (fun targets ->
        let targetKeys = targets |> List.map (fun target -> target.Database, target.Table, target.Qualifier) |> List.distinct

        match targetKeys with
        | [ targetKey ] when Set.contains targetKey view.InsertableTargets ->
            Ok(List.head targets, targets |> List.map _.Column)
        | _ :: _ :: _ -> Error(Err(1393, sprintf "Can not modify more than one base table through a join view '%s.%s'" view.ViewDatabase view.ViewName))
        | _ -> Error(Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" view.ViewName)))

let private validateViewAssignmentTarget (view: UpdatableView) (target: ViewColumnTarget) (assignments: (string * Expr) list) =
    assignments
    |> traverse (fun (column, _) ->
        match Map.tryFind (column.ToLowerInvariant()) view.Targets with
        | Some assignmentTarget -> Ok assignmentTarget
        | None when view.OrderedColumns |> List.exists (fun name -> name.Equals(column, System.StringComparison.OrdinalIgnoreCase)) ->
            Error(Err(1348, sprintf "Column '%s' is not updatable" column))
        | None -> Error(Err(1054, sprintf "Unknown column '%s' in field list" column)))
    |> Result.bind (fun targets ->
        if targets |> List.forall (sameViewTarget target) then
            Ok()
        else
            Error(Err(1393, sprintf "Can not modify more than one base table through a join view '%s.%s'" view.ViewDatabase view.ViewName)))

let private rewriteViewAssignments (view: UpdatableView) (assignments: (string * Expr) list) =
    let rewrite = rewriteViewExpression view

    assignments
    |> traverse (fun (column, value) ->
        resolveViewColumn view column
        |> Result.map (fun baseColumn -> baseColumn, rewrite value))

let private combineViewPredicate predicate whereClause =
    match predicate, whereClause with
    | Some predicate, Some whereClause -> Some(BinOp(And, predicate, whereClause))
    | Some predicate, None -> Some predicate
    | None, whereClause -> whereClause

/// One row of `EXPLAIN`'s classic 12-column tabular output. `Id`/`Table` are
/// `option` since a few rows render `NULL` there (`UNION RESULT`'s `Id`
/// doesn't belong to any one branch; a from-less `SELECT 1`'s `Table`
/// doesn't name one) — `None` renders `NULL` the same way every other
/// resultset cell already does.
type private ExplainRow =
    { Id: int option
      SelectType: string
      Table: string option
      Type: string option
      /// `Some(keyName, keyLen)` when execution reads this table through a
      /// one-column equality index.
      Key: (string * int option) option
      Ref: string option
      Rows: uint64 option
      Extra: string list }

/// Every subquery `expr` embeds, in encounter order — `EXPLAIN`'s source of
/// `SUBQUERY`/`DEPENDENT SUBQUERY` rows, one nested block per subquery form
/// found this way.
let private collectSubqueries = Expression.collectSubqueries

/// Whether `expr` contains a subquery form (`Exists`/`Subquery`/
/// `InSubquery`) anywhere inside it — the same walk `collectSubqueries`
/// already does, asked as a yes/no, for `EXPLAIN`'s `SIMPLE` vs. `PRIMARY`.
let private containsSubqueryExpr (expr: Expr) : bool = not (collectSubqueries expr).IsEmpty

/// Every expression position a `SELECT` can hide a subquery in — one list,
/// read both by `explainSelectBlock` (which blocks to emit for each
/// embedded subquery) and by the `SIMPLE`-vs-`PRIMARY` check in
/// `explainStatement`, so the two can't drift out of sync the way a second
/// hand-written copy of this list did (missing `GroupBy`/`OrderBy`).
let private selectSubqueryExprs (select: SelectStmt) : Expr list =
    (select.Projections |> List.map fst)
    @ (select.Where |> Option.toList)
    @ (select.Having |> Option.toList)
    @ select.GroupBy
    @ (select.OrderBy |> List.map fst)

/// `EXPLAIN`'s `type`/`rows` pair for one real (or `information_schema`
/// virtual) table: `system` for a table with at most one row, `ALL`
/// otherwise, and the table's actual current row count. `EXPLAIN` still
/// describes a real statement, so a table that doesn't exist is 1146 here
/// too, same as it would be if the statement actually ran — not a fake
/// plan with `rows = NULL`.
let private explainTableStats (store: Store) (registry: Registry) (dbName: string) (tableRef: TableRef) : Result<uint64 option * string, QueryResult> =
    let tableDb = tableRef.Database |> Option.defaultValue dbName

    let rowCountResult =
        if System.String.Equals(tableDb, "information_schema", System.StringComparison.OrdinalIgnoreCase) then
            match InformationSchema.scan store.Catalog tableRef.Table (Some(describeStoredViewColumns store registry)) with
            | Some(_, rows) -> Ok(uint64 (List.length rows))
            | None -> Error(storageErr (NoSuchTable tableRef.Table))
        else
            scan store tableDb tableRef.Table |> Result.mapError storageErr |> Result.map (snd >> Seq.length >> uint64)

    rowCountResult |> Result.map (fun n -> Some n, (if n <= 1UL then "system" else "ALL"))

/// One `EXPLAIN` block's table rows (`from` plus every `join`, in order),
/// recursing into a `FROM (SELECT ...) AS alias`'s own block (`DERIVED`)
/// and every subquery `subqueryExprs` embeds (`SUBQUERY`/`DEPENDENT
/// SUBQUERY`) — the shared plumbing `explainSelectBlock` (a real `SELECT`)
/// and `execute`'s `UPDATE`/`DELETE ... JOIN` explain handling both drive,
/// since neither `Ast.UpdateStmt` nor `Ast.DeleteStmt` has a `SelectStmt` to
/// hand this the way a `SELECT`/derived table does.
/// MySQL's `key_len` for a one-column unique key: the key part's byte
/// length as real MySQL reports it (utf8mb4 chars are 4 bytes each, `VAR*`
/// adds a 2-byte length prefix, a nullable column adds 1 for the null
/// flag); `None` for a type MySQL wouldn't report a fixed length on.
let private explainKeyLen (col: ColumnDef) : int option =
    let characterBytes = Collation.maxBytesPerCharacter col.Charset

    let baseLen =
        match col.Type with
        | TTinyInt _
        | TBool -> Some 1
        | TSmallInt _ -> Some 2
        | TMediumInt _ -> Some 3
        | TInt _ -> Some 4
        | TBigInt _ -> Some 8
        | TChar n -> Some(n * characterBytes)
        | TVarchar n -> Some(n * characterBytes + 2)
        | TBinary n -> Some n
        | TVarBinary n -> Some(n + 2)
        | TFloat _ -> Some 4
        | TDouble _ -> Some 8
        | TDate -> Some 3
        | TEnum values -> Some(if List.length values <= 255 then 1 else 2)
        | _ -> None

    // A PRIMARY KEY column is implicitly NOT NULL in MySQL even when the
    // DDL never said so (and this engine's ColumnDef keeps `Nullable = true`
    // for it), so only a genuinely nullable unique column pays the null flag.
    baseLen |> Option.map (fun n -> if col.Nullable && not col.PrimaryKey then n + 1 else n)

let private explainCompositeKeyLen (columns: ColumnDef list) (indices: int list) : int option =
    indices
    |> traverse (fun index ->
        match explainKeyLen columns.[index] with
        | Some length -> Ok length
        | None -> Error())
    |> Result.toOption
    |> Option.map List.sum

let private explainPrefixKeyLen (column: ColumnDef) (prefixLength: int option) =
    let narrowed =
        match prefixLength, column.Type with
        | Some length, TChar _ -> { column with Type = TChar length }
        | Some length, TVarchar _
        | Some length, TTinyText
        | Some length, TText
        | Some length, TMediumText
        | Some length, TLongText -> { column with Type = TVarchar length }
        | Some length, TBinary _ -> { column with Type = TBinary length }
        | Some length, TVarBinary _
        | Some length, TTinyBlob
        | Some length, TBlob
        | Some length, TMediumBlob
        | Some length, TLongBlob -> { column with Type = TVarBinary length }
        | _ -> column

    explainKeyLen narrowed

let private explainIndexKeyLen (columns: ColumnDef list) (indices: int list) (prefixLengths: int option list) =
    List.zip indices prefixLengths
    |> traverse (fun (index, prefixLength) ->
        match explainPrefixKeyLen columns.[index] prefixLength with
        | Some length -> Ok length
        | None -> Error())
    |> Result.toOption
    |> Option.map List.sum

let private tryExplainPhysicalSource (store: Store) (dbName: string) (item: FromItem) : Result<(string * ColumnDef list * Table) option, QueryResult> =
    match item with
    | FromTable tableRef ->
        tryPhysicalTableRef store dbName tableRef
        |> Result.map (Option.map (fun table -> fromItemQualifier item, table.Columns, table))
    | _ -> Ok None

let private leftColumnReference (sources: (string * ColumnDef list * Table) list) (index: int) : string =
    let rec find offset =
        function
        | [] -> failwith "left column index out of range"
        | (qualifier, (columns: ColumnDef list), _) :: rest ->
            if index < offset + columns.Length then
                qualifier + "." + columns.[index - offset].Name
            else
                find (offset + columns.Length) rest

    find 0 sources

let private indexedJoinExplainPlans
    (store: Store)
    (dbName: string)
    (from: FromItem option)
    (joins: Join list)
    : Result<Map<int, IndexedJoinPlan>, QueryResult> =
    let appendSource state item =
        tryExplainPhysicalSource store dbName item
        |> Result.map (fun source ->
            match state, source with
            | Some sources, Some source -> Some(sources @ [ source ])
            | _ -> None)

    let initial =
        match from with
        | None -> Ok(Some [])
        | Some item -> appendSource (Some []) item

    let step plans ((joinIndex, join): int * Join) =
        plans
        |> Result.bind (fun (sources, probes) ->
            tryExplainPhysicalSource store dbName join.Table
            |> Result.map (fun rightSource ->
                let probe =
                    match sources, rightSource with
                    | Some leftSources, Some(rightQualifier, rightColumns, table) ->
                        let leftColumns = leftSources |> List.collect (fun (_, columns, _) -> columns)
                        let qualifiers =
                            (leftSources |> List.map (fun (qualifier, columns, _) -> qualifier, columns))
                            @ [ rightQualifier, rightColumns ]

                        let resolveQualified (qualifier: string) (column: string) =
                            qualifierRanges qualifiers
                            |> Map.tryFind (qualifier.ToLowerInvariant())
                            |> Option.bind (fun (columns, offset) ->
                                columns
                                |> List.tryFindIndex (fun definition -> System.String.Equals(definition.Name, column, System.StringComparison.OrdinalIgnoreCase))
                                |> Option.map (fun columnIndex -> offset + columnIndex, columns.[columnIndex].Type))

                        let coalesceNames =
                            match join.Kind with
                            | NaturalJoin
                            | NaturalLeftJoin
                            | NaturalRightJoin -> naturalCommonNames leftColumns rightColumns
                            | _ -> join.Using

                        let accessKeys =
                            if coalesceNames.IsEmpty then
                                Some(extractEquiKeys resolveQualified leftColumns.Length join.On)
                            else
                                namedEquiKeys leftColumns rightColumns coalesceNames
                                |> Result.toOption
                                |> Option.map (fun keys -> keys, [])

                        accessKeys
                        |> Option.bind (fun (equiKeys, residual) ->
                            tryIndexedJoinProbe store join leftColumns rightColumns (Some table) equiKeys
                            |> Option.map (fun probe ->
                                joinIndex + 1,
                                { Table = probe.Table
                                  KeyName = probe.Index.Name
                                  ColumnIndices = probe.Index.ColumnIndices
                                  PrefixLengths = probe.Index.PrefixLengths
                                  Unique = probe.Index.Unique
                                  References = probe.LeftIndices |> List.map (leftColumnReference leftSources)
                                  HasResidual =
                                    not residual.IsEmpty
                                    || (probe.Index.PrefixLengths |> List.exists Option.isSome)
                                    || (probe.Index.Transforms |> List.exists Option.isSome) }))
                    | _ -> None

                let sources' =
                    match sources, rightSource with
                    | Some leftSources, Some source -> Some(leftSources @ [ source ])
                    | _ -> None

                let probes' =
                    match probe with
                    | Some(index, plan) -> Map.add index plan probes
                    | None -> probes

                sources', probes'))

    joins
    |> List.indexed
    |> List.fold step (initial |> Result.map (fun sources -> sources, Map.empty))
    |> Result.map snd

let rec private explainJoinBlock
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (nextId: unit -> int)
    (acc: ResizeArray<ExplainRow>)
    (id: int)
    (selectType: string)
    (from: FromItem option)
    (joins: Join list)
    (whereOpt: Expr option)
    (extra: string list)
    (subqueryExprs: Expr list)
    (indexOrderPlan: IndexOrderPlan option)
    : Result<unit, QueryResult> =
    let tableCount = (from |> Option.toList |> List.length) + joins.Length

    let emitTableRow (idx: int) (label: string) (rowCount: uint64 option) (typeLabel: string) =
        acc.Add
            { Id = Some id
              SelectType = selectType
              Table = Some label
              Type = Some typeLabel
              Key = None
              Ref = None
              Rows = rowCount
              Extra = (if idx = tableCount - 1 then extra else []) }

    let tryExplainIndexedAccess (tref: TableRef) : bool =
        if tableCount <> 1 then
            false
        else
            let tableDb = tref.Database |> Option.defaultValue dbName

            match tryEqualityAccess store dbName tref whereOpt with
            | Some plan when plan.Unique && plan.Rows.IsEmpty ->
                acc.Add
                    { Id = Some id
                      SelectType = selectType
                      Table = None
                      Type = None
                      Key = None
                      Ref = None
                      Rows = None
                      Extra = [ "no matching row in const table" ] }

                true
            | Some plan ->
                acc.Add
                    { Id = Some id
                      SelectType = selectType
                      Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                      Type = Some(if plan.Unique then "const" else "ref")
                      Key = Some(plan.KeyName, explainIndexKeyLen plan.Columns plan.ColumnIndices plan.PrefixLengths)
                      Ref = Some(String.concat "," (List.replicate plan.ColumnIndices.Length "const"))
                      Rows = Some(uint64 plan.Rows.Length)
                      Extra = if plan.Unique then extra |> List.filter ((<>) "Using where") else extra }

                true
            | None ->
                match tryLiteralInAccess store dbName tref whereOpt with
                | Some plan ->
                    acc.Add
                        { Id = Some id
                          SelectType = selectType
                          Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                          Type = Some "range"
                          Key = Some(plan.KeyName, explainIndexKeyLen plan.Columns plan.ColumnIndices plan.PrefixLengths)
                          Ref = None
                          Rows = Some(uint64 plan.Rows.Length)
                          Extra = extra }

                    true
                | None ->
                    match indexOrderPlan with
                    | Some plan ->
                        let hasBounds =
                            match plan.ColumnIndices with
                            | [ index ] ->
                                rangeLookupBounds BareOrQualifiedRange tref whereOpt
                                |> List.exists (fun bounds ->
                                    System.String.Equals(bounds.Column, plan.Columns.[index].Name, System.StringComparison.OrdinalIgnoreCase))
                            | _ -> false

                        acc.Add
                            { Id = Some id
                              SelectType = selectType
                              Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                              Type = Some(if hasBounds then "range" else "index")
                              Key = Some(plan.KeyName, explainCompositeKeyLen plan.Columns plan.ColumnIndices)
                              Ref = None
                              Rows = Some(uint64 plan.EstimatedRows)
                              Extra = extra }

                        true
                    | None ->
                        rangeLookupBounds BareOrQualifiedRange tref whereOpt
                        |> List.tryPick (fun bounds ->
                            Storage.trySecondaryRangeLookup store tableDb tref.Table bounds.Column bounds.Lower bounds.Upper)
                        |> Option.map (fun lookup ->
                            acc.Add
                                { Id = Some id
                                  SelectType = selectType
                                  Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                                  Type = Some "range"
                                  Key =
                                    Some(
                                        lookup.RangeIndexName,
                                        explainPrefixKeyLen lookup.RangeColumns.[lookup.RangeColumnIndex] lookup.RangePrefixLength
                                    )
                                  Ref = None
                                  Rows = Some(uint64 lookup.RangeRows.Length)
                                  Extra = extra })
                        |> Option.isSome

    /// One `FromItem`'s row(s): a real table's stats, or a derived table's
    /// `<derivedN>` placeholder plus its own recursive `DERIVED` block.
    let explainFromItem (joinPlans: Map<int, IndexedJoinPlan>) (idx: int) (item: FromItem) : Result<unit, QueryResult> =
        match item with
        | FromTable tref ->
            explainTableStats store registry dbName tref
            |> Result.map (fun (n, ty) ->
                match Map.tryFind idx joinPlans with
                | Some plan ->
                    acc.Add
                        { Id = Some id
                          SelectType = selectType
                          Table = Some(tref.Alias |> Option.defaultValue tref.Table)
                          Type = Some(if plan.Unique then "eq_ref" else "ref")
                          Key = Some(plan.KeyName, explainIndexKeyLen plan.Table.Columns plan.ColumnIndices plan.PrefixLengths)
                          Ref = Some(String.concat "," plan.References)
                          Rows = Some 1UL
                          Extra =
                              (if idx = tableCount - 1 then extra else [])
                              @ (if plan.HasResidual then [ "Using where" ] else [])
                              |> List.distinct }
                | None when not (tryExplainIndexedAccess tref) ->
                    emitTableRow idx (tref.Alias |> Option.defaultValue tref.Table) n ty
                | None -> ())
        | FromSubquery(PlainSelect sub, _alias)
        // A LATERAL body plans like any other derived table here; only its
        // per-left-row evaluation differs, which EXPLAIN doesn't model.
        | FromLateral(PlainSelect sub, _alias) ->
            let derivedId = nextId ()
            emitTableRow idx (sprintf "<derived%d>" derivedId) None "ALL"
            explainSelectBlock store registry dbName nextId acc derivedId "DERIVED" sub
        | FromJsonTable(_, _, _, alias) ->
            // A table function has no stats and no derived block — one
            // "ALL" row under its alias, close enough to MySQL's
            // materialized-table-function row for EXPLAIN's purposes.
            emitTableRow idx alias None "ALL"
            Ok()
        | FromSubquery(UnionSelect(first, rest, _, _, _), _alias)
        | FromLateral(UnionSelect(first, rest, _, _, _), _alias) ->
            // Same "DERIVED" + "UNION" per-branch shape as a top-level
            // `Union`'s own `EXPLAIN` (see `explainStatement`'s `Union`
            // case) — a derived table's body can be a `UNION` too
            // (`Ast.SelectOrUnion`'s doc), so it gets the same per-branch
            // rows nested one level under its own `<derivedN>` placeholder.
            let derivedId = nextId ()
            emitTableRow idx (sprintf "<derived%d>" derivedId) None "ALL"

            explainSelectBlock store registry dbName nextId acc derivedId "DERIVED" first
            |> Result.bind (fun () -> rest |> traverse (fun (_, s) -> explainSelectBlock store registry dbName nextId acc (nextId ()) "UNION" s))
            |> Result.map ignore


    indexedJoinExplainPlans store dbName from joins
    |> Result.bind (fun joinPlans ->
        let fromResult = from |> Option.map (explainFromItem joinPlans 0) |> Option.defaultValue (Ok())

        fromResult
        |> Result.bind (fun () -> joins |> List.indexed |> traverse (fun (i, j) -> explainFromItem joinPlans (i + 1) j.Table)))
    |> Result.map (fun _ ->
        if tableCount = 0 then
            acc.Add
                { Id = Some id
                  SelectType = selectType
                  Table = None
                  Type = None
                  Key = None
                  Ref = None
                  Rows = None
                  Extra = [ "No tables used" ] })
    |> Result.bind (fun () ->
        subqueryExprs
        |> List.collect collectSubqueries
        |> traverse (fun sub ->
            let sid = nextId ()
            let stype = if Expression.hasQualifiedOuterReference sub then "DEPENDENT SUBQUERY" else "SUBQUERY"
            explainSelectBlock store registry dbName nextId acc sid stype sub)
        |> Result.map ignore)

/// One `SELECT`'s (or `FROM (SELECT ...)` derived table's) `EXPLAIN` block.
and private explainSelectBlock
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (nextId: unit -> int)
    (acc: ResizeArray<ExplainRow>)
    (id: int)
    (selectType: string)
    (select: SelectStmt)
    : Result<unit, QueryResult> =
    let joins = planJoinOrder store dbName select

    let indexOrderPlan =
        match select.From, joins with
        | Some(FromTable tref), [] ->
            tryIndexOrder store registry dbName tref select
            |> Option.orElseWith (fun () -> tryGroupIndexOrder store dbName tref select)
        | _ -> None

    let extra =
        [ if select.Where.IsSome then "Using where"
          if not select.OrderBy.IsEmpty && indexOrderPlan.IsNone then "Using filesort"
          if (not select.GroupBy.IsEmpty && indexOrderPlan.IsNone) || select.Distinct then "Using temporary" ]

    explainJoinBlock store registry dbName nextId acc id selectType select.From joins select.Where extra (selectSubqueryExprs select) indexOrderPlan

/// Renders every collected `ExplainRow` into `EXPLAIN`'s classic 12-column
/// resultset — `id` ascending, `None -> NULL` in every `option` cell the
/// same way every other resultset already does (see `ExplainRow`'s doc for
/// why `Id`/`Table` are `option` at all). `filtered` is always the honest
/// `100.00` a planner with no statistics can report (this engine has
/// nothing else to estimate it from), present on every real table row and
/// `NULL` only where `type` itself is `NULL` too (a from-less `SELECT 1`, a
/// `UNION RESULT` row).
let private renderExplainRows (rows: ExplainRow list) : QueryResult =
    let columns =
        [ "id"; "select_type"; "table"; "partitions"; "type"; "possible_keys"; "key"; "key_len"; "ref"; "rows"; "filtered"; "Extra" ]

    let renderRow (r: ExplainRow) : string option list =
        [ r.Id |> Option.map string
          Some r.SelectType
          r.Table
          None
          r.Type
          r.Key |> Option.map fst
          r.Key |> Option.map fst
          r.Key |> Option.bind (snd >> Option.map string)
          r.Ref
          r.Rows |> Option.map string
          (r.Type |> Option.map (fun _ -> "100.00"))
          (if r.Extra.IsEmpty then None else Some(String.concat "; " r.Extra)) ]

    ResultSet(columns, rows |> List.sortBy (fun r -> r.Id |> Option.defaultValue System.Int32.MaxValue) |> List.map renderRow)

let private renderExplainJson (rows: ExplainRow list) : QueryResult =
    let jsonArray values =
        let array = JsonArray()
        values |> Seq.iter (fun value -> array.Add(value: JsonNode))
        array

    let tableNode (row: ExplainRow) =
        let table = JsonObject()
        row.Table |> Option.iter (fun value -> table["table_name"] <- JsonValue.Create(value))
        row.Type |> Option.iter (fun value -> table["access_type"] <- JsonValue.Create(value))

        row.Key
        |> Option.iter (fun (key, length) ->
            table["possible_keys"] <- jsonArray [ JsonValue.Create(key) ]
            table["key"] <- JsonValue.Create(key)
            length |> Option.iter (fun value -> table["key_length"] <- JsonValue.Create(string value)))

        row.Ref
        |> Option.iter (fun value -> table["ref"] <- jsonArray [ JsonValue.Create(value) ])

        row.Rows
        |> Option.iter (fun value ->
            table["rows_examined_per_scan"] <- JsonValue.Create(value)
            table["rows_produced_per_join"] <- JsonValue.Create(value))

        row.Type |> Option.iter (fun _ -> table["filtered"] <- JsonValue.Create("100.00"))

        if not row.Extra.IsEmpty then
            table["message"] <- JsonValue.Create(String.concat "; " row.Extra)

        let item = JsonObject()
        item["table"] <- table
        item

    let sorted = rows |> List.sortBy (fun row -> row.Id |> Option.defaultValue System.Int32.MaxValue)
    let queryBlock = JsonObject()

    sorted
    |> List.tryPick _.Id
    |> Option.iter (fun id -> queryBlock["select_id"] <- JsonValue.Create(id))

    match sorted with
    | [ row ] ->
        let item = tableNode row
        queryBlock["table"] <- item["table"].DeepClone()
    | rows -> queryBlock["nested_loop"] <- rows |> List.map (tableNode >> fun node -> node :> JsonNode) |> jsonArray

    let root = JsonObject()
    root["query_block"] <- queryBlock
    let options = JsonSerializerOptions(WriteIndented = true)
    ResultSet([ "EXPLAIN" ], [ [ Some(root.ToJsonString(options)) ] ])

let private renderExplainAnalyze (rows: ExplainRow list) (elapsedMilliseconds: float) (actualRows: int) : QueryResult =
    let planRows =
        rows
        |> List.sortBy (fun row -> row.Id |> Option.defaultValue System.Int32.MaxValue)
        |> List.map (fun row ->
            let source = row.Table |> Option.defaultValue "no tables used"
            let access = row.Type |> Option.defaultValue row.SelectType
            let estimate = row.Rows |> Option.map string |> Option.defaultValue "unknown"
            sprintf "    -> %s on %s (estimated rows=%s)" access source estimate)

    let root =
        sprintf
            "-> fsdb query plan (actual time=0.000..%.3f rows=%d loops=1)"
            elapsedMilliseconds
            actualRows

    ResultSet([ "EXPLAIN" ], (root :: planRows) |> List.map (fun line -> [ Some line ]))

let private renderExplainTree (rows: ExplainRow list) : QueryResult =
    let lines =
        rows
        |> List.sortBy (fun row -> row.Id |> Option.defaultValue System.Int32.MaxValue)
        |> List.map (fun row ->
            let source = row.Table |> Option.defaultValue "no tables used"
            let access = row.Type |> Option.defaultValue row.SelectType
            let estimate = row.Rows |> Option.map string |> Option.defaultValue "unknown"
            let details = if row.Extra.IsEmpty then "" else sprintf " (%s)" (String.concat "; " row.Extra)
            sprintf "-> %s on %s  (rows=%s)%s" access source estimate details)

    ResultSet([ "EXPLAIN" ], lines |> List.map (fun line -> [ Some line ]))

let private checksumTables (store: Store) (dbName: string) (tables: string list) (quick: bool) : QueryResult =
    let checksum tableName =
        let database, table = splitQualified dbName tableName
        let label = database + "." + table

        if quick then
            [ Some label; None ]
        else
            match scan store database table with
            | Error _ -> [ Some label; None ]
            | Ok(_, rows) ->
                let writer = Fsdb.Binary.Writer()
                rows |> Seq.iter (Array.iter (encodeValue writer))
                [ Some label; Some(string (Fsdb.Binary.crc32 (writer.ToArray()))) ]

    ResultSet([ "Table"; "Checksum" ], tables |> List.map checksum)

/// `EXPLAIN [FORMAT=TRADITIONAL] stmt` — a pure description of what
/// `execute` would do with `stmt`, never actually running it. Still
/// validates `stmt` the way actually running it would, though — real MySQL
/// gives 1146 for a table `EXPLAIN` describes that doesn't exist and 1054
/// for a column it doesn't recognize, not a fake plan; `explainJoinBlock`/
/// `explainTableStats` carry that check for every table an `UPDATE`/
/// `DELETE`/`SELECT`/subquery touches, `runSelectStmt` (read-only, so safe
/// to call purely to typecheck and discard) covers a `SELECT`'s columns the
/// same way `QueryHandler`'s real execution path would.
/// Covers every statement shape real MySQL allows `EXPLAIN` on that this
/// engine has a join/subquery structure to describe (`SELECT`, `UNION`,
/// `UPDATE`, `DELETE`, `INSERT` — plain or `... SELECT`); anything else
/// (DDL, session statements, ...) is the same 1064 real MySQL gives.
let rec private explainStatement (format: ExplainFormat) (store: Store) (registry: Registry) (dbName: string) (stmt: Statement) : QueryResult =
    let counter = ref 0

    let nextId () =
        counter.Value <- counter.Value + 1
        counter.Value

    let acc = ResizeArray<ExplainRow>()
    let mutable analyzedMilliseconds = 0.0
    let mutable analyzedRows = 0
    let mutable analyzedStatements = 0

    let finish (result: Result<unit, QueryResult>) =
        match result with
        | Ok() ->
            let rows = List.ofSeq acc

            match format with
            | ExplainTraditional -> renderExplainRows rows
            | ExplainJson -> renderExplainJson rows
            | ExplainTree -> renderExplainTree rows
            | ExplainAnalyze when analyzedStatements = 1 -> renderExplainAnalyze rows analyzedMilliseconds analyzedRows
            | ExplainAnalyze -> Err(1235, "EXPLAIN ANALYZE currently supports one SELECT")
        | Error e -> e

    /// `INSERT`'s target table never goes through `explainJoinBlock` (an
    /// `INSERT` has no `FROM`), so it needs its own existence check.
    let checkTableExists (table: string) : Result<unit, QueryResult> =
        let db, tname = splitQualified dbName table
        resolveTableRef store registry dbName { Database = Some db; Table = tname; Alias = None; Partitions = [] } |> Result.map ignore

    let checkSelect (select: SelectStmt) : Result<unit, QueryResult> =
        let stopwatch = System.Diagnostics.Stopwatch.StartNew()
        let result, _, rows = runSelectStmt store registry dbName select None
        stopwatch.Stop()

        if format = ExplainAnalyze then
            analyzedMilliseconds <- analyzedMilliseconds + stopwatch.Elapsed.TotalMilliseconds
            analyzedRows <- analyzedRows + List.length rows
            analyzedStatements <- analyzedStatements + 1

        match result with
        | Err(code, message) -> Error(Err(code, message))
        | _ -> Ok()

    /// `UPDATE`/`DELETE` have no `SelectStmt` to hand `checkSelect`, so
    /// `exprs` (their `WHERE`, and an `UPDATE`'s `SET` right-hand sides) get
    /// the same "evaluate against a synthetic all-NULL probe row" check the
    /// real single-table `UPDATE`/`DELETE` paths already run before writing
    /// anything — an unknown column is 1054 here too, not a fake plan.
    let checkMutationWhere (fromRef: TableRef) (joins: Join list) (exprs: Expr list) : Result<unit, QueryResult> =
        let resolveJoinSource (j: Join) =
            match j.Table with
            | FromSubquery _ ->
                resolveFromSubquery store registry dbName j.Table None
                |> Result.map (fun (cols, _) -> fromItemQualifier j.Table, cols)
            | FromLateral _ -> Error(Err(1064, "a lateral derived table isn't supported as a multi-table UPDATE/DELETE JOIN source"))
            | FromJsonTable _ -> Error(Err(1064, "JSON_TABLE isn't supported as a multi-table UPDATE/DELETE JOIN source"))
            | FromTable tref -> resolveTableRef store registry dbName tref |> Result.map (fun (cols, _) -> fromItemQualifier j.Table, cols)

        resolveTableRef store registry dbName fromRef
        |> Result.bind (fun (fromCols, _) ->
            joins
            |> traverse resolveJoinSource
            |> Result.map (fun joinSources -> ((fromRef.Alias |> Option.defaultValue fromRef.Table), fromCols) :: joinSources))
        |> Result.bind (fun sources ->
            let allCols = sources |> List.collect snd
            let ctx = contextFactory store registry dbName (columnIndexOf allCols) (qualifierRanges sources) None (probeRow allCols)
            exprs |> traverse (fun e -> evalExpr ctx e |> Result.map ignore) |> Result.map ignore |> Result.mapError Err)

    match stmt with
    | Do _ -> Err(1064, "EXPLAIN does not support DO")
    | Select select ->
        let id = nextId ()

        let selectType =
            let isDerived = match select.From with Some(FromSubquery _) -> true | _ -> false
            if (selectSubqueryExprs select |> List.exists containsSubqueryExpr) || isDerived then "PRIMARY" else "SIMPLE"

        finish (checkSelect select |> Result.bind (fun () -> explainSelectBlock store registry dbName nextId acc id selectType select))
    | Union(first, rest, _, _, _) ->
        let id1 = nextId ()

        finish (
            checkSelect first
            |> Result.bind (fun () -> rest |> traverse (fun (_, s) -> checkSelect s) |> Result.map ignore)
            |> Result.bind (fun () -> explainSelectBlock store registry dbName nextId acc id1 "PRIMARY" first)
            |> Result.bind (fun () ->
                rest
                |> traverse (fun (_, s) ->
                    let sid = nextId ()
                    explainSelectBlock store registry dbName nextId acc sid "UNION" s |> Result.map (fun () -> sid)))
            |> Result.map (fun restIds ->
                if not restIds.IsEmpty then
                    let label = sprintf "<union%s>" (id1 :: restIds |> List.map string |> String.concat ",")
                    acc.Add { Id = None; SelectType = "UNION RESULT"; Table = Some label; Type = None; Key = None; Ref = None; Rows = None; Extra = [] })
        )
    | Update u when not u.Ctes.IsEmpty ->
        let expressions =
            (u.Assignments |> List.map _.Value)
            @ Option.toList u.Where
            @ (u.OrderBy |> List.map fst)
            @ Option.toList u.Limit
        let ctes = referencedMutationCtes u.Ctes u.Joins expressions
        withCteQueryResult store registry dbName ctes (fun () -> explainStatement format store registry dbName (Update { u with Ctes = [] }))
    | Update u ->
        let id = nextId ()
        let extra = [ if u.Where.IsSome then "Using where"
                      if not u.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = (u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)) @ (u.OrderBy |> List.map fst)

        finish (
            checkMutationWhere u.From u.Joins ((u.Where |> Option.toList) @ (u.Assignments |> List.map (fun a -> a.Value)))
            |> Result.bind (fun () -> explainJoinBlock store registry dbName nextId acc id "UPDATE" (Some(FromTable u.From)) u.Joins u.Where extra subqueryExprs None)
        )
    | Delete d when not d.Ctes.IsEmpty ->
        let expressions =
            Option.toList d.Where
            @ (d.OrderBy |> List.map fst)
            @ Option.toList d.Limit
        let ctes = referencedMutationCtes d.Ctes d.Joins expressions
        withCteQueryResult store registry dbName ctes (fun () -> explainStatement format store registry dbName (Delete { d with Ctes = [] }))
    | Delete d ->
        let id = nextId ()
        let extra = [ if d.Where.IsSome then "Using where"
                      if not d.OrderBy.IsEmpty then "Using filesort" ]
        let subqueryExprs = d.Where |> Option.toList

        finish (
            checkMutationWhere d.From d.Joins (d.Where |> Option.toList)
            |> Result.bind (fun () -> explainJoinBlock store registry dbName nextId acc id "DELETE" (Some(FromTable d.From)) d.Joins d.Where extra subqueryExprs None)
        )
    | Insert(table, _, rowsExprs, _, _)
    | Replace(table, _, rowsExprs) ->
        let id = nextId ()

        let selectType =
            match stmt with
            | Replace _ -> "REPLACE"
            | _ -> "INSERT"

        finish (
            checkTableExists table
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = selectType; Table = Some table; Type = None; Key = None; Ref = None; Rows = None; Extra = [] })
            |> Result.bind (fun () ->
                List.concat rowsExprs
                |> List.collect collectSubqueries
                |> traverse (fun sub ->
                    let sid = nextId ()
                    explainSelectBlock store registry dbName nextId acc sid (if Expression.hasQualifiedOuterReference sub then "DEPENDENT SUBQUERY" else "SUBQUERY") sub)
                |> Result.map ignore)
        )
    | ReplaceSet(table, assignments) ->
        let id = nextId ()

        finish (
            checkTableExists table
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = "REPLACE"; Table = Some table; Type = None; Key = None; Ref = None; Rows = None; Extra = [] })
            |> Result.bind (fun () ->
                assignments
                |> List.collect (snd >> collectSubqueries)
                |> traverse (fun sub ->
                    let sid = nextId ()
                    explainSelectBlock store registry dbName nextId acc sid (if Expression.hasQualifiedOuterReference sub then "DEPENDENT SUBQUERY" else "SUBQUERY") sub)
                |> Result.map ignore)
        )
    | InsertSelect(table, _, select, _, _)
    | ReplaceSelect(table, _, select) ->
        let id = nextId ()

        let selectType =
            match stmt with
            | ReplaceSelect _ -> "REPLACE"
            | _ -> "INSERT"

        finish (
            checkTableExists table
            |> Result.bind (fun () -> checkSelect select)
            |> Result.map (fun () -> acc.Add { Id = Some id; SelectType = selectType; Table = Some table; Type = None; Key = None; Ref = None; Rows = None; Extra = [] })
            |> Result.bind (fun () ->
                let sid = nextId ()
                explainSelectBlock store registry dbName nextId acc sid "SUBQUERY" select)
        )
    | Explain(nestedFormat, inner) -> explainStatement nestedFormat store registry dbName inner
    | _ -> Err(1064, "EXPLAIN is not supported for this statement")

/// A top-level `SELECT`'s resultset plus its per-column MySQL wire types —
/// `QueryHandler.executeStatement`'s type-preserving entry point into
/// `runSelectStmt`, which can't be `public` itself (see the doc there).
/// `outer` is always `None` for a top-level statement, so this needs no
/// `EvalContext` in its own signature. SQL_CALC_FOUND_ROWS executes the
/// unbounded query once and slices that result afterward, so expressions
/// with side effects are not evaluated a second time merely to count rows.
let runTopLevelSelect
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (select: SelectStmt)
    : QueryResult * ColumnMetadata list * uint64 option * Value[] list =
    resetStatementMemo ()
    let executable = { select with IntoVariables = [] }

    if select.CalculateFoundRows then
        let unbounded =
            { executable with
                CalculateFoundRows = false
                Limit = None
                Offset = None }

        let result, types, values = runSelectStmt store registry dbName unbounded None
        let limit = select.Limit |> Option.map rowCount
        let offset = select.Offset |> Option.map rowCount

        match result with
        | ResultSet(columns, rows) ->
            let limitedValues = applyLimitOffset limit offset values
            ResultSet(columns, applyLimitOffset limit offset rows), types, Some(uint64 values.Length), limitedValues
        | error -> error, types, None, []
    else
        let result, types, values = runSelectStmt store registry dbName executable None
        result, types, None, values

let runTopLevelUnion
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (first: SelectStmt)
    (rest: (SetOp * SelectStmt) list)
    (orderBy: OrderKey list)
    (limit: Expr option)
    (offset: Expr option)
    : QueryResult * ColumnMetadata list * uint64 option =
    resetStatementMemo ()

    if first.CalculateFoundRows then
        let result, types, values =
            runUnionStmtWithOuter store registry dbName { first with CalculateFoundRows = false } rest orderBy None None None

        let limit = limit |> Option.map rowCount
        let offset = offset |> Option.map rowCount

        match result with
        | ResultSet(columns, rows) ->
            ResultSet(columns, applyLimitOffset limit offset rows), types, Some(uint64 values.Length)
        | error -> error, types, None
    else
        let result, types, _ = runUnionStmtWithOuter store registry dbName first rest orderBy limit offset None
        result, types, None

/// Describes a stored view without evaluating its query or its expressions.
let viewColumns (store: Store) (registry: Registry) (schema: string) (name: string) : ColumnDef list option =
    describeStoredViewColumns store registry schema name

/// Describes the result columns a statement fixes at prepare time without
/// evaluating its expressions or reading its rows.
let statementColumns (store: Store) (registry: Registry) (schema: string) (statement: Statement) : ColumnDef list option =
    match statement with
    | Select select when select.IntoVariables.IsEmpty ->
        describeQueryColumns store registry schema (QueryBody(PlainSelect select))
    | Union(first, rest, orderBy, limit, offset) ->
        describeQueryColumns store registry schema (QueryBody(UnionSelect(first, rest, orderBy, limit, offset)))
    | _ -> None

/// PREPARE must expose the same physical source fields as later execution
/// without evaluating the statement to recover them.
let statementColumnOrigins (store: Store) (schema: string) (statement: Statement) : ColumnOrigin option list option =
    match statement with
    | Select select when select.IntoVariables.IsEmpty ->
        let sources =
            (select.From |> Option.toList) @ (select.Joins |> List.map _.Table)
            |> List.map (fun source ->
                fromItemQualifier source,
                selectSourceColumns store schema source |> List.choose id)

        Some(outputColumnOrigins store schema (qualifierRanges sources) select)
    | _ -> None

/// Folds one `insertRows`/`insertRowsIgnore`/`upsertRows` result's
/// `(okPacketId, generatedId)` pair into `execute`'s own `ids` accumulator —
/// see `execute`'s doc for what each half means. `0L` from storage means
/// "this statement assigned no id at all", so the OK-packet half falls back
/// to its previous value the same way it always has; the `LAST_INSERT_ID()`
/// half only ever moves forward on an actual generated id.
let private nextIds ((okId, generatedId): int64 * int64) ((newOkId: int64), (newGenerated: int64 option)) : int64 * int64 =
    (if newOkId <> 0L then newOkId else okId), (newGenerated |> Option.defaultValue generatedId)

let private firstDirectOnlyCall (registry: Registry) =
    Expression.tryPick (function
        | FuncCall(name, _) ->
            registry.Extensions
            |> Map.tryFind (name.ToUpperInvariant())
            |> Option.filter _.DirectOnly
            |> Option.map (fun _ -> name)
        | _ -> None)

/// MySQL's own ER_GENERATED_COLUMN_FUNCTION_IS_NOT_ALLOWED (3102) shape,
/// for the first offending column in a CREATE/ALTER's column definitions.
let private rejectDirectOnlyGenerated (registry: Registry) (columns: Ast.ColumnDef list) : QueryResult option =
    columns
    |> List.tryPick (fun c -> c.Generated |> Option.bind (fun (e, _) -> firstDirectOnlyCall registry e |> Option.map (fun _ -> c.Name)))
    |> Option.map (fun col -> Err(3102, sprintf "Expression of generated column '%s' contains a disallowed function." col))

let private containsQuantifiedComparison =
    Expression.exists (function QuantifiedComparison _ -> true | _ -> false)

let private rejectQuantifiedComparisonsInGenerated (columns: Ast.ColumnDef list) : QueryResult option =
    columns
    |> List.tryPick (fun column -> column.Generated |> Option.bind (fun (expression, _) -> if containsQuantifiedComparison expression then Some column.Name else None))
    |> Option.map (fun column -> Err(3102, sprintf "Expression of generated column '%s' contains a disallowed function." column))

let rec private containsSessionVariable (expression: Expr) : bool =
    Expression.fold
        (fun found node ->
            if found then
                Expression.Prune true
            else
                match node with
                | UserVariable _
                | SystemVariable _
                | AssignUserVariable _ -> Expression.Prune true
                | _ ->
                    if Expression.subqueries node |> List.exists selectContainsSessionVariable then
                        Expression.Prune true
                    else
                        Expression.Descend false)
        false
        expression

and private selectContainsSessionVariable (select: SelectStmt) : bool =
    let fromContainsSessionVariable =
        function
        | FromTable _ -> false
        | FromSubquery(body, _)
        | FromLateral(body, _) -> selectOrUnionContainsSessionVariable body
        | FromJsonTable(source, _, _, _) -> containsSessionVariable source

    (select.Projections |> List.exists (fst >> containsSessionVariable))
    || (select.From |> Option.exists fromContainsSessionVariable)
    || (select.Joins |> List.exists (fun join -> fromContainsSessionVariable join.Table || containsSessionVariable join.On))
    || (select.Where |> Option.exists containsSessionVariable)
    || (select.GroupBy |> List.exists containsSessionVariable)
    || (select.Windows
        |> List.exists (fun (_, spec) ->
            Expression.overExpressions (OverSpec spec) |> List.exists containsSessionVariable))
    || (select.Ctes |> List.exists (fun cte -> selectOrUnionContainsSessionVariable cte.Body))
    || (select.Having |> Option.exists containsSessionVariable)
    || (select.OrderBy |> List.exists (fst >> containsSessionVariable))
    || (select.Limit |> Option.exists containsSessionVariable)
    || (select.Offset |> Option.exists containsSessionVariable)

and private selectOrUnionContainsSessionVariable (body: SelectOrUnion) : bool =
    match body with
    | PlainSelect select -> selectContainsSessionVariable select
    | UnionSelect(first, rest, orderBy, limit, offset) ->
        selectContainsSessionVariable first
        || (rest |> List.exists (snd >> selectContainsSessionVariable))
        || (orderBy |> List.exists (fst >> containsSessionVariable))
        || (limit |> Option.exists containsSessionVariable)
        || (offset |> Option.exists containsSessionVariable)

let private rejectSessionVariablesInGenerated (columns: Ast.ColumnDef list) : QueryResult option =
    columns
    |> List.tryPick (fun column -> column.Generated |> Option.bind (fun (expression, _) -> if containsSessionVariable expression then Some column.Name else None))
    |> Option.map (fun column -> Err(3772, sprintf "Default value expression of column '%s' cannot refer user or system variables." column))

let private viewContainsSessionVariable =
    function
    | Select select -> selectContainsSessionVariable select
    | Union(first, rest, orderBy, limit, offset) ->
        selectOrUnionContainsSessionVariable (UnionSelect(first, rest, orderBy, limit, offset))
    | _ -> false

let private checkColumnReferences (expression: Expr) : (string option * string) list =
    Expression.fold
        (fun references node ->
            match node with
            | Col column -> Expression.Descend((None, column) :: references)
            | QualifiedCol(table, column) -> Expression.Descend((Some table, column) :: references)
            | MatchAgainst(columns, _, _) ->
                columns
                |> List.fold (fun found column -> (column.Qualifier, column.Name) :: found) references
                |> Expression.Descend
            | WindowOver _ -> Expression.Prune references
            | _ -> Expression.Descend references)
        []
        expression
    |> List.rev

let private nondeterministicCheckFunctions =
    set
        [ "BENCHMARK"; "CONNECTION_ID"; "CURDATE"; "CURRENT_DATE"; "CURRENT_TIME"; "CURRENT_TIMESTAMP"
          "CURRENT_USER"; "CURTIME"; "DATABASE"; "FOUND_ROWS"; "LAST_INSERT_ID"; "LOCALTIME"
          "LOCALTIMESTAMP"; "NOW"; "RAND"; "ROW_COUNT"; "SYSDATE"; "UNIX_TIMESTAMP"
          "USER"; "UUID"; "UUID_SHORT"; "VERSION" ]

let private firstDisallowedCheckFunction (registry: Registry) =
    Expression.tryPick (fun expression ->
        match expression with
        | FuncCall(name, _) ->
            let key = name.ToUpperInvariant()

            match Map.tryFind key registry.Extensions with
            | _ when nondeterministicCheckFunctions.Contains key || isAggregateCall registry expression -> Some name
            | Some extension when extension.DirectOnly || not extension.Deterministic -> Some name
            | _ -> None
        | _ -> None)

let private firstDisallowedCheckShape =
    Expression.tryPick (function
        | Placeholder _ -> Some "parameter"
        | WindowOver _ -> Some "window function"
        | Star _ -> Some "wildcard"
        | MatchAgainst _ -> Some "full-text expression"
        | UserVariable _
        | SystemVariable _
        | AssignUserVariable _ -> Some "session variable"
        | Distinct _
        | OrderBy _ -> Some "aggregate modifier"
        | Exists _
        | Subquery _
        | InSubquery _
        | QuantifiedComparison _ -> Some "subquery"
        | _ -> None)

let private validateFunctionalDefaults (registry: Registry) (columns: ColumnDef list) : Result<unit, QueryResult> =
    let disallowedFunctions = set [ "BENCHMARK"; "SLEEP" ]

    columns
    |> List.indexed
    |> List.tryPick (fun (columnIndex, column) ->
        column.Default
        |> Option.bind (function
            | DConst _
            | DCurrentTimestamp -> None
            | DExpression expression ->
                if containsSessionVariable expression then
                    Some(Err(3772, sprintf "Default value expression of column '%s' cannot refer user or system variables." column.Name))
                elif containsSubqueryExpr expression || Expression.exists (function WindowOver _ | Placeholder _ | Star _ | MatchAgainst _ -> true | _ -> false) expression then
                    Some(Err(3769, sprintf "Default value expression of column '%s' contains a disallowed function." column.Name))
                elif containsAggregate registry expression then
                    Some(Err(1111, "Invalid use of group function"))
                else
                    let disallowed =
                        Expression.collect
                            (function
                            | FuncCall(name, _) when disallowedFunctions.Contains(name.ToUpperInvariant()) -> Some name
                            | FuncCall(name, _) ->
                                registry.Extensions
                                |> Map.tryFind (name.ToUpperInvariant())
                                |> Option.filter _.DirectOnly
                                |> Option.map (fun _ -> name)
                            | _ -> None)
                            expression
                        |> List.tryHead

                    match disallowed with
                    | Some name -> Some(Err(3770, sprintf "Default value expression of column '%s' contains a disallowed function: %s." column.Name (name.ToLowerInvariant())))
                    | None ->
                        checkColumnReferences expression
                        |> List.tryPick (fun (qualifier, name) ->
                            match qualifier, resolveColumn columns name with
                            | Some qualifier, _ -> Some(Err(1054, sprintf "Unknown column '%s.%s' in 'DEFAULT'" qualifier name))
                            | None, Error _ -> Some(Err(1054, sprintf "Unknown column '%s' in 'DEFAULT'" name))
                            | None, Ok referencedIndex when columns.[referencedIndex].AutoIncrement ->
                                Some(Err(3768, sprintf "Default value expression of column '%s' cannot refer to an auto-increment column." column.Name))
                            | None, Ok referencedIndex
                                when referencedIndex >= columnIndex
                                     && (columns.[referencedIndex].Generated.IsSome
                                         || (match columns.[referencedIndex].Default with Some(DExpression _) -> true | _ -> false)) ->
                                Some(Err(3767, sprintf "Default value expression of column '%s' cannot refer to a column defined after it if that column is a generated column or has an expression as default value." column.Name))
                            | _ -> None)))
    |> function
        | None -> Ok()
        | Some error -> Error error

let private validateFunctionalDefaultsForStorage (registry: Registry) (columns: ColumnDef list) : Result<unit, StorageError> =
    match validateFunctionalDefaults registry columns with
    | Ok() -> Ok()
    | Error(Err(code, message)) -> Error(ExpressionError(code, message))
    | Error _ -> Error(ExpressionError(1105, "Invalid functional default"))

let private validateIndexExpressions
    (registry: Registry)
    (columns: ColumnDef list)
    (indexes: IndexDef list)
    : Result<unit, StorageError> =
    let invalid (index: IndexDef) detail =
        Error(
            ExpressionError(
                3758,
                sprintf "Expression of functional index '%s' contains a disallowed %s." index.Name detail
            )
        )

    let validateExpression (index: IndexDef) expression =
        match firstDisallowedCheckShape expression with
        | Some shape -> invalid index shape
        | None when Expression.exists (function Row _ -> true | _ -> false) expression -> invalid index "row expression"
        | None ->
            match firstDisallowedCheckFunction registry expression with
            | Some functionName -> invalid index (sprintf "function: %s" functionName)
            | None ->
                checkColumnReferences expression
                |> List.tryPick (fun (qualifier, name) ->
                    match qualifier, resolveColumn columns name with
                    | Some _, _ -> Some(ExpressionError(3757, "Cannot create a functional index on an expression that refers to another table."))
                    | None, Error _ -> Some(UnknownColumn name)
                    | None, Ok _ -> None)
                |> function
                    | Some error -> Error error
                    | None -> Ok()

    indexes
    |> traverse (fun index ->
        index.KeyColumns
        |> traverse (fun column ->
            match column.Transform with
            | Some(Expression expression) -> validateExpression index expression
            | _ -> Ok())
        |> Result.map ignore)
    |> Result.map ignore

let private validateCheckDefinition
    (registry: Registry)
    (columns: ColumnDef list)
    (definition: CheckConstraintDef)
    : Result<unit, StorageError> =
    let constraintName = definition.Name |> Option.defaultValue ""
    let invalid message = Error(ExpressionError(3813, message))

    match firstDisallowedCheckShape definition.Expression with
    | Some shape -> invalid (sprintf "Check constraint '%s' contains a disallowed %s." constraintName shape)
    | None ->
        match firstDisallowedCheckFunction registry definition.Expression with
        | Some functionName ->
            Error(
                ExpressionError(
                    3814,
                    sprintf "An expression of a check constraint '%s' contains disallowed function: %s." constraintName functionName
                )
            )
        | None ->
            let references = checkColumnReferences definition.Expression

            match references |> List.tryFind (fun (qualifier, _) -> qualifier.IsSome) with
            | Some _ -> invalid (sprintf "Check constraint '%s' cannot refer to another table." constraintName)
            | None ->
                match references |> List.tryFind (fun (_, name) -> resolveColumn columns name |> Result.isError) with
                | Some(_, column) ->
                    Error(UnknownColumn column)
                | None ->
                    let resolved =
                        references
                        |> List.choose (fun (_, name) -> resolveColumn columns name |> Result.toOption)

                    match definition.Column with
                    | Some owner when references |> List.exists (fun (_, name) -> not (System.String.Equals(owner, name, System.StringComparison.OrdinalIgnoreCase))) ->
                        invalid (sprintf "Column check constraint '%s' references other column." constraintName)
                    | _ ->
                        match resolved |> List.tryFind (fun index -> columns.[index].AutoIncrement) with
                        | Some _ -> Error(ExpressionError(3818, sprintf "Check constraint '%s' cannot refer to an auto-increment column." constraintName))
                        | None -> Ok()

let private allStoredCheckRows (store: Store) : Value[] list =
    match scan store "mysql" "check_constraints" with
    | Ok(_, rows) -> List.ofSeq rows
    | Error _ -> []

let private checkRowSatisfies predicate row =
    row |> SystemCatalog.Check.tryRead |> Option.exists predicate

let private storeCheckDefinitions
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (tableName: string)
    (columns: ColumnDef list)
    (definitions: CheckConstraintDef list)
    : Result<unit, StorageError> =
    let equal left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)
    let existing =
        allStoredCheckRows store
        |> List.choose SystemCatalog.Check.tryRead
        |> List.filter (fun check -> equal check.Schema dbName)

    let usedNames = existing |> List.map _.Name |> List.map _.ToLowerInvariant() |> Set.ofList
    let existingOrdinals = storedChecks store dbName tableName |> List.map _.Ordinal
    let mutable nextOrdinal = (if existingOrdinals.IsEmpty then 0 else List.max existingOrdinals) + 1
    let mutable generatedIndex = 1
    let mutable names = usedNames

    let allocateName (definition: CheckConstraintDef) =
        match definition.Name with
        | Some name when names.Contains(name.ToLowerInvariant()) ->
            Error(ExpressionError(3822, sprintf "Duplicate check constraint name '%s'." name))
        | Some name -> Ok(name, false)
        | None ->
            let mutable candidate = sprintf "%s_chk_%d" tableName generatedIndex

            while names.Contains(candidate.ToLowerInvariant()) do
                generatedIndex <- generatedIndex + 1
                candidate <- sprintf "%s_chk_%d" tableName generatedIndex

            generatedIndex <- generatedIndex + 1
            Ok(candidate, true)

    definitions
    |> traverse (fun definition ->
        allocateName definition
        |> Result.bind (fun (name, generated) ->
            let named = { definition with Name = Some name }

            validateCheckDefinition registry columns named
            |> Result.map (fun () ->
                names <- names.Add(name.ToLowerInvariant())
                let ordinal = nextOrdinal
                nextOrdinal <- nextOrdinal + 1
                named, generated, ordinal)))
    |> Result.bind (fun prepared ->
        prepared
        |> traverse (fun (definition, generated, ordinal) ->
            let name = definition.Name.Value

            insertRows
                store
                "mysql"
                "check_constraints"
                None
                [ [ VString name
                    VString dbName
                    VString tableName
                    VString(InformationSchema.exprToSql definition.Expression)
                    VString(if definition.Enforced then "YES" else "NO")
                    (definition.Column |> Option.map VString |> Option.defaultValue VNull)
                    VString(if generated then "YES" else "NO")
                    VInt(int64 ordinal) ] ]
            |> Result.map ignore)
        |> Result.map ignore)

let private removeStoredChecks (store: Store) (dbName: string) (tableName: string) : Result<int, StorageError> =
    deleteRows store "mysql" "check_constraints" (fun row ->
        row
        |> SystemCatalog.Check.tryRead
        |> Option.exists (fun check ->
            System.String.Equals(check.Schema, dbName, System.StringComparison.OrdinalIgnoreCase)
            && System.String.Equals(check.Table, tableName, System.StringComparison.OrdinalIgnoreCase))
        |> Ok)

let private validateCheckForeignKeys
    (store: Store)
    (dbName: string)
    (tableName: string)
    (foreignKeys: ForeignKeyDef list)
    : Result<unit, StorageError> =
    let mutatesChildColumn (foreignKey: ForeignKeyDef) =
        let actionMutates (action: string option) =
            match action with
            | Some action ->
                action.Equals("CASCADE", System.StringComparison.OrdinalIgnoreCase)
                || action.Equals("SET NULL", System.StringComparison.OrdinalIgnoreCase)
            | None -> false

        actionMutates foreignKey.OnUpdate
        || (foreignKey.OnDelete |> Option.exists (fun action -> action.Equals("SET NULL", System.StringComparison.OrdinalIgnoreCase)))

    let checks = storedChecks store dbName tableName

    foreignKeys
    |> List.filter mutatesChildColumn
    |> List.tryPick (fun foreignKey ->
        checks
        |> List.tryPick (fun check ->
            match Parser.parseExpression check.Clause with
            | Result.Error _ -> None
            | Result.Ok expression ->
                checkColumnReferences expression
                |> List.tryPick (fun (_, column) ->
                    foreignKey.Columns
                    |> List.tryFind (fun fkColumn -> fkColumn.Equals(column, System.StringComparison.OrdinalIgnoreCase))
                    |> Option.map (fun matched -> check, foreignKey, matched))))
    |> function
        | None -> Ok()
        | Some(check, foreignKey, column) ->
            Error(
                ExpressionError(
                    3823,
                    sprintf
                        "Column '%s' cannot be used in a check constraint '%s': needed in a foreign key constraint '%s' referential action."
                        column
                        check.Name
                        foreignKey.Name
                )
            )

// ---------------------------------------------------------------------------
// Triggers
// ---------------------------------------------------------------------------

/// Trigger subjects on the current execution thread, innermost first.
let private triggerChain = new System.Threading.ThreadLocal<(string * string) list>(fun () -> [])

let private triggerInvocationTables =
    new System.Threading.ThreadLocal<Set<string * string>>(fun () -> Set.empty)

let private withTriggerInvocationTables tables body =
    let previous = triggerInvocationTables.Value
    triggerInvocationTables.Value <- Set.union previous tables

    try
        body ()
    finally
        triggerInvocationTables.Value <- previous

let private err1442 (table: string) : QueryResult =
    Err(
        1442,
        sprintf
            "Can't update table '%s' in stored function/trigger because it is already used by statement which invoked this stored function/trigger."
            table
    )

let private isTriggerSlot
    (db: string)
    (table: string)
    (timing: string)
    (event: string)
    (trigger: SystemCatalog.Trigger.Entry)
    =
    let equals left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

    equals trigger.Schema db
    && equals trigger.Table (normalizeTableName table)
    && equals trigger.Timing timing
    && equals trigger.Event event

let private sameTriggerSlot db table timing event row =
    row
    |> SystemCatalog.Trigger.tryRead
    |> Option.exists (isTriggerSlot db table timing event)

let private triggersFor
    (store: Store)
    (db: string)
    (table: string)
    (timing: string)
    (event: string)
    : StoredTrigger list =
    match scan store "mysql" "triggers" with
    | Error _ -> []
    | Ok(_, rows) ->
        rows
        |> Seq.choose SystemCatalog.Trigger.tryRead
        |> Seq.filter (isTriggerSlot db table timing event)
        |> Seq.map (fun trigger ->
            { Name = trigger.Name
              Body = trigger.Body
              Definer = trigger.Definer
              Order = trigger.Order
              SqlMode = trigger.SqlMode
              CharacterSetClient = trigger.CharacterSetClient
              CollationConnection = trigger.CollationConnection })
        |> Seq.sortBy (fun trigger -> trigger.Order)
        |> List.ofSeq

let private afterInsertTriggers (store: Store) (db: string) (table: string) =
    triggersFor store db table "AFTER" "INSERT"

let private beforeInsertTriggers (store: Store) (db: string) (table: string) =
    triggersFor store db table "BEFORE" "INSERT"

type private TriggerStatement = StoredProgram.Statement

type private TriggerRoutineScope =
    { Conditions: Map<string, StoredProgram.ConditionValue>
      Statements: StoredProgram.Statement list
      ActiveError: SqlState.Error option
      StackedDiagnostics: StoredProgram.DiagnosticsSnapshot option }

let private triggerDmlStatements = StoredProgram.sqlStatements
let private triggerConditions = StoredProgram.expressions

/// The one table a trigger body statement writes — what 1442's "already
/// used by the statement which invoked this trigger" check points at.
let private writtenTableOf (dbName: string) (stmt: Statement) : (string * string) option =
    match stmt with
    | Insert(t, _, _, _, _)
    | InsertSelect(t, _, _, _, _)
    | Replace(t, _, _)
    | ReplaceSelect(t, _, _)
    | ReplaceSet(t, _) ->
        let db, t = splitQualified dbName t
        Some(db, normalizeTableName t)
    | Update u -> Some(u.From.Database |> Option.defaultValue dbName, normalizeTableName u.From.Table)
    | Delete d -> Some(d.From.Database |> Option.defaultValue dbName, normalizeTableName d.From.Table)
    | _ -> None

/// Every database an INSERT into `dbName.tableName` can reach through its
/// AFTER INSERT triggers, following the chain transitively.
///
/// `QueryHandler` gates all of them, not just the insert's own target: a
/// trigger body writing into another database while nobody holds that
/// database's gate lets a concurrent writer there interleave with it, and
/// one of the two rows is lost when the transaction merges
/// (`Storage.mergeDatabaseSlot`).
let triggerWriteDatabases (store: Store) (dbName: string) (tableName: string) : string list =
    let bodyTargets (defaultDb: string) (triggerStatement: StoredProgram.Statement) =
        let tableRefTarget (table: TableRef) =
            table.Database |> Option.defaultValue defaultDb, normalizeTableName table.Table

        let joinTargets (joins: Join list) =
            joins
            |> List.choose (fun (join: Join) ->
                match join.Table with
                | FromTable table -> Some(tableRefTarget table)
                | _ -> None)

        StoredProgram.sqlStatements triggerStatement
        |> List.collect (function
            | Insert(table, _, _, _, _)
            | InsertSelect(table, _, _, _, _)
            | Replace(table, _, _)
            | ReplaceSelect(table, _, _)
            | ReplaceSet(table, _) -> [ splitQualified defaultDb table |> fun (db, name) -> db, normalizeTableName name ]
            | Update update -> tableRefTarget update.From :: joinTargets update.Joins
            | Delete delete -> tableRefTarget delete.From :: joinTargets delete.Joins
            | _ -> [])

    let rec visit (visited: Set<string * string>) (db: string) (table: string) =
        let key = db.ToLowerInvariant(), normalizeTableName table

        if Set.contains key visited then
            visited, []
        else
            let visited = Set.add key visited

            afterInsertTriggers store db table
            |> List.fold
                (fun (seen, databases) trigger ->
                    match StoredProgram.parseTrigger (SqlMode.parserOptionsFor trigger.SqlMode) trigger.Body with
                    | Ok statements ->
                        statements
                        |> List.collect (bodyTargets db)
                        |> List.fold
                            (fun (nestedSeen, nestedDatabases) (targetDb, targetTable) ->
                                let nestedSeen, deeper = visit nestedSeen targetDb targetTable
                                nestedSeen, targetDb :: deeper @ nestedDatabases)
                            (seen, databases)
                    | _ -> seen, databases)
                (visited, [])

    visit Set.empty dbName tableName |> snd |> List.distinct

/// The exprs a trigger body evaluates at fire time, for the CREATE-time
/// half of DIRECTONLY enforcement (`firstDirectOnlyCall` over each). An
/// INSERT...SELECT body's SELECT isn't traversed — the fire-time
/// `shadowDirectOnly` backstop still catches those, the same backstop that
/// covers functions registered only after the trigger was created.
let private triggerBodyExprs (triggerStatement: StoredProgram.Statement) : Expr list =
    let statementExprs =
        function
        | SetTriggerNew(_, expression) -> [ expression ]
        | Insert(_, _, rows, onDup, _) -> List.concat rows @ (onDup |> List.map snd)
        | InsertSelect(_, _, _, onDup, _) -> onDup |> List.map snd
        | Replace(_, _, rows) -> List.concat rows
        | ReplaceSelect _ -> []
        | ReplaceSet(_, assignments) -> assignments |> List.map snd
        | Update u -> (u.Assignments |> List.map (fun a -> a.Value)) @ Option.toList u.Where
        | Delete d -> Option.toList d.Where
        | _ -> []

    StoredProgram.expressions triggerStatement
    @ (StoredProgram.sqlStatements triggerStatement |> List.collect statementExprs)

let private triggerRowImageError (event: TriggerEvent) (columns: ColumnDef list) (triggerStatement: StoredProgram.Statement) =
    let rec references =
        function
        | QualifiedCol(qualifier, column) when qualifier.Equals("OLD", System.StringComparison.OrdinalIgnoreCase) || qualifier.Equals("NEW", System.StringComparison.OrdinalIgnoreCase) ->
            [ qualifier.ToUpperInvariant(), column ]
        | Exists select
        | Subquery select -> selectReferences select
        | InSubquery(value, select) -> references value @ selectReferences select
        | QuantifiedComparison(value, _, _, select) -> references value @ selectReferences select
        | WindowOver(functions, over) ->
            Expression.windowExpressions functions @ Expression.overExpressions over
            |> List.collect references
        | FuncCall(_, arguments) -> arguments |> List.collect references
        | BinOp(_, left, right)
        | Like(left, right, _, _)
        | Regexp(left, right) -> references left @ references right
        | Not expression
        | IsNull expression
        | IsNotNull expression
        | IsTrue expression
        | IsFalse expression
        | Distinct expression
        | OrderBy(expression, _)
        | Cast(expression, _)
        | Collate(expression, _)
        | AssignUserVariable(_, expression) -> references expression
        | In(expression, candidates) -> references expression @ (candidates |> List.collect references)
        | Between(expression, lower, upper) -> references expression @ references lower @ references upper
        | Case(subject, branches, otherwise) ->
            (subject |> Option.map references |> Option.defaultValue [])
            @ (branches |> List.collect (fun (condition, result) -> references condition @ references result))
            @ (otherwise |> Option.map references |> Option.defaultValue [])
        | MatchAgainst(_, query, _) -> references query
        | _ -> []

    and fromReferences =
        function
        | FromTable _ -> []
        | FromSubquery(body, _)
        | FromLateral(body, _) -> selectOrUnionReferences body
        | FromJsonTable(source, _, _, _) -> references source

    and selectReferences (select: SelectStmt) =
        (select.Projections |> List.collect (fst >> references))
        @ (select.From |> Option.map fromReferences |> Option.defaultValue [])
        @ (select.Joins |> List.collect (fun join -> fromReferences join.Table @ references join.On))
        @ (select.Where |> Option.map references |> Option.defaultValue [])
        @ (select.GroupBy |> List.collect references)
        @ (select.Windows
           |> List.collect (fun (_, spec) ->
               Expression.overExpressions (OverSpec spec) |> List.collect references))
        @ (select.Ctes |> List.collect (fun cte -> selectOrUnionReferences cte.Body))
        @ (select.Having |> Option.map references |> Option.defaultValue [])
        @ (select.OrderBy |> List.collect (fst >> references))
        @ (select.Limit |> Option.map references |> Option.defaultValue [])
        @ (select.Offset |> Option.map references |> Option.defaultValue [])

    and selectOrUnionReferences =
        function
        | PlainSelect select -> selectReferences select
        | UnionSelect(first, rest, orderBy, limit, offset) ->
            selectReferences first
            @ (rest |> List.collect (snd >> selectReferences))
            @ (orderBy |> List.collect (fst >> references))
            @ (limit |> Option.map references |> Option.defaultValue [])
            @ (offset |> Option.map references |> Option.defaultValue [])

    let conditionReferences =
        triggerConditions triggerStatement |> List.collect references

    let statementReferences =
        StoredProgram.sqlStatements triggerStatement
        |> List.collect (function
            | SetTriggerNew(_, expression) -> references expression
            | Insert(_, _, rows, assignments, _) -> (rows |> List.collect (List.collect references)) @ (assignments |> List.collect (snd >> references))
            | InsertSelect(_, _, select, assignments, _) -> selectReferences select @ (assignments |> List.collect (snd >> references))
            | Replace(_, _, rows) -> rows |> List.collect (List.collect references)
            | ReplaceSelect(_, _, select) -> selectReferences select
            | ReplaceSet(_, assignments) -> assignments |> List.collect (snd >> references)
            | Update update ->
                (update.Ctes |> List.collect (fun cte -> selectOrUnionReferences cte.Body))
                @ (update.Assignments |> List.collect (fun assignment -> references assignment.Value))
                @ (update.Where |> Option.map references |> Option.defaultValue [])
            | Delete delete ->
                (delete.Ctes |> List.collect (fun cte -> selectOrUnionReferences cte.Body))
                @ (delete.Where |> Option.map references |> Option.defaultValue [])
            | _ -> [])

    let statementReferences = conditionReferences @ statementReferences

    statementReferences
    |> List.tryPick (fun (image, column) ->
        match image, event with
        | "OLD", TriggerInsert -> Some(Err(1363, "There is no OLD row in INSERT trigger"))
        | "NEW", TriggerDelete -> Some(Err(1363, "There is no NEW row in DELETE trigger"))
        | _ ->
            match resolveColumn columns column with
            | Ok index when columns.[index].Generated.IsSome -> Some(Err(3105, sprintf "Trigger cannot reference generated column '%s'" column))
            | Ok _ -> None
            | Error _ -> Some(Err(1054, sprintf "Unknown column '%s.%s' in trigger" image column)))

let private validateTriggerStatement
    (registry: Registry)
    (timing: TriggerTiming)
    (event: TriggerEvent)
    (columns: ColumnDef list)
    (triggerStatement: TriggerStatement)
    : Result<unit, QueryResult> =
    let validateStatement =
        function
        | Insert _
        | InsertSelect _
        | Replace _
        | ReplaceSelect _
        | ReplaceSet _
        | Update _
        | Delete _ -> Ok()
        | SetTriggerNew(_, _) when timing <> Before || event = TriggerDelete ->
            Error(Err(1362, "Updating of NEW row is not allowed in after trigger"))
        | SetTriggerNew(column, _) ->
            match resolveColumn columns column with
            | Error error -> Error(storageErr error)
            | Ok index when columns.[index].Generated.IsSome ->
                Error(Err(1362, sprintf "Updating of NEW row is not allowed for generated column '%s'" column))
            | Ok _ -> Ok()
        | _ -> Error(Err(1064, "Trigger body accepts INSERT, UPDATE, DELETE, REPLACE, CALL, or SET NEW statements"))

    match StoredProgram.executableSqlStatements triggerStatement |> traverse validateStatement with
    | Error error -> Error error
    | Ok _ ->
        match triggerRowImageError event columns triggerStatement with
        | Some error -> Error error
        | None ->
            match triggerBodyExprs triggerStatement |> List.tryPick (firstDirectOnlyCall registry) with
            | Some fn -> Error(Err(3102, sprintf "Expression of trigger contains a disallowed function: %s" fn))
            | None -> Ok()

let private storeTriggerDefinition
    (store: Store)
    (account: Auth.Account)
    (db: string)
    (table: string)
    (name: string)
    (timing: string)
    (event: string)
    (order: TriggerOrder option)
    (body: string)
    : Result<unit, QueryResult> =
    match scan store "mysql" "triggers" with
    | Error error -> Error(storageErr error)
    | Ok(_, existing) ->
        let equals left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

        let duplicateName =
            existing
            |> Seq.choose SystemCatalog.Trigger.tryRead
            |> Seq.exists (fun trigger -> equals trigger.Schema db && equals trigger.Name name)

        let sameSlot = isTriggerSlot db table timing event
        let sameSlotRow row = row |> SystemCatalog.Trigger.tryRead |> Option.exists sameSlot

        let peers =
            existing
            |> Seq.choose (fun row -> SystemCatalog.Trigger.tryRead row |> Option.map (fun trigger -> row, trigger))
            |> Seq.filter (snd >> sameSlot)
            |> List.ofSeq

        let insertionOrder =
            match order with
            | None ->
                let lastOrder = peers |> List.map (fun (_, trigger) -> trigger.Order) |> List.fold max 0L
                Ok(lastOrder + 1L)
            | Some requested ->
                let reference =
                    match requested with
                    | Follows trigger
                    | Precedes trigger -> trigger

                match
                    peers
                    |> List.tryFind (fun (_, trigger) -> equals trigger.Name reference)
                with
                | None ->
                    Error(
                        Err(
                            3011,
                            sprintf "Referenced trigger '%s' for the given action time and event type does not exist." reference
                        )
                    )
                | Some(_, trigger) ->
                    let referenceOrder = trigger.Order

                    match requested with
                    | Follows _ -> Ok(referenceOrder + 1L)
                    | Precedes _ -> Ok referenceOrder

        if duplicateName then
            Error(Err(1359, "Trigger already exists"))
        else
            insertionOrder
            |> Result.bind (fun insertionOrder ->
                let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

                updateRows
                    snapshot
                    "mysql"
                    "triggers"
                    None
                    (fun row -> Ok(sameSlotRow row && SystemCatalog.Trigger.actionOrder row >= insertionOrder))
                    (fun row ->
                        row
                        |> SystemCatalog.Trigger.withActionOrder (SystemCatalog.Trigger.actionOrder row + 1L)
                        |> Ok)
                |> Result.bind (fun _ ->
                    insertRows
                        snapshot
                        "mysql"
                        "triggers"
                        (Some
                            [ "trigger_name"; "trigger_schema"; "event_table"; "action_timing"; "event_manipulation"
                              "action_statement"; "created"; "definer"; "action_order"; "sql_mode"
                              "character_set_client"; "collation_connection"; "database_collation" ])
                        [ [ VString name
                            VString db
                            VString(normalizeTableName table)
                            VString timing
                            VString event
                            VString body
                            VDateTime System.DateTime.Now
                            VString(Auth.formatAccount account)
                            VInt insertionOrder
                            VString store.ExecutionSettings.SqlModeText
                            VString store.ExecutionSettings.ConnectionCharset
                            VString store.ExecutionSettings.ConnectionCollation.Name
                            VString Collation.defaultCollation.Name ] ])
                |> Result.mapError storageErr
                |> Result.map (fun _ ->
                    Storage.commitCatalogInto store baseCatalog snapshot))

let private reportNumericDisplayWarnings columns =
    for column in columns do
        match column.NumericDisplay with
        | Some display ->
            if display.ZeroFill then
                Diagnostics.warning
                    1681
                    "The ZEROFILL attribute is deprecated and will be removed in a future release. Use the LPAD function to zero-pad numbers, or store the formatted numbers in a CHAR column."

            match column.Type, display.Width, display.Decimals with
            | (TTinyInt _ | TBool | TSmallInt _ | TMediumInt _ | TInt _ | TBigInt _), Some _, _ ->
                Diagnostics.warning 1681 "Integer display width is deprecated and will be removed in a future release."
            | (TFloat _ | TDouble _), Some _, Some _ ->
                Diagnostics.warning
                    1681
                    "Specifying number of digits for floating point data types is deprecated and will be removed in a future release."
            | _ -> ()
        | None -> ()

/// Executes a statement under `currentAccount`; `ids` carries the OK-packet
/// and generated AUTO_INCREMENT identities between statements.
let rec executeAs
    (store: Store)
    (registry: Registry)
    (dbName: string)
    (ids: int64 * int64)
    (foundRows: bool)
    (currentAccount: Auth.Account)
    (stmt: Statement)
    : (int64 * int64) * QueryResult =
    // Query results are stable only for the statement that produced them.
    resetStatementMemo ()

    /// Bodies execute with the trigger's schema `db` as the default
    /// database — MySQL resolves a body's unqualified table names against
    /// the schema the trigger lives in, not the session's current database
    /// (probed: `INSERT INTO a.t` from a session on db b runs the body's
    /// `INSERT INTO work_log` against a.work_log). Returns `Some err` when
    /// a body failed.
    let fireTriggers
        (runStore: Store)
        (db: string)
        (table: string)
        (timing: TriggerTiming)
        (event: TriggerEvent)
        (triggers: StoredTrigger list)
        (rows: (Value[] option * Value[] option) list)
        : QueryResult option =
        match scan runStore db table with
        | Error _ -> None
        | Ok(columns, _) ->
            let chain = triggerChain.Value
            let self = db, normalizeTableName table
            let protectedTables = Set.add self (Set.union (Set.ofList chain) triggerInvocationTables.Value)

            // MySQL rejects every trigger before firing any row when its body
            // writes a table targeted by the invoking statement.
            let checkBody trigger =
                match StoredProgram.parseTrigger (SqlMode.parserOptionsFor trigger.SqlMode) trigger.Body with
                | Result.Error msg -> Result.Error(Err(1064, sprintf "Trigger '%s' body has a syntax error: %s" trigger.Name msg))
                | Result.Ok bodyStatements ->
                    let dmlStatements = bodyStatements |> List.collect StoredProgram.sqlStatements
                    let targets = dmlStatements |> List.choose (writtenTableOf db)

                    match targets |> List.tryFind protectedTables.Contains with
                    | Some target -> Result.Error(err1442 (snd target))
                    | _ ->
                        // Definer privileges are checked at execution so revokes
                        // take effect without recreating the trigger.
                        if trigger.Definer = "" then
                            Result.Error(
                                Err(1449, sprintf "The user specified as a definer ('') does not exist for trigger '%s'" trigger.Name)
                            )
                        else
                            let privileges =
                                dmlStatements |> traverse (checkStoredDefiner runStore trigger.Definer db)

                            match Auth.tryParseAccount trigger.Definer, privileges with
                            | Some account, Result.Ok _ -> Result.Ok(trigger, bodyStatements, account)
                            | _, Result.Error(code, msg) -> Result.Error(Err(code, msg))
                            | None, Result.Ok _ ->
                                Result.Error(Err(1449, sprintf "The user specified as a definer ('') does not exist for trigger '%s'" trigger.Name))

            match triggers |> traverse checkBody with
            | Result.Error err -> Some err
            | Result.Ok bodies ->
                // Eval-time DIRECTONLY backstop, same as generated
                // columns — see `shadowDirectOnly`'s doc.
                let shadowed = shadowDirectOnly "trigger" registry
                triggerChain.Value <- self :: chain

                // An extension's own `SqlError` (including the
                // DirectOnly shadow's 3102) surfaces as a clean
                // error result here rather than an exception, so a
                // failing body doesn't abort a surrounding
                // transaction the way an escaped exception would.
                let runBody (oldRow: Value[] option) (newRow: Value[] option) (statements: TriggerStatement list, account: Auth.Account) : QueryResult =
                    let definerRegistry = registryForDefiner account shadowed
                    let locals = ref Map.empty<string, RoutineVariable>
                    let cursors = ref Map.empty<string, StoredProgram.Cursor>
                    let currentDiagnostics: StoredProgram.DiagnosticsSnapshot ref =
                        ref
                            { Conditions = []
                              RowCount = 0L }

                    let localColumn name columnType =
                        { Name = name
                          Type = columnType
                          NumericDisplay = None
                          Nullable = true
                          Default = None
                          AutoIncrement = false
                          PrimaryKey = false
                          Unique = false
                          Generated = None
                          Comment = ""
                          Collation = None
                          Charset = None
                          OnUpdateCurrentTimestamp = false }

                    let localContext () =
                        let bindings = locals.Value |> Map.toList
                        let columns = bindings |> List.map (snd >> _.Column)
                        let values = bindings |> List.map (snd >> _.Value) |> Array.ofList
                        contextFactory runStore definerRegistry db (columnIndexOf columns) Map.empty None values

                    let setLocal name value =
                        match Map.tryFind name locals.Value with
                        | None -> Error(Err(1327, sprintf "Undeclared variable: %s" name))
                        | Some local ->
                            coerceValue runStore.ExecutionSettings.SqlMode.Strict local.Column value
                            |> Result.mapError storageErr
                            |> Result.map (fun value ->
                                locals.Value <- Map.add name { local with Value = value } locals.Value)

                    withTriggerRowScope
                        { Columns = columns
                          Old = oldRow
                          New = newRow }
                        (fun () ->
                            let runDml statement =
                                withRoutineVariableState locals (fun () ->
                                    match statement with
                                    | SetTriggerNew(column, expression) ->
                                        match timing, event, newRow, resolveColumn columns column with
                                        | Before, (TriggerInsert | TriggerUpdate), Some row, Ok index ->
                                            let context = localContext ()

                                            match evalExpr context expression |> Result.mapError Err with
                                            | Error error -> error
                                            | Ok value ->
                                                match coerceValue runStore.ExecutionSettings.SqlMode.Strict columns.[index] value with
                                                | Error error -> storageErr error
                                                | Ok value ->
                                                    row.[index] <- value
                                                    Affected 0UL
                                        | _ -> Err(1362, "Updating of NEW row is not allowed in after trigger")
                                    | _ ->
                                        try
                                            executeAs runStore definerRegistry db (0L, 0L) foundRows account statement |> snd
                                        with SqlError(code, msg) ->
                                            Err(code, msg))

                            let complete result = result, StoredProgram.Flow.Complete

                            let updateDiagnostics generated result =
                                let conditions =
                                    match errorInfo result with
                                    | Some error -> generated @ [ Diagnostics.fromError error ]
                                    | None -> generated

                                currentDiagnostics.Value <-
                                    { Conditions = conditions
                                      RowCount =
                                        match result with
                                        | Affected count -> int64 count
                                        | _ -> -1L }

                                generated |> List.iter Diagnostics.record

                            let rec runStatements scope statements =
                                match statements with
                                | [] -> complete (Affected 0UL)
                                | statement :: rest ->
                                    let result, generated =
                                        match statement with
                                        | StoredProgram.GetDiagnostics _ -> runStatement scope statement, []
                                        | _ -> Diagnostics.capture (fun () -> runStatement scope statement)

                                    match statement with
                                    | StoredProgram.GetDiagnostics _ -> ()
                                    | _ -> updateDiagnostics generated (fst result)

                                    match result with
                                    | (Err _ as result), _ ->
                                        match errorInfo result with
                                        | Some error -> handleCondition scope rest error
                                        | None -> complete result
                                    | _, StoredProgram.Flow.Complete -> runStatements scope rest
                                    | result, flow -> result, flow

                            and runStatement scope =
                                function
                                | StoredProgram.Sql statement -> runDml statement |> complete
                                | StoredProgram.SelectInto _ ->
                                    Err(1235, "SELECT INTO local variables is not supported in triggers")
                                    |> complete
                                | StoredProgram.TextSql sql ->
                                    match triggerTextExecutor.Value, currentVariableContext () with
                                    | Some execute, Some variables ->
                                        let execution =
                                            { TriggerStore = runStore
                                              TriggerRegistry = definerRegistry
                                              TriggerDatabase = db
                                              TriggerAccount = account
                                              TriggerProtectedTables = protectedTables
                                              TriggerUserVariables = variables.UserVariables }

                                        withRoutineVariableState locals (fun () -> execute execution sql)
                                        |> function
                                            | ResultSet _
                                            | MultipleResults _ ->
                                                Err(1415, "Not allowed to return a result set from a trigger")
                                            | result -> result
                                        |> complete
                                    | _ ->
                                        Err(1235, "Text-only statements are not supported in triggers") |> complete
                                | StoredProgram.DeclareCondition _
                                | StoredProgram.DeclareHandler _ -> complete (Affected 0UL)
                                | StoredProgram.Signal(condition, information) ->
                                    evaluateSignal scope None (Some condition) information
                                | StoredProgram.Resignal(condition, information) ->
                                    evaluateSignal scope scope.ActiveError condition information
                                | StoredProgram.DeclareCursor(name, query) ->
                                    cursors.Value <- Map.add name (StoredProgram.cursor query) cursors.Value
                                    complete (Affected 0UL)
                                | StoredProgram.OpenCursor name ->
                                    match StoredProgram.tryOpenCursor name cursors.Value with
                                    | Result.Error error -> complete (ErrInfo error)
                                    | Ok cursor ->
                                        match runDml cursor.Query with
                                        | ResultSet(columns, rows) ->
                                            let rows =
                                                rows
                                                |> List.map (
                                                    List.map (Option.map VString >> Option.defaultValue VNull)
                                                    >> List.toArray
                                                )
                                                |> List.toArray

                                            cursors.Value <-
                                                StoredProgram.setCursorRows name columns.Length rows cursors.Value

                                            complete (Affected 0UL)
                                        | error -> complete error
                                | StoredProgram.FetchCursor(name, targets) ->
                                    match StoredProgram.tryFetchCursorRow name targets.Length cursors.Value with
                                    | Result.Error error -> complete (ErrInfo error)
                                    | Ok(row, nextCursors) ->
                                        cursors.Value <- nextCursors

                                        match
                                            List.zip targets (Array.toList row)
                                            |> List.tryFind (fst >> fun target -> not (Map.containsKey target locals.Value))
                                        with
                                        | Some(target, _) -> complete (Err(1327, sprintf "Undeclared variable: %s" target))
                                        | None ->
                                            List.zip targets (Array.toList row)
                                            |> traverse (fun (target, value) -> setLocal target value)
                                            |> function
                                                | Ok _ -> complete (Affected 0UL)
                                                | Error error -> complete error
                                | StoredProgram.CloseCursor name ->
                                    match StoredProgram.tryCloseCursor name cursors.Value with
                                    | Result.Error error -> complete (ErrInfo error)
                                    | Ok nextCursors ->
                                        cursors.Value <- nextCursors
                                        complete (Affected 0UL)
                                | StoredProgram.GetDiagnostics diagnostics -> runDiagnostics scope diagnostics
                                | StoredProgram.Declare declaration ->
                                    let column = localColumn declaration.Name declaration.ColumnType

                                    let value =
                                        match declaration.InitialValue with
                                        | None -> Ok VNull
                                        | Some expression -> evalExpr (localContext ()) expression |> Result.mapError Err

                                    value
                                    |> Result.bind (fun value ->
                                        coerceValue runStore.ExecutionSettings.SqlMode.Strict column value
                                        |> Result.mapError storageErr)
                                    |> function
                                        | Error error -> complete error
                                        | Ok value ->
                                            locals.Value <-
                                                Map.add declaration.Name { Column = column; Value = value } locals.Value

                                            complete (Affected 0UL)
                                | StoredProgram.SetLocal(name, expression) ->
                                    match evalExpr (localContext ()) expression with
                                    | Error(code, message) -> complete (Err(code, message))
                                    | Ok value ->
                                        match setLocal name value with
                                        | Ok() -> complete (Affected 0UL)
                                        | Error error -> complete error
                                | StoredProgram.Return _ ->
                                    complete (Err(1313, "RETURN is only allowed in a FUNCTION"))
                                | StoredProgram.Block(label, body) ->
                                    let before = locals.Value
                                    let beforeCursors = cursors.Value
                                    let blockScope =
                                        { Conditions = StoredProgram.conditionDefinitions scope.Conditions body
                                          Statements = body
                                          ActiveError = scope.ActiveError
                                          StackedDiagnostics = scope.StackedDiagnostics }

                                    let result, flow = runStatements blockScope body
                                    locals.Value <- StoredProgram.restoreOuterScope body before locals.Value
                                    cursors.Value <- StoredProgram.restoreOuterCursors body beforeCursors cursors.Value

                                    match flow, label with
                                    | StoredProgram.Flow.Leave target, Some label when target = label ->
                                        result, StoredProgram.Flow.Complete
                                    | StoredProgram.Flow.ExitHandler, _ -> result, StoredProgram.Flow.Complete
                                    | _ -> result, flow
                                | StoredProgram.If(condition, whenTrue, whenFalse) ->
                                    let context = localContext ()

                                    match evalExpr context condition with
                                    | Error(code, message) -> complete (Err(code, message))
                                    | Ok value when truthy value = Some true -> runStatements scope whenTrue
                                    | Ok _ -> runStatements scope whenFalse
                                | StoredProgram.Case(selector, branches, otherwise) ->
                                    let branchSelector = StoredProgram.caseBranchIndexExpression selector branches

                                    match evalExpr (localContext ()) branchSelector with
                                    | Error(code, message) -> complete (Err(code, message))
                                    | Ok(VInt index) when index >= 0L ->
                                        branches |> List.item (int index) |> snd |> runStatements scope
                                    | Ok _ ->
                                        match otherwise with
                                        | Some body -> runStatements scope body
                                        | None -> complete (Err(1339, "Case not found for CASE statement"))
                                | StoredProgram.While(label, condition, body) ->
                                    let rec iterate () =
                                        Storage.queryCancellation.Value.ThrowIfCancellationRequested()

                                        match evalExpr (localContext ()) condition with
                                        | Error(code, message) -> complete (Err(code, message))
                                        | Ok value when truthy value <> Some true -> complete (Affected 0UL)
                                        | Ok _ ->
                                            match runStatements scope body with
                                            | (Err _ as error), _ -> complete error
                                            | result, StoredProgram.Flow.Leave target when label = Some target ->
                                                result, StoredProgram.Flow.Complete
                                            | _, StoredProgram.Flow.Iterate target when label = Some target -> iterate ()
                                            | _, StoredProgram.Flow.Complete -> iterate ()
                                            | result, flow -> result, flow

                                    iterate ()
                                | StoredProgram.Repeat(label, body, until) ->
                                    let rec iterate () =
                                        Storage.queryCancellation.Value.ThrowIfCancellationRequested()

                                        match runStatements scope body with
                                        | (Err _ as error), _ -> complete error
                                        | result, StoredProgram.Flow.Leave target when label = Some target ->
                                            result, StoredProgram.Flow.Complete
                                        | _, StoredProgram.Flow.Iterate target when label = Some target -> iterate ()
                                        | result, StoredProgram.Flow.Complete ->
                                            match evalExpr (localContext ()) until with
                                            | Error(code, message) -> complete (Err(code, message))
                                            | Ok value when truthy value = Some true -> complete result
                                            | Ok _ -> iterate ()
                                        | result, flow -> result, flow

                                    iterate ()
                                | StoredProgram.Loop(label, body) ->
                                    let rec iterate () =
                                        Storage.queryCancellation.Value.ThrowIfCancellationRequested()

                                        match runStatements scope body with
                                        | (Err _ as error), _ -> complete error
                                        | result, StoredProgram.Flow.Leave target when label = Some target ->
                                            result, StoredProgram.Flow.Complete
                                        | _, StoredProgram.Flow.Iterate target when label = Some target -> iterate ()
                                        | _, StoredProgram.Flow.Complete -> iterate ()
                                        | result, flow -> result, flow

                                    iterate ()
                                | StoredProgram.Leave label -> Affected 0UL, StoredProgram.Flow.Leave label
                                | StoredProgram.Iterate label -> Affected 0UL, StoredProgram.Flow.Iterate label

                            and evaluateSignal scope original condition information =
                                let evaluated =
                                    information
                                    |> traverse (fun (name, expression) ->
                                        evalExpr (localContext ()) expression |> Result.map (fun value -> name, value))

                                match evaluated with
                                | Error(code, message) -> complete (Err(code, message))
                                | Ok information ->
                                    match StoredProgram.signalError scope.Conditions original condition information with
                                    | Error(code, message) -> complete (Err(code, message))
                                    | Ok error -> handleCondition scope [] error

                            and runDiagnostics scope diagnostics =
                                let snapshot =
                                    match diagnostics.Area with
                                    | StoredProgram.Current -> Ok currentDiagnostics.Value
                                    | StoredProgram.Stacked ->
                                        match scope.StackedDiagnostics with
                                        | Some snapshot -> Ok snapshot
                                        | None ->
                                            Error(ErrState(3004, "0Z002", "GET STACKED DIAGNOSTICS when handler not active"))

                                snapshot
                                |> Result.bind (fun snapshot ->
                                    let conditionNumber =
                                        match diagnostics.Request with
                                        | StoredProgram.ConditionInformation(expression, _) ->
                                            evalExpr (localContext ()) expression
                                            |> Result.mapError Err
                                            |> Result.map StoredProgram.tryDiagnosticsConditionNumber
                                        | StoredProgram.StatementInformation _ -> Ok None

                                    conditionNumber
                                    |> Result.bind (fun conditionNumber ->
                                        match
                                            StoredProgram.diagnosticsAssignments
                                                snapshot
                                                diagnostics.Request
                                                conditionNumber
                                        with
                                        | None ->
                                            currentDiagnostics.Value <-
                                                { Conditions = [ Diagnostics.invalidConditionNumber ]
                                                  RowCount = -1L }

                                            Ok()
                                        | Some assignments -> applyDiagnosticsAssignments assignments))
                                |> function
                                    | Ok() -> complete (Affected 0UL)
                                    | Error result -> complete result

                            and applyDiagnosticsAssignments assignments =
                                let rec apply =
                                    function
                                    | [] -> Ok()
                                    | (StoredProgram.LocalVariable name, value) :: rest ->
                                        setLocal name value |> Result.bind (fun () -> apply rest)
                                    | (StoredProgram.UserVariable variable, value) :: rest ->
                                        match currentVariableContext () with
                                        | None -> Error(Err(1105, "User-variable context is unavailable"))
                                        | Some bindings
                                            when not (Map.containsKey variable.Name bindings.UserVariables.Value)
                                                 && bindings.UserVariables.Value.Count >= bindings.MaxUserVariables ->
                                            Error(Err(1105, "Too many user-defined variables"))
                                        | Some bindings ->
                                            bindings.UserVariables.Value <-
                                                Map.add variable.Name value bindings.UserVariables.Value

                                            apply rest

                                apply assignments

                            and handleCondition scope rest error =
                                currentDiagnostics.Value <-
                                    StoredProgram.diagnosticsForError currentDiagnostics.Value error

                                match StoredProgram.tryHandler scope.Conditions scope.Statements error with
                                | None when StoredProgram.isWarning error ->
                                    Diagnostics.record (Diagnostics.fromWarning error)
                                    runStatements scope rest
                                | None -> complete (ErrInfo error)
                                | Some(action, body) ->
                                    let handlerScope =
                                        { scope with
                                            Statements = []
                                            ActiveError = Some error
                                            StackedDiagnostics = Some currentDiagnostics.Value }

                                    match runStatement handlerScope body with
                                    | (Err _ as result), _ -> complete result
                                    | result, StoredProgram.Flow.Complete ->
                                        match action with
                                        | StoredProgram.HandlerAction.Continue -> runStatements scope rest
                                        | StoredProgram.HandlerAction.Exit -> result, StoredProgram.Flow.ExitHandler
                                    | result, flow -> result, flow

                            let scope =
                                { Conditions = StoredProgram.conditionDefinitions Map.empty statements
                                  Statements = statements
                                  ActiveError = None
                                  StackedDiagnostics = None }

                            runStatements scope statements |> fst)

                try
                    rows
                    |> List.tryPick (fun (oldRow, newRow) ->
                        bodies
                        |> List.tryPick (fun (trigger, statements, account) ->
                            Storage.withExecutionSettings runStore (triggerExecutionSettings trigger) (fun () ->
                                match runBody oldRow newRow (statements, account) with
                                | Err _ as e -> Some e
                                | _ -> None)))
                finally
                    triggerChain.Value <- chain

    let triggerStorageResult =
        function
        | None -> Ok()
        | Some(Err(code, message)) -> Error(ExpressionError(code, message))
        | Some _ -> Error(ExpressionError(1105, "Trigger execution failed"))

    let validateViewCandidate (runStore: Store) (db: string) (table: string) (columns: ColumnDef list) (candidate: Value[]) =
        match viewCheckScope.Value with
        | Some scope
            when scope.Database.Equals(db, System.StringComparison.OrdinalIgnoreCase)
                 && scope.Table.Equals(table, System.StringComparison.OrdinalIgnoreCase) ->
            match scope.Predicate with
            | None -> Ok candidate
            | Some predicate ->
                let context =
                    contextFactory
                        runStore
                        registry
                        db
                        (columnIndexOf columns)
                        (singleQualifier table columns)
                        None
                        candidate

                match evalExpr { context with Clause = WhereClause } predicate with
                | Ok value when truthy value = Some true -> Ok candidate
                | Ok _ -> Error(ExpressionError(1369, sprintf "CHECK OPTION failed '%s'" scope.View))
                | Error error -> Error(ExpressionError error)
        | _ -> Ok candidate

    let evaluateFunctionalDefaults
        (runStore: Store)
        (db: string)
        (table: string)
        (columns: ColumnDef list)
        (omitted: Set<int>)
        (candidate: Value[])
        =
        let result = Array.copy candidate
        let context = contextFactory runStore registry db (columnIndexOf columns) (singleQualifier table columns) None

        omitted
        |> Set.toList
        |> List.fold
            (fun state index ->
                state
                |> Result.bind (fun () ->
                    match columns.[index].Default with
                    | Some(DExpression expression) ->
                        evalExpr (context result) expression
                        |> Result.mapError ExpressionError
                        |> Result.bind (coerceValue runStore.ExecutionSettings.SqlMode.Strict columns.[index])
                        |> Result.map (fun value -> result.[index] <- value)
                    | _ -> Ok()))
            (Ok())
        |> Result.map (fun () -> result)

    let evaluateInsertRows
        (runStore: Store)
        (db: string)
        (table: string)
        (columns: ColumnDef list)
        (targetColumns: string list)
        (rows: Expr list list)
        =
        let indices =
            if targetColumns.IsEmpty then
                Ok [ 0 .. columns.Length - 1 ]
            else
                targetColumns |> traverse (resolveAssignableColumn columns table)

        let isDefault = function
            | FuncCall(name, []) when name.Equals("DEFAULT", System.StringComparison.OrdinalIgnoreCase) -> true
            | _ -> false

        let context = contextFactory runStore registry dbName Map.empty Map.empty None [||]

        let evaluateRow (indices: int list) (row: Expr list) =
            if row.Length <> indices.Length then
                Error(ColumnCountMismatch(indices.Length, row.Length))
            else
                row
                |> traverse (fun expression ->
                    if isDefault expression then
                        Ok None
                    else
                        evalExpr context expression |> Result.map Some |> Result.mapError ExpressionError)
                |> Result.bind (fun values ->
                    let candidate = columns |> List.map (evalDefaultWithMode (temporalCoercionMode runStore)) |> Array.ofList

                    List.zip indices values
                    |> List.iter (function
                        | index, Some value -> candidate.[index] <- value
                        | _ -> ())

                    let defaulted =
                        List.zip indices values
                        |> List.choose (function
                            | index, None -> Some index
                            | _ -> None)

                    defaulted
                    |> List.tryPick (fun index ->
                        let column = columns.[index]

                        if column.Default.IsNone && not column.Nullable && not column.AutoIncrement && column.Generated.IsNone then
                            Some(Error(ExpressionError(1364, sprintf "Field '%s' doesn't have a default value" column.Name)))
                        else
                            None)
                    |> Option.defaultValue (Ok())
                    |> Result.bind (fun () ->
                        evaluateFunctionalDefaults runStore db table columns (Set.ofList defaulted) candidate)
                    |> Result.map (fun candidate -> indices |> List.map (fun index -> candidate.[index])))

        indices
        |> Result.bind (fun indices -> rows |> traverse (evaluateRow indices))

    let prepareInsertRow (runStore: Store) (db: string) (table: string) (columns: ColumnDef list) (omitted: Set<int>) (candidate: Value[]) =
        let finish candidate =
            computeGeneratedRow runStore registry db table columns candidate
            |> Result.bind (validateViewCandidate runStore db table columns)

        evaluateFunctionalDefaults runStore db table columns omitted candidate
        |> Result.bind (fun candidate ->
            match beforeInsertTriggers runStore db table with
            | [] -> finish candidate
            | triggers ->
                match fireTriggers runStore db table Before TriggerInsert triggers [ None, Some candidate ] with
                | Some(Err(code, message)) -> Error(ExpressionError(code, message))
                | _ -> finish candidate)

    /// Runs an insert branch's storage write and fires AFTER INSERT triggers with
    /// MySQL's statement atomicity: when triggers exist, the insert and
    /// every body's effects land in a private `beginTransactionSnapshot`
    /// (the multi-table UPDATE precedent), merged back — one commit, WAL
    /// events ordered after the originating statement's — only when every
    /// body succeeded. A body error discards the snapshot, so the
    /// originating rows roll back with it (probed MySQL semantics) and the
    /// OK-packet ids don't advance. Trigger-free inserts write directly.
    let finishInsert (db: string) (table: string) (doInsert: Store -> Result<InsertOutcome, StorageError>) : (int64 * int64) * QueryResult =
        let ok (outcome: InsertOutcome) =
            outcome.IgnoredErrors
            |> List.iter (fun error ->
                let code, message = toMySqlError error
                Diagnostics.warning code message)

            nextIds ids (outcome.LastInsertId, outcome.GeneratedId), Affected(uint64 outcome.Affected)

        let before = beforeInsertTriggers store db table
        let after = afterInsertTriggers store db table

        match before, after with
        | [], [] ->
            match doInsert store with
            | Ok outcome -> ok outcome
            | Error e -> ids, storageErr e
        | _, triggers ->
            let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

            match doInsert snapshot with
            | Error e -> ids, storageErr e
            | Ok outcome when outcome.InsertedRows.IsEmpty ->
                // Nothing actually inserted (all-duplicate upsert/IGNORE) —
                // nothing to fire, but the update-path writes still count.
                Storage.commitCatalogInto store baseCatalog snapshot
                ok outcome
            | Ok outcome ->
                let rows = outcome.InsertedRows |> List.map (fun row -> None, Some row)

                match fireTriggers snapshot db table After TriggerInsert triggers rows with
                | Some err -> ids, err
                | None ->
                    Storage.commitCatalogInto store baseCatalog snapshot
                    ok outcome

    let onDuplicateUpdater
        (table: string)
        (tableColumns: ColumnDef list)
        (columnIndex: Map<string, int list>)
        (onDuplicateUpdate: (string * Expr) list)
        (sourceBindings: (Expr * ColumnDef option * Value) list)
        (existing: Value[])
        (candidate: Value[])
        : Result<Value[], StorageError> =
        let targetContext = contextFactory store registry dbName columnIndex (singleQualifier table tableColumns) None existing
        let ctx = withInsertSelectSources targetContext sourceBindings

        onDuplicateUpdate
        |> traverse (fun (name, expr) ->
            match resolveAssignableColumn tableColumns table name with
            | Error e -> Error e
            | Ok idx ->
                let expression =
                    expr
                    |> substituteValuesFunc columnIndex candidate

                match evalExpr ctx expression with
                | Ok v -> Ok(idx, v)
                | Error err -> Error(ExpressionError err))
        |> Result.map (fun idxVals ->
            let newRow = Array.copy existing
            for idx, v in idxVals do
                newRow.[idx] <- v
            let assignedIdxs = idxVals |> List.map fst |> Set.ofList
            applyOnUpdateTimestamps (temporalCoercionMode store) tableColumns assignedIdxs existing newRow)

    let upsertEvaluated
        (db: string)
        (table: string)
        (cols: string list option)
        (rowsValues: Value list list)
        (sourceBindings: (Expr * ColumnDef option * Value) list array)
        (onDuplicateUpdate: (string * Expr) list)
        =
        match scan store db table with
        | Error e -> ids, storageErr e
        | Ok(tableColumns, _) ->
            let columnIndex = columnIndexOf tableColumns

            finishInsert db table (fun s ->
                let prepare omitted candidate =
                    evaluateFunctionalDefaults s db table tableColumns omitted candidate
                    |> Result.bind (computeGeneratedRow s registry db table tableColumns)
                    |> Result.bind (validateViewCandidate s db table tableColumns)

                let computeGenerated candidate =
                    computeGeneratedRow s registry db table tableColumns candidate
                    |> Result.bind (validateViewCandidate s db table tableColumns)

                let applyUpdate ordinal existing candidate =
                    onDuplicateUpdater table tableColumns columnIndex onDuplicateUpdate sourceBindings.[ordinal] existing candidate
                    |> Result.bind computeGenerated

                upsertRowsWithOrdinal s db table cols rowsValues prepare applyUpdate foundRows)

    let replaceEvaluatedWith
        (db: string)
        (table: string)
        (cols: string list option)
        (rowsValues: Value list list)
        (deferred: Set<int>)
        (prepareFor: Store -> ColumnDef list -> int -> Set<int> -> Value[] -> Result<Value[], StorageError>)
        =
        let beforeInsert = beforeInsertTriggers store db table
        let afterInsert = afterInsertTriggers store db table
        let beforeDelete = triggersFor store db table "BEFORE" "DELETE"
        let afterDelete = triggersFor store db table "AFTER" "DELETE"
        let hasTriggers = not (beforeInsert.IsEmpty && afterInsert.IsEmpty && beforeDelete.IsEmpty && afterDelete.IsEmpty)

        match hasTriggers, scan store db table with
        | _, Error error -> ids, storageErr error
        | false, Ok(tableColumns, _) ->
            finishInsert db table (fun targetStore ->
                replaceRowsWithOrdinal
                    targetStore
                    db
                    table
                    cols
                    rowsValues
                    deferred
                    (prepareFor targetStore tableColumns))
        | true, Ok(tableColumns, _) ->
            let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
            let prepare = prepareFor snapshot tableColumns

            let fire timing event (triggers: StoredTrigger list) rows =
                if List.isEmpty triggers then
                    Ok()
                else
                    match fireTriggers snapshot db table timing event triggers rows with
                    | Some error -> Error error
                    | None -> Ok()

            let deleteConflict result ((rowId, oldRow) as conflict) =
                result
                |> Result.bind (fun () -> fire Before TriggerDelete beforeDelete [ Some oldRow, None ])
                |> Result.bind (fun () ->
                    deleteRowsCandidates snapshot db table [ conflict ] (fun _ -> Ok true)
                    |> Result.map ignore
                    |> Result.mapError storageErr)
                |> Result.bind (fun () -> fire After TriggerDelete afterDelete [ Some oldRow, None ])

            let step result (rowNumber, values) =
                Diagnostics.withRowNumber (rowNumber + 1) (fun () ->
                    result
                    |> Result.bind (fun (firstAuto, lastExplicit, affected, inserted) ->
                        Storage.prepareInsertCandidateWithDeferred
                            snapshot
                            db
                            table
                            cols
                            values
                            deferred
                            (prepare rowNumber)
                        |> Result.mapError storageErr
                        |> Result.bind (fun prepared ->
                            replaceConflictRows snapshot db table prepared.Values
                            |> Result.mapError storageErr
                            |> Result.bind (fun conflicts ->
                                conflicts
                                |> List.fold deleteConflict (Ok())
                                |> Result.bind (fun () ->
                                    insertPreparedCandidate snapshot db table prepared
                                    |> Result.mapError storageErr)
                                |> Result.bind (fun _ ->
                                    fire After TriggerInsert afterInsert [ None, Some prepared.Values ])
                                |> Result.map (fun () ->
                                    let firstAuto, lastExplicit =
                                        match prepared.AssignedAutoId with
                                        | Some(true, value) -> Option.orElse (Some value) firstAuto, lastExplicit
                                        | Some(false, value) -> firstAuto, Some value
                                        | None -> firstAuto, lastExplicit

                                    firstAuto,
                                    lastExplicit,
                                    affected + conflicts.Length + 1,
                                    prepared.Values :: inserted)))))

            match rowsValues |> List.indexed |> List.fold step (Ok(None, None, 0, [])) with
            | Error error -> ids, error
            | Ok(firstAuto, lastExplicit, affected, inserted) ->
                Storage.commitCatalogInto store baseCatalog snapshot

                let outcome =
                    { LastInsertId = Option.defaultValue 0L (Option.orElse lastExplicit firstAuto)
                      GeneratedId = firstAuto
                      Affected = affected
                      InsertedRows = List.rev inserted
                      IgnoredErrors = [] }

                nextIds ids (outcome.LastInsertId, outcome.GeneratedId), Affected(uint64 outcome.Affected)

    let replaceEvaluated (db: string) (table: string) (cols: string list option) (rowsValues: Value list list) =
        replaceEvaluatedWith
            db
            table
            cols
            rowsValues
            Set.empty
            (fun targetStore tableColumns _ omitted candidate ->
                prepareInsertRow targetStore db table tableColumns omitted candidate)

    let executeViewWrite (view: UpdatableView) (target: ViewColumnTarget) statement =
        let retarget (access: ViewAccess) =
            let qualified = access.Database + "." + access.Table

            match statement with
            | Insert(_, columns, rows, updates, ignore) -> Insert(qualified, columns, rows, updates, ignore)
            | InsertSelect(_, columns, select, updates, ignore) -> InsertSelect(qualified, columns, select, updates, ignore)
            | Replace(_, columns, rows) -> Replace(qualified, columns, rows)
            | ReplaceSelect(_, columns, select) -> ReplaceSelect(qualified, columns, select)
            | ReplaceSet(_, assignments) -> ReplaceSet(qualified, assignments)
            | LoadData load -> LoadData { load with Table = qualified }
            | Update update -> Update { update with From = { update.From with Database = Some access.Database; Table = access.Table } }
            | Delete delete -> Delete { delete with From = { delete.From with Database = Some access.Database; Table = access.Table } }
            | other -> other

        let authorizedRegistry =
            if view.AccessPath.IsEmpty then
                registryForViewSecurity store registry view.SecurityType view.Definer view.ViewDatabase statement
            else
                view.AccessPath
                |> List.fold
                    (fun state access ->
                        state
                        |> Result.bind (fun current ->
                            registryForViewSecurity store current access.SecurityType access.Definer access.Database (retarget access)))
                    (Ok registry)

        match authorizedRegistry with
        | Error(code, message) -> ids, Err(code, message)
        | Ok authorized ->
            let execute () = executeAs store authorized dbName ids foundRows currentAccount statement
            let targetKey = target.Database, target.Table, target.Qualifier

            match Map.tryFind targetKey view.CheckPredicates with
            | Some predicate ->
                DynamicScope.withValue
                    viewCheckScope
                    (Some
                        { Database = target.Database
                          Table = target.Table
                          View = view.ViewDatabase + "." + view.ViewName
                          Predicate = Some predicate })
                    execute
            | None -> execute ()

    match stmt with
    | Update update when not update.Ctes.IsEmpty ->
        let mutable nextIds = ids
        let expressions =
            (update.Assignments |> List.map _.Value)
            @ Option.toList update.Where
            @ (update.OrderBy |> List.map fst)
            @ Option.toList update.Limit
        let ctes = referencedMutationCtes update.Ctes update.Joins expressions

        let result =
            withCteQueryResult store registry dbName ctes (fun () ->
                let ids, result = executeAs store registry dbName ids foundRows currentAccount (Update { update with Ctes = [] })
                nextIds <- ids
                result)

        nextIds, result
    | Delete delete when not delete.Ctes.IsEmpty ->
        let mutable nextIds = ids
        let expressions =
            Option.toList delete.Where
            @ (delete.OrderBy |> List.map fst)
            @ Option.toList delete.Limit
        let ctes = referencedMutationCtes delete.Ctes delete.Joins expressions

        let result =
            withCteQueryResult store registry dbName ctes (fun () ->
                let ids, result = executeAs store registry dbName ids foundRows currentAccount (Delete { delete with Ctes = [] })
                nextIds <- ids
                result)

        nextIds, result
    | SetTriggerNew _ -> ids, Err(1064, "SET NEW is only valid in a trigger body")

    | CreateDatabase(name, ifNotExists) ->
        match Storage.createDatabase store name with
        | Ok() -> ids, Affected 0UL
        | Error(DatabaseExists _) when ifNotExists -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | DropDatabase(name, ifExists) ->
        let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

        match Storage.dropDatabase snapshot name with
        | Ok() ->
            let belongsToDatabase schemaIndex (row: Value[]) =
                let schema = toText row.[schemaIndex] |> Option.defaultValue ""
                Ok(System.String.Equals(schema, name, System.StringComparison.OrdinalIgnoreCase))

            match
                deleteRows snapshot "mysql" "views" (belongsToDatabase 1),
                deleteRows snapshot "mysql" "triggers" (belongsToDatabase 1),
                deleteRows snapshot "mysql" "check_constraints" (belongsToDatabase 1)
            with
            | Ok _, Ok _, Ok _ ->
                Storage.commitCatalogInto store baseCatalog snapshot
                ids, Affected 0UL
            | Error error, _, _
            | _, Error error, _
            | _, _, Error error -> ids, storageErr error
        | Error(NoSuchDatabase _) when ifExists -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | AlterDatabase requestedName ->
        // The charset/collate tail is parsed and discarded (see
        // `Parser.databaseOptions`'s doc) — nothing in the catalog needs to
        // record it, so this is just an existence check.
        let name = requestedName |> Option.defaultValue dbName

        if Storage.databaseExists store name then
            ids, Affected 0UL
        else
            ids, storageErr (NoSuchDatabase name)

    | CreateTableAs(name, query, ifNotExists) ->
        let destinationDb, destinationName = splitQualified dbName name
        let destinationExists = scan store destinationDb destinationName |> Result.isOk
        let viewExists = tryStoredView store destinationDb destinationName |> Option.isSome

        if ifNotExists && (destinationExists || viewExists) then
            ids, Affected 0UL
        elif destinationExists || viewExists then
            ids, storageErr (TableExists destinationName)
        else
            let selected =
                match query with
                | Select select ->
                    let result, metadata, rows = runSelectStmt store registry dbName select None
                    let names = match result with ResultSet(names, _) -> names | _ -> []
                    result, metadata, rows, selectColumnCollations store registry dbName select names
                | Union(first, rest, orderBy, limit, offset) ->
                    let result, metadata, rows = runUnionStmtWithOuter store registry dbName first rest orderBy limit offset None
                    let names = match result with ResultSet(names, _) -> names | _ -> []
                    result, metadata, rows, List.replicate names.Length Collation.defaultCollation
                | _ -> Err(1064, "CREATE TABLE ... AS requires a query"), [], [], []

            match selected with
            | Err(code, message), _, _, _ -> ids, Err(code, message)
            | ResultSet(names, _), metadata, rows, collations ->
                let columns = deriveColumns names collations metadata
                let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
                Storage.setStrictMode snapshot store.ExecutionSettings.SqlMode.Strict

                let created =
                    createTableSeeded snapshot destinationDb destinationName columns [] [] None None None None None
                    |> Result.bind (fun () ->
                        rows
                        |> List.map Array.toList
                        |> insertRows snapshot destinationDb destinationName None
                        |> Result.map _.Affected)

                match created with
                | Ok affected ->
                    Storage.commitCatalogInto store baseCatalog snapshot
                    ids, Affected(uint64 affected)
                | Error error -> ids, storageErr error
            | _ -> ids, Err(1064, "CREATE TABLE ... AS requires a query")

    | CreateTableLike(name, source, ifNotExists) ->
        let destinationDb, destinationName = splitQualified dbName name
        let destinationExists = scan store destinationDb destinationName |> Result.isOk

        if ifNotExists && (destinationExists || tryStoredView store destinationDb destinationName |> Option.isSome) then
            ids, Affected 0UL
        else
            let sourceDb, sourceName = splitQualified dbName source

            let sourceTable =
                store.Catalog
                |> Map.tryFind (sourceDb.ToLowerInvariant())
                |> Option.bind (Map.tryFind (normalizeTableName sourceName))

            match sourceTable with
            | None -> ids, storageErr (NoSuchTable sourceName)
            | Some table ->
                let decodeCheck (check: StoredCheck) =
                    match Parser.parse ("SELECT " + check.Clause) with
                    | Ok(Select { Projections = [ expression, _ ] }) ->
                        Ok
                            { Name = None
                              Expression = expression
                              Enforced = check.Enforced
                              Column = check.Column }
                    | _ -> Error(ExpressionError(1105, sprintf "Stored CHECK expression for '%s' is invalid" check.Name))

                match storedChecks store sourceDb sourceName |> traverse decodeCheck with
                | Error error -> ids, storageErr error
                | Ok checks ->
                    executeAs
                        store
                        registry
                        dbName
                        ids
                        foundRows
                        currentAccount
                        (CreateTable
                            { Name = name
                              Columns = table.Columns
                              Indexes = table.Indexes
                              ForeignKeys = []
                              Checks = checks
                              IfNotExists = ifNotExists
                              Charset = table.TableCharset
                              Collation = table.TableCollation
                              AutoIncrementSeed = None
                              Comment = if table.TableComment = "" then None else Some table.TableComment
                              Partitioning = table.Partitioning })

    | CreateTable table ->
        let db, name = splitQualified dbName table.Name

        match tryStoredView store db name with
        | Some _ when table.IfNotExists -> ids, Affected 0UL
        | Some _ -> ids, storageErr (TableExists name)
        | None ->
            match
                rejectDirectOnlyGenerated registry table.Columns,
                rejectQuantifiedComparisonsInGenerated table.Columns,
                rejectSessionVariablesInGenerated table.Columns,
                validateFunctionalDefaults registry table.Columns,
                validateIndexExpressions registry table.Columns table.Indexes
            with
            | Some err, _, _, _, _
            | _, Some err, _, _, _
            | _, _, Some err, _, _
            | _, _, _, Error err, _ -> ids, err
            | _, _, _, _, Error error -> ids, storageErr error
            | None, None, None, Ok(), Ok() ->
                let alreadyExists = scan store db name |> Result.isOk

                if alreadyExists && table.IfNotExists then
                    ids, Affected 0UL
                else
                    let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
                    Storage.setStrictMode snapshot store.ExecutionSettings.SqlMode.Strict

                    let created =
                        createTableSeeded
                            snapshot
                            db
                            name
                            table.Columns
                            table.Indexes
                            table.ForeignKeys
                            table.Charset
                            table.Collation
                            table.AutoIncrementSeed
                            table.Comment
                            table.Partitioning
                        |> Result.bind (fun () -> storeCheckDefinitions snapshot registry db name table.Columns table.Checks)
                        |> Result.bind (fun () -> validateCheckForeignKeys snapshot db name table.ForeignKeys)

                    match created with
                    | Ok() ->
                        Storage.commitCatalogInto store baseCatalog snapshot
                        reportNumericDisplayWarnings table.Columns
                        ids, Affected 0UL
                    | Error(TableExists _) when table.IfNotExists -> ids, Affected 0UL
                    | Error e -> ids, storageErr e

    | DropTable(names, ifExists) ->
        let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

        let targets = names |> List.map (splitQualified dbName)

        let removeStoredObjects (db, name) =
            deleteRows
                snapshot
                "mysql"
                "triggers"
                (fun row ->
                    let text i = toText row.[i] |> Option.defaultValue ""

                    Ok(
                        System.String.Equals(text 1, db, System.StringComparison.OrdinalIgnoreCase)
                        && System.String.Equals(text 2, normalizeTableName name, System.StringComparison.OrdinalIgnoreCase)
                    ))
            |> Result.bind (fun _ -> removeStoredChecks snapshot db name |> Result.map ignore)

        match dropTables snapshot ifExists targets |> Result.bind (traverse removeStoredObjects) with
        | Ok _ ->
            Storage.commitCatalogInto store baseCatalog snapshot
            ids, Affected 0UL
        | Error e -> ids, storageErr e

    | AlterTable(table, actions) ->
        let db, table = splitQualified dbName table

        let unsupportedEngine =
            actions
            |> List.tryPick (function
                | SetEngine name when not (System.String.Equals(name, "InnoDB", System.StringComparison.OrdinalIgnoreCase)) -> Some name
                | _ -> None)

        let addedColumns =
            actions
            |> List.choose (function
                | AddColumn(c, _)
                | ModifyColumn(c, _)
                | ChangeColumn(_, c, _) -> Some c
                | _ -> None)

        match unsupportedEngine, rejectDirectOnlyGenerated registry addedColumns, rejectQuantifiedComparisonsInGenerated addedColumns, rejectSessionVariablesInGenerated addedColumns with
        | Some engine, _, _, _ -> ids, Err(1286, sprintf "Unknown storage engine '%s'" engine)
        | None, Some err, _, _
        | None, _, Some err, _
        | None, _, _, Some err -> ids, err
        | None, None, None, None ->
            let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
            Storage.setStrictMode snapshot store.ExecutionSettings.SqlMode.Strict

            let finalTable =
                actions
                |> List.choose (function
                    | RenameTo name -> Some name
                    | _ -> None)
                |> List.tryLast
                |> Option.defaultValue table

            let physicalActions =
                actions
                |> List.filter (function
                    | AddCheck _
                    | DropCheck _
                    | SetCheckEnforced _
                    | SetEngine _ -> false
                    | _ -> true)

            let equal left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)

            let originalCheckNames =
                storedChecks snapshot db table
                |> List.map (fun check -> check.Name.ToLowerInvariant())
                |> Set.ofList

            let explicitlyDropped =
                actions
                |> List.choose (function
                    | DropCheck name -> Some(name.ToLowerInvariant())
                    | _ -> None)
                |> Set.ofList

            let prepareColumnChanges () =
                let activeChecks =
                    storedChecks snapshot db table
                    |> List.filter (fun check -> not (explicitlyDropped.Contains(check.Name.ToLowerInvariant())))

                let referencedColumns (check: StoredCheck) =
                    match Parser.parseExpression check.Clause with
                    | Result.Error _ -> []
                    | Result.Ok expression -> checkColumnReferences expression |> List.map snd

                let references column check = referencedColumns check |> List.exists (equal column)

                let referencesOnly column check =
                    match referencedColumns check with
                    | [] -> false
                    | columns -> columns |> List.forall (equal column)

                let removeAutomatic (check: StoredCheck) =
                    deleteRows snapshot "mysql" "check_constraints" (fun row ->
                        Ok(checkRowSatisfies (fun entry -> equal entry.Schema db && equal entry.Table table && equal entry.Name check.Name) row))
                    |> Result.map ignore

                let checkAction action =
                    match action with
                    | DropColumn column ->
                        let dependent = activeChecks |> List.filter (references column)
                        let automatic, blocking =
                            dependent
                            |> List.partition (referencesOnly column)

                        match blocking with
                        | check :: _ ->
                            Error(
                                ExpressionError(
                                    3959,
                                    sprintf "Check constraint '%s' uses column '%s', hence column cannot be dropped or renamed." check.Name column
                                )
                            )
                        | [] -> automatic |> traverse removeAutomatic |> Result.map ignore
                    | RenameColumnTo(oldName, newName)
                    | ChangeColumn(oldName, { Name = newName }, _) when not (equal oldName newName) ->
                        match activeChecks |> List.tryFind (references oldName) with
                        | Some check ->
                            Error(
                                ExpressionError(
                                    3959,
                                    sprintf "Check constraint '%s' uses column '%s', hence column cannot be dropped or renamed." check.Name oldName
                                )
                            )
                        | None -> Ok()
                    | _ -> Ok()

                let removeExplicit =
                    explicitlyDropped
                    |> Set.toList
                    |> traverse (fun name ->
                        deleteRows snapshot "mysql" "check_constraints" (fun row ->
                            Ok(checkRowSatisfies (fun entry -> equal entry.Schema db && equal entry.Table table && equal entry.Name name) row))
                        |> Result.map ignore)
                    |> Result.map ignore

                removeExplicit
                |> Result.bind (fun () -> actions |> List.fold (fun state action -> state |> Result.bind (fun () -> checkAction action)) (Ok()))

            let alterPhysical =
                prepareColumnChanges ()
                |> Result.bind (fun () ->
                    if physicalActions.IsEmpty then
                        scan snapshot db table |> Result.map ignore
                    else
                        alterTable snapshot db table physicalActions
                        |> withGeneratedRecomputed snapshot registry dbName db table)

            let fillAddedFunctionalDefaults () =
                let names =
                    actions
                    |> List.choose (function
                        | AddColumn({ Default = Some(DExpression _) } as column, _) -> Some column.Name
                        | _ -> None)

                if names.IsEmpty then
                    Ok()
                else
                    scan snapshot db finalTable
                    |> Result.bind (fun (columns, _) ->
                        names
                        |> traverse (resolveColumn columns)
                        |> Result.bind (fun indices ->
                            let omitted = Set.ofList indices

                            updateRows
                                snapshot
                                db
                                finalTable
                                None
                                (fun _ -> Ok true)
                                (fun row ->
                                    evaluateFunctionalDefaults snapshot db finalTable columns omitted row)))
                    |> Result.map ignore

            let retargetAlterObjects () =
                if equal table finalTable then
                    Ok()
                else
                    let retargetChecks =
                        updateRows
                            snapshot
                            "mysql"
                            "check_constraints"
                            None
                            (fun row -> Ok(checkRowSatisfies (fun entry -> equal entry.Schema db && equal entry.Table table) row))
                            (fun row ->
                                let updated = SystemCatalog.Check.withTable finalTable row

                                match SystemCatalog.Check.tryRead row with
                                | Some check when check.GeneratedName ->
                                    let oldKey = normalizeTableName table
                                    let suffix =
                                        if check.Name.StartsWith(oldKey + "_chk_", System.StringComparison.OrdinalIgnoreCase) then
                                            check.Name.Substring(oldKey.Length)
                                        else
                                            "_chk_1"

                                    updated |> SystemCatalog.Check.withName (finalTable + suffix) |> Ok
                                | _ -> Ok updated)
                        |> Result.map ignore

                    let retargetTriggers =
                        updateRows
                            snapshot
                            "mysql"
                            "triggers"
                            None
                            (fun row ->
                                row
                                |> SystemCatalog.Trigger.tryRead
                                |> Option.exists (fun trigger ->
                                    equal trigger.Schema db && equal trigger.Table (normalizeTableName table))
                                |> Ok)
                            (fun row ->
                                row
                                |> SystemCatalog.Trigger.withTable (normalizeTableName finalTable)
                                |> Ok)
                        |> Result.map ignore

                    retargetChecks |> Result.bind (fun () -> retargetTriggers)

            let validateExistingDefinitions columns =
                storedChecks snapshot db finalTable
                |> traverse (fun check ->
                    match Parser.parseExpression check.Clause with
                    | Result.Error _ -> Error(ExpressionError(3812, sprintf "Check constraint '%s' is invalid." check.Name))
                    | Result.Ok expression ->
                        validateCheckDefinition
                            registry
                            columns
                            { Name = Some check.Name
                              Expression = expression
                              Enforced = check.Enforced
                              Column = check.Column })
                |> Result.map ignore

            let validateRows columns =
                scan snapshot db finalTable
                |> Result.bind (fun (_, rows) ->
                    rows
                    |> List.ofSeq
                    |> traverse (validateCheckRow snapshot registry db finalTable columns)
                    |> Result.map ignore)

            let applyCheckAction columns action =
                match action with
                | AddCheck definition ->
                    storeCheckDefinitions snapshot registry db finalTable columns [ definition ]
                    |> Result.bind (fun () -> if definition.Enforced then validateRows columns else Ok())
                | DropCheck name ->
                    deleteRows snapshot "mysql" "check_constraints" (fun row ->
                        Ok(checkRowSatisfies (fun entry -> equal entry.Schema db && equal entry.Table finalTable && equal entry.Name name) row))
                    |> Result.bind (fun removed ->
                        if removed = 0 && not (originalCheckNames.Contains(name.ToLowerInvariant())) then
                            Error(ExpressionError(1091, sprintf "Can't DROP '%s'; check that column/key exists" name))
                        else
                            Ok())
                | SetCheckEnforced(name, enforced) ->
                    updateRows
                        snapshot
                        "mysql"
                        "check_constraints"
                        None
                        (fun row ->
                            Ok(checkRowSatisfies (fun entry -> equal entry.Schema db && equal entry.Table finalTable && equal entry.Name name) row))
                        (SystemCatalog.Check.withEnforced enforced >> Ok)
                    |> Result.bind (fun changed ->
                        if changed = 0 && not (storedChecks snapshot db finalTable |> List.exists (fun check -> equal check.Name name)) then
                            Error(ExpressionError(1091, sprintf "Check constraint '%s' is not found." name))
                        elif enforced then
                            validateRows columns
                        else
                            Ok())
                | _ -> Ok()

            let altered =
                alterPhysical
                |> Result.bind (fun () -> fillAddedFunctionalDefaults ())
                |> Result.bind (fun () -> retargetAlterObjects ())
                |> Result.bind (fun () -> scan snapshot db finalTable |> Result.map fst)
                |> Result.bind (fun columns ->
                    validateFunctionalDefaultsForStorage registry columns
                    |> Result.bind (fun () ->
                        snapshot.Catalog
                        |> Map.tryFind db
                        |> Option.bind (Map.tryFind (normalizeTableName finalTable))
                        |> Option.map (fun storedTable -> validateIndexExpressions registry columns storedTable.Indexes)
                        |> Option.defaultValue (Error(NoSuchTable finalTable)))
                    |> Result.bind (fun () -> validateExistingDefinitions columns)
                    |> Result.bind (fun () -> actions |> List.fold (fun state action -> state |> Result.bind (fun () -> applyCheckAction columns action)) (Ok()))
                    |> Result.bind (fun () ->
                        snapshot.Catalog
                        |> Map.tryFind db
                        |> Option.bind (Map.tryFind (normalizeTableName finalTable))
                        |> Option.map (fun storedTable -> validateCheckForeignKeys snapshot db finalTable storedTable.ForeignKeys)
                        |> Option.defaultValue (Error(NoSuchTable finalTable))))

            match altered with
            | Ok() ->
                Storage.commitCatalogInto store baseCatalog snapshot
                reportNumericDisplayWarnings addedColumns
                ids, Affected 0UL
            | Error e -> ids, storageErr e

    | RenameTable pairs ->
        // A cross-database `RENAME TABLE a.t TO b.t` only takes the target
        // name's table part. It does not move the table
        // between catalogs, add that once a migration renames across
        // databases rather than within one.
        // Grouped by database and applied per group, so a multi-pair rename
        // within one database is one atomic catalog swap and one WAL event
        // (see `Storage.renameTables`) rather than N independently-replayable
        // ones. Grouping preserves each group's original pair order.
        let groups =
            pairs
            |> List.map (fun (oldName, newName) ->
                let db, oldTable = splitQualified dbName oldName
                let _, newTable = splitQualified dbName newName
                db, (oldTable, newTable))
            |> List.groupBy fst
            |> List.map (fun (db, entries) -> db, entries |> List.map snd)

        let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
        Storage.setStrictMode snapshot store.ExecutionSettings.SqlMode.Strict

        match groups |> traverse (fun (db, dbPairs) -> renameTables snapshot db dbPairs) with
        | Ok _ ->
            let retargetTriggers (db, dbPairs) =
                let renames =
                    dbPairs
                    |> List.map (fun (oldName, newName) -> normalizeTableName oldName, normalizeTableName newName)
                    |> Map.ofList

                updateRows
                    snapshot
                    "mysql"
                    "triggers"
                    None
                    (fun row ->
                        row
                        |> SystemCatalog.Trigger.tryRead
                        |> Option.exists (fun trigger ->
                            System.String.Equals(trigger.Schema, db, System.StringComparison.OrdinalIgnoreCase)
                            && Map.containsKey (normalizeTableName trigger.Table) renames)
                        |> Ok)
                    (fun row ->
                        match SystemCatalog.Trigger.tryRead row with
                        | Some trigger ->
                            row
                            |> SystemCatalog.Trigger.withTable (Map.find (normalizeTableName trigger.Table) renames)
                            |> Ok
                        | None -> Ok row)
                |> Result.map ignore

            let retargetChecks (db, dbPairs) =
                let renames =
                    dbPairs
                    |> List.map (fun (oldName, newName) -> normalizeTableName oldName, newName)
                    |> Map.ofList

                updateRows
                    snapshot
                    "mysql"
                    "check_constraints"
                    None
                    (fun row ->
                        Ok(
                            checkRowSatisfies
                                (fun check ->
                                    System.String.Equals(check.Schema, db, System.StringComparison.OrdinalIgnoreCase)
                                    && Map.containsKey (normalizeTableName check.Table) renames)
                                row
                        ))
                    (fun row ->
                        match SystemCatalog.Check.tryRead row with
                        | Some check ->
                            let oldName = normalizeTableName check.Table
                            let newName = Map.find oldName renames
                            let updated = SystemCatalog.Check.withTable newName row

                            if check.GeneratedName then
                                let suffix =
                                    if check.Name.StartsWith(oldName + "_chk_", System.StringComparison.OrdinalIgnoreCase) then
                                        check.Name.Substring(oldName.Length)
                                    else
                                        "_chk_1"

                                updated |> SystemCatalog.Check.withName (newName + suffix) |> Ok
                            else
                                Ok updated
                        | None -> Ok row)
                |> Result.map ignore

            match groups |> traverse retargetTriggers |> Result.bind (fun _ -> groups |> traverse retargetChecks) with
            | Ok _ ->
                Storage.commitCatalogInto store baseCatalog snapshot
                ids, Affected 0UL
            | Error error -> ids, storageErr error
        | Error e -> ids, storageErr e

    | CreateIndex(name, table, columns, unique, kind, visible) ->
        let db, table = splitQualified dbName table
        let index =
            { Name = name
              KeyColumns = columns
              Unique = unique
              Visible = visible
              Kind = kind }

        match scan store db table with
        | Error error -> ids, storageErr error
        | Ok(columnDefinitions, _) ->
            match validateIndexExpressions registry columnDefinitions [ index ] with
            | Error error -> ids, storageErr error
            | Ok() ->
                match alterTable store db table [ AddIndex index ] with
                | Ok() -> ids, Affected 0UL
                | Error e -> ids, storageErr e

    | DropIndexStmt(name, table, _) ->
        // `ifExists` needs no executor logic (see the AST case's doc):
        // dropping a missing index is already a silent no-op, and a missing
        // table errors here even under `IF EXISTS`, matching MySQL.
        let db, table = splitQualified dbName table

        match alterTable store db table [ DropIndexAction name ] with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | Truncate table ->
        let db, table = splitQualified dbName table

        match truncate store db table with
        | Ok() -> ids, Affected 0UL
        | Error e -> ids, storageErr e

    | CreateView viewSpec ->
        let db, viewName = splitQualified dbName viewSpec.Name
        let existing = tryStoredView store db viewName
        let altering = viewSpec.Action = AlterViewDdl

        let parsedView, viewDefinition, checkOption =
            match Parser.parseViewDefinition viewSpec.Definition with
            | Ok definition -> Ok definition.Statement, definition.Sql, definition.CheckOption
            | Error error -> Error error, viewSpec.Definition, "NONE"

        let duplicateColumns =
            viewSpec.Columns
            |> List.countBy (fun column -> column.ToLowerInvariant())
            |> List.tryFind (fun (_, count) -> count > 1)

        let baseObjectExists =
            store.Catalog
            |> Map.tryFind db
            |> Option.exists (Map.containsKey (normalizeTableName viewName))

        let virtualObjectExists =
            System.String.Equals(db, defaultDatabase, System.StringComparison.OrdinalIgnoreCase)
            && store.VirtualTables.ContainsKey(normalizeTableName viewName)

        let objectError =
            match viewSpec.Action, baseObjectExists || virtualObjectExists, existing with
            | AlterViewDdl, true, _ -> Some(1347, sprintf "'%s.%s' is not VIEW" db viewName)
            | AlterViewDdl, false, None -> Some(1146, sprintf "Table '%s.%s' doesn't exist" db viewName)
            | CreateViewDdl true, true, _ -> Some(1347, sprintf "'%s.%s' is not VIEW" db viewName)
            | CreateViewDdl false, true, _
            | CreateViewDdl false, false, Some _ -> Some(1050, sprintf "Table '%s' already exists" viewName)
            | _ -> None

        let algorithm =
            match viewSpec.Algorithm, existing with
            | Some ViewAlgorithmMerge, _ -> "MERGE"
            | Some ViewAlgorithmTemptable, _ -> "TEMPTABLE"
            | Some ViewAlgorithmUndefined, _ -> "UNDEFINED"
            | None, Some view when altering -> view.Algorithm
            | None, _ -> "UNDEFINED"

        let security =
            match viewSpec.Security, existing with
            | Some ViewInvoker, _ -> "INVOKER"
            | Some ViewDefiner, _ -> "DEFINER"
            | None, Some view when altering -> view.SecurityType
            | None, _ -> "DEFINER"

        let requestedDefiner =
            match viewSpec.Definer with
            | Some CurrentViewDefiner -> Some currentAccount
            | Some(ExplicitViewDefiner(user, host)) -> Some(Auth.account user host)
            | None -> None

        let definer =
            match requestedDefiner with
            | Some account
                when not (Auth.sameAccount account currentAccount)
                     && not (Auth.hasGlobalPrivForAccount store currentAccount "SUPER") ->
                Error(
                    1227,
                    "Access denied; you need (at least one of) the SUPER or SET_ANY_DEFINER privilege(s) for this operation"
                )
            | Some account -> Ok(Auth.formatAccount account)
            | None ->
                match existing with
                | Some view when altering -> Ok view.Definer
                | _ -> Ok(Auth.formatAccount currentAccount)

        let missingDefiner =
            requestedDefiner
            |> Option.filter (fun account -> Auth.tryUserRowForAccount store account |> Option.isNone)

        let candidateView algorithm definer =
            { Name = viewName
              Schema = db
              Definition = viewDefinition
              Columns = viewSpec.Columns
              Definer = definer
              CheckOption = checkOption
              SecurityType = security
              Algorithm = algorithm }

        let supportsMergeAlgorithm = function
            | Select select ->
                select.From.IsSome
                && not select.Distinct
                && not select.CalculateFoundRows
                && select.GroupBy.IsEmpty
                && not select.Rollup
                && select.Windows.IsEmpty
                && select.Having.IsNone
                && select.Limit.IsNone
                && select.Offset.IsNone
                && select.Locking.IsEmpty
                && not (select.Projections |> List.exists (fst >> containsAggregate registry))
                && not (select.OrderBy |> List.exists (fst >> containsAggregate registry))
            | _ -> false

        let supportsCheckOption definer = function
            | Select select ->
                updatableViewOfSelect store (candidateView algorithm definer) select
                |> Option.isSome
            | _ -> false

        match parsedView, Map.containsKey db store.Catalog, duplicateColumns, objectError, definer with
        | Result.Error message, _, _, _, _ -> ids, Err(1064, sprintf "View definition has a syntax error: %s" message)
        | _, false, _, _, _ -> ids, storageErr (NoSuchDatabase db)
        | _, _, Some(column, _), _, _ -> ids, Err(1060, sprintf "Duplicate column name '%s'" column)
        | _, _, _, Some(code, message), _ -> ids, Err(code, message)
        | _, _, _, _, Error(code, message) -> ids, Err(code, message)
        | Result.Ok((Select _ | Union _) as view), true, None, None, Ok definer ->
            let storedColumnNames =
                match viewSpec.Columns with
                | _ :: _ as explicit -> explicit
                | [] ->
                    statementColumns store registry db view
                    |> Option.map (List.map _.Name)
                    |> Option.defaultValue []

            let algorithm =
                if algorithm = "MERGE" && not (supportsMergeAlgorithm view) then
                    Diagnostics.warning 1354 "View merge algorithm can't be used here for now (assumed undefined algorithm)"
                    "UNDEFINED"
                else
                    algorithm

            if viewContainsSessionVariable view then
                ids, Err(1351, "View's SELECT contains a variable or parameter")
            elif checkOption <> "NONE" && not (supportsCheckOption definer view) then
                ids, Err(1368, sprintf "CHECK OPTION on non-updatable view '%s.%s'" db viewName)
            else
                missingDefiner
                |> Option.iter (fun account ->
                    Diagnostics.note 1449 (sprintf "The user specified as a definer ('%s'@'%s') does not exist" account.Name account.Host))

                let removeExisting () =
                    match existing with
                    | None -> Ok 0
                    | Some _ ->
                        deleteRows
                            store
                            "mysql"
                            "views"
                            (fun row ->
                                let text i = toText row.[i] |> Option.defaultValue ""

                                Ok(
                                    System.String.Equals(text 0, viewName, System.StringComparison.OrdinalIgnoreCase)
                                    && System.String.Equals(text 1, db, System.StringComparison.OrdinalIgnoreCase)
                                ))

                match removeExisting () with
                | Error error -> ids, storageErr error
                | Ok _ ->
                    match
                        insertRows
                            store
                            "mysql"
                            "views"
                            (Some
                                [ "view_name"
                                  "view_schema"
                                  "view_definition"
                                  "column_names"
                                  "created"
                                  "definer"
                                  "check_option"
                                  "security_type"
                                  "algorithm" ])
                                [ [ VString viewName
                                    VString db
                                    VString viewDefinition
                                    VString(JsonSerializer.Serialize(storedColumnNames |> List.toArray))
                                    VDateTime System.DateTime.Now
                                    VString definer
                                    VString checkOption
                                    VString security
                                    VString algorithm ] ]
                    with
                    | Ok _ -> ids, Affected 0UL
                    | Error error -> ids, storageErr error
        | Result.Ok _, true, None, None, Ok _ -> ids, Err(1347, sprintf "'%s.%s' is not VIEW" db viewName)

    | DropView(names, ifExists) ->
        let dropOne name =
            let db, viewName = splitQualified dbName name

            deleteRows
                store
                "mysql"
                "views"
                (fun row ->
                    let text i = toText row.[i] |> Option.defaultValue ""

                    Ok(
                        System.String.Equals(text 0, viewName, System.StringComparison.OrdinalIgnoreCase)
                        && System.String.Equals(text 1, db, System.StringComparison.OrdinalIgnoreCase)
                    ))
            |> Result.bind (fun removed ->
                if removed = 0 && not ifExists then
                    Error(NoSuchTable viewName)
                else
                    Ok())

        match names |> traverse dropOne with
        | Ok _ -> ids, Affected 0UL
        | Error error -> ids, storageErr error

    | CreateTrigger(name, timing, event, table, order, body) ->
        let db, table = splitQualified dbName table
        let timingText =
            match timing with
            | Before -> "BEFORE"
            | After -> "AFTER"

        let eventText =
            match event with
            | TriggerInsert -> "INSERT"
            | TriggerUpdate -> "UPDATE"
            | TriggerDelete -> "DELETE"

        match scan store db table with
        | Error e -> ids, storageErr e
        | Ok(columns, _) ->
            match StoredProgram.parseTrigger (SqlMode.parserOptionsFor store.ExecutionSettings.SqlModeText) body with
            | Result.Error msg -> ids, Err(1064, sprintf "Trigger body has a syntax error: %s" msg)
            | Result.Ok bodyStatements ->
                match StoredProgram.validate [] bodyStatements with
                | Error validation ->
                    let code, message = StoredProgram.validationError validation
                    ids, Err(code, message)
                | Ok() ->
                    match bodyStatements |> traverse (validateTriggerStatement registry timing event columns) with
                    | Error result -> ids, result
                    | Ok _ ->
                        match storeTriggerDefinition store currentAccount db table name timingText eventText order body with
                        | Error result -> ids, result
                        | Ok() -> ids, Affected 0UL

    | DropTrigger(name, ifExists) ->
        let equals left right = System.String.Equals(left, right, System.StringComparison.OrdinalIgnoreCase)
        let matchesTrigger (trigger: SystemCatalog.Trigger.Entry) = equals trigger.Name name && equals trigger.Schema dbName
        let matchesRow row = row |> SystemCatalog.Trigger.tryRead |> Option.exists matchesTrigger

        match scan store "mysql" "triggers" with
        | Error error -> ids, storageErr error
        | Ok(_, rows) ->
            match
                rows
                |> Seq.choose (fun row -> SystemCatalog.Trigger.tryRead row |> Option.map (fun trigger -> row, trigger))
                |> Seq.tryFind (snd >> matchesTrigger)
            with
            | None when ifExists -> ids, Affected 0UL
            | None -> ids, Err(1360, "Trigger does not exist")
            | Some(target, trigger) ->
                let targetOrder = trigger.Order
                let sameSlot = sameTriggerSlot dbName trigger.Table trigger.Timing trigger.Event
                let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

                let changed =
                    deleteRows snapshot "mysql" "triggers" (matchesRow >> Ok)
                    |> Result.bind (fun _ ->
                        updateRows
                            snapshot
                            "mysql"
                            "triggers"
                            None
                            (fun row -> Ok(sameSlot row && SystemCatalog.Trigger.actionOrder row > targetOrder))
                            (fun row ->
                                row
                                |> SystemCatalog.Trigger.withActionOrder (SystemCatalog.Trigger.actionOrder row - 1L)
                                |> Ok))

                match changed with
                | Error error -> ids, storageErr error
                | Ok _ ->
                    Storage.commitCatalogInto store baseCatalog snapshot
                    ids, Affected 0UL

    | CreateUser(users, ifNotExists, options) ->
        let createOne (name, host, password) =
            match Auth.createUserWithOptions store name host password options with
            | Error(1396, _) when ifNotExists -> Ok()
            | result -> result

        match users |> traverse createOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | CreateRole(users, ifNotExists) ->
        let createOne (name, host) =
            match Auth.createUser store name host None with
            | Error(1396, _) when ifNotExists -> Ok()
            | Error(1396, _) -> Error(1396, sprintf "Operation CREATE ROLE failed for '%s'@'%s'" name host)
            | Ok() -> Auth.setAccountLocked store name host true
            | error -> error

        match users |> traverse createOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | DropRole(users, ifExists) ->
        let dropOne (name, host) =
            match Auth.dropRole store name host with
            | Error(1396, _) when ifExists -> Ok()
            | Error(1396, _) -> Error(1396, sprintf "Operation DROP ROLE failed for '%s'@'%s'" name host)
            | result -> result

        match users |> traverse dropOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | DropUser(users, ifExists) ->
        let dropOne (name, host) =
            match Auth.dropUser store name host with
            | Error(1396, _) when ifExists -> Ok()
            | r -> r

        match users |> traverse dropOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | RenameUser users ->
        let renameOne ((oldName, oldHost), (newName, newHost)) =
            Auth.renameUser store oldName oldHost newName newHost

        match users |> traverse renameOne with
        | Ok _ -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | AlterUser(name, host, password, ifExists, options) ->
        match Auth.alterUser store name host password options with
        | Ok() -> ids, Affected 0UL
        | Error(1396, _) when ifExists -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | Grant(privs, level, users, withGrantOption) ->
        match Auth.grantSpecifications store privs (Auth.targetOfLevel dbName level) users withGrantOption with
        | Ok() -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | Revoke(privs, level, users) ->
        match Auth.revokeSpecifications store privs (Auth.targetOfLevel dbName level) users with
        | Ok() -> ids, Affected 0UL
        | Error(code, msg) -> ids, Err(code, msg)

    | LoadData load when tryStoredView store (splitQualified dbName load.Table |> fst) (splitQualified dbName load.Table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName load.Table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the INSERT is not insertable-into" viewName)
        | Some view when load.Replace && not view.UpdateJoins.IsEmpty ->
            ids, Err(1395, sprintf "Can not delete from join view '%s.%s'" view.ViewDatabase view.ViewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty && load.Fields.IsEmpty ->
            ids, Err(1394, sprintf "Can not insert into join view '%s.%s' without fields list" view.ViewDatabase view.ViewName)
        | Some view ->
            let fields =
                if load.Fields.IsEmpty then
                    view.OrderedColumns |> List.map LoadColumn
                else
                    load.Fields

            let writeColumns =
                (fields
                 |> List.choose (function
                     | LoadColumn name -> Some name
                     | LoadUserVariable _ -> None))
                @ (load.Assignments |> List.map fst)

            let resolvedTarget =
                if writeColumns.IsEmpty then
                    match Set.toList view.InsertableTargets with
                    | [ targetKey ] ->
                        match view.Targets |> Map.values |> Seq.tryFind (viewTargetKey >> (=) targetKey) with
                        | Some target -> Ok target
                        | None -> Error(Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" view.ViewName))
                    | _ -> Error(Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" view.ViewName))
                else
                    resolveViewInsertTarget view writeColumns |> Result.map fst

            let rewriteField (target: ViewColumnTarget) = function
                | LoadUserVariable variable -> Ok(LoadUserVariable variable)
                | LoadColumn column ->
                    match Map.tryFind (column.ToLowerInvariant()) view.Targets with
                    | Some fieldTarget when sameViewTarget fieldTarget target ->
                        Ok(LoadColumn fieldTarget.Column)
                    | Some _ ->
                        Error(Err(1393, sprintf "Can not modify more than one base table through a join view '%s.%s'" view.ViewDatabase view.ViewName))
                    | None when view.OrderedColumns |> List.exists (fun name -> name.Equals(column, System.StringComparison.OrdinalIgnoreCase)) ->
                        Error(Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" view.ViewName))
                    | None -> Error(Err(1054, sprintf "Unknown column '%s' in field list" column))

            match resolvedTarget with
            | Error error -> ids, error
            | Ok target ->
                match
                    validateViewAssignmentTarget view target load.Assignments,
                    rewriteViewAssignments view load.Assignments,
                    fields |> traverse (rewriteField target)
                with
                | Error error, _, _
                | _, Error error, _
                | _, _, Error error -> ids, error
                | Ok(), Ok assignments, Ok baseFields ->
                    let rewritten =
                        LoadData
                            { load with
                                Table = target.Database + "." + target.Table
                                Fields = baseFields
                                Assignments = assignments }

                    executeViewWrite view target rewritten

    | LoadData load ->
        let db, table = splitQualified dbName load.Table

        match scan store db table with
        | Error error -> ids, storageErr error
        | Ok(tableColumns, _) ->
            let fields =
                if load.Fields.IsEmpty then
                    tableColumns |> List.map (fun column -> LoadColumn column.Name)
                else
                    load.Fields

            let inputColumns =
                fields
                |> List.choose (function
                    | LoadColumn name -> Some name
                    | LoadUserVariable _ -> None)

            let splitRow (row: Value list) =
                if row.Length <> fields.Length then
                    Error(ColumnCountMismatch(fields.Length, row.Length))
                else
                    List.zip fields row
                    |> List.fold
                        (fun (values, variables) -> function
                            | LoadColumn _, value -> value :: values, variables
                            | LoadUserVariable variable, value -> values, (variable, value) :: variables)
                        ([], [])
                    |> fun (values, variables) -> Ok(List.rev values, List.rev variables)

            match load.Rows |> traverse splitRow with
            | Error error -> ids, storageErr error
            | Ok preparedRows ->
                let rowsValues, rowVariables = preparedRows |> List.unzip
                let rowVariables = List.toArray rowVariables

                match
                    load.Assignments
                    |> traverse (fun (name, expression) ->
                        resolveAssignableColumn tableColumns table name
                        |> Result.map (fun index -> index, expression))
                with
                | Error error -> ids, storageErr error
                | Ok indexedAssignments ->
                    let assignedIndices = indexedAssignments |> List.map fst |> Set.ofList
                    let columnIndex = columnIndexOf tableColumns
                    let qualifiers = singleQualifier table tableColumns

                    let prepareFor
                        (targetStore: Store)
                        (currentColumns: ColumnDef list)
                        (ordinal: int)
                        (omitted: Set<int>)
                        (candidate: Value[])
                        =
                        let bindVariables () =
                            match currentVariableContext () with
                            | None -> Error(ExpressionError(1105, "LOAD DATA requires a session variable context"))
                            | Some bindings ->
                                rowVariables.[ordinal]
                                |> List.fold
                                    (fun state (variable, value) ->
                                        state
                                        |> Result.bind (fun variables ->
                                            match UserVariableRef.validationError variable with
                                            | Some message -> Error(ExpressionError(3061, message))
                                            | None when Map.containsKey variable.Name variables || variables.Count < bindings.MaxUserVariables ->
                                                Ok(Map.add variable.Name value variables)
                                            | None -> Error(ExpressionError(1105, "Too many user-defined variables"))))
                                    (Ok bindings.UserVariables.Value)
                                |> Result.map (fun variables -> bindings.UserVariables.Value <- variables)

                        bindVariables ()
                        |> Result.bind (fun () ->
                            let context = contextFactory targetStore registry dbName columnIndex qualifiers None candidate

                            indexedAssignments
                            |> List.fold
                                (fun state (index, expression) ->
                                    state
                                    |> Result.bind (fun () ->
                                        evalExpr context expression
                                        |> Result.mapError ExpressionError
                                        |> Result.bind (fun value ->
                                            let column = currentColumns.[index]

                                            if column.AutoIncrement && value = VNull then
                                                Ok VNull
                                            else
                                                coerceStoredColumnValue targetStore column value)
                                        |> Result.map (fun value -> candidate.[index] <- value)))
                                (Ok()))
                        |> Result.bind (fun () ->
                            prepareInsertRow
                                targetStore
                                db
                                table
                                currentColumns
                                (Set.difference omitted assignedIndices)
                                candidate)

                    let cols = if load.Fields.IsEmpty then None else Some inputColumns
                    if load.Replace then
                        replaceEvaluatedWith db table cols rowsValues assignedIndices prepareFor
                    else
                        finishInsert db table (fun targetStore ->
                            match scan targetStore db table with
                            | Error error -> Error error
                            | Ok(currentColumns, _) ->
                                let prepare = prepareFor targetStore currentColumns

                                if load.Ignore then
                                    insertRowsIgnorePreparedWithOrdinal targetStore db table cols rowsValues assignedIndices prepare
                                else
                                    insertRowsPreparedWithOrdinal targetStore db table cols rowsValues assignedIndices prepare)

    | Insert(table, columns, rowsExprs, onDuplicateUpdate, ignoreDuplicates) when tryStoredView store (splitQualified dbName table |> fst) (splitQualified dbName table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the INSERT is not insertable-into" viewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty && columns.IsEmpty ->
            ids, Err(1394, sprintf "Can not insert into join view '%s.%s' without fields list" view.ViewDatabase view.ViewName)
        | Some view ->
            let viewColumns = if columns.IsEmpty then view.OrderedColumns else columns
            match resolveViewInsertTarget view viewColumns with
            | Error error -> ids, error
            | Ok(target, baseColumns) ->
                match validateViewAssignmentTarget view target onDuplicateUpdate, rewriteViewAssignments view onDuplicateUpdate with
                | Error error, _
                | _, Error error -> ids, error
                | Ok(), Ok assignments ->
                    let rewritten = Insert(target.Database + "." + target.Table, baseColumns, rowsExprs, assignments, ignoreDuplicates)

                    executeViewWrite view target rewritten

    | Insert(table, columns, rowsExprs, onDuplicateUpdate, ignoreDuplicates) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error error -> ids, storageErr error
        | Ok(tableColumns, _) ->
            match evaluateInsertRows store db table tableColumns columns rowsExprs with
            | Error error -> ids, storageErr error
            | Ok rowsValues ->
                let cols = if columns.IsEmpty then None else Some columns

                if onDuplicateUpdate.IsEmpty then
                    finishInsert db table (fun s ->
                        match scan s db table with
                        | Error error -> Error error
                        | Ok(tableColumns, _) ->
                            let prepare = prepareInsertRow s db table tableColumns

                            if ignoreDuplicates then
                                insertRowsIgnorePrepared s db table cols rowsValues prepare
                            else
                                insertRowsPrepared s db table cols rowsValues prepare)
                else
                    upsertEvaluated db table cols rowsValues (Array.create rowsValues.Length []) onDuplicateUpdate

    | InsertSelect(table, columns, select, onDuplicateUpdate, ignoreDuplicates) when tryStoredView store (splitQualified dbName table |> fst) (splitQualified dbName table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the INSERT is not insertable-into" viewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the INSERT is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty && columns.IsEmpty ->
            ids, Err(1394, sprintf "Can not insert into join view '%s.%s' without fields list" view.ViewDatabase view.ViewName)
        | Some view ->
            let viewColumns = if columns.IsEmpty then view.OrderedColumns else columns
            match resolveViewInsertTarget view viewColumns with
            | Error error -> ids, error
            | Ok(target, baseColumns) ->
                match validateViewAssignmentTarget view target onDuplicateUpdate, rewriteViewAssignments view onDuplicateUpdate with
                | Error error, _
                | _, Error error -> ids, error
                | Ok(), Ok assignments ->
                    let rewritten = InsertSelect(target.Database + "." + target.Table, baseColumns, select, assignments, ignoreDuplicates)

                    executeViewWrite view target rewritten

    | InsertSelect(table, columns, select, onDuplicateUpdate, ignoreDuplicates) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error error -> ids, storageErr error
        | Ok(targetColumns, _) ->
            match prepareInsertSelectSourceBindings store registry dbName targetColumns select onDuplicateUpdate with
            | Error(code, message) -> ids, Err(code, message)
            | Ok(boundSelect, sourceReferences) ->
                let selectResult, _, typedRows = runSelectStmt store registry dbName boundSelect None

                match selectResult with
                | Err(code, message) -> ids, Err(code, message)
                | Affected _ -> ids, Err(1064, "INSERT ... SELECT source did not return a resultset")
                | MultipleResults _ -> ids, nestedResultsError "an INSERT ... SELECT source"
                | ResultSet _ ->
                    let hiddenCount = sourceReferences.Length

                    let rowsValues, sourceBindings =
                        typedRows
                        |> List.map (fun row ->
                            let visibleCount = row.Length - hiddenCount
                            let values = row |> Array.take visibleCount |> Array.toList

                            let bindings =
                                sourceReferences
                                |> List.mapi (fun index (reference, column) -> reference, column, row.[visibleCount + index])

                            values, bindings)
                        |> List.unzip
                        |> fun (values, bindings) -> values, List.toArray bindings

                    let cols = if columns.IsEmpty then None else Some columns

                    if onDuplicateUpdate.IsEmpty then
                        finishInsert db table (fun s ->
                            match scan s db table with
                            | Error error -> Error error
                            | Ok(currentColumns, _) ->
                                let prepare = prepareInsertRow s db table currentColumns

                                if ignoreDuplicates then
                                    insertRowsIgnorePrepared s db table cols rowsValues prepare
                                else
                                    insertRowsPrepared s db table cols rowsValues prepare)
                    else
                        upsertEvaluated db table cols rowsValues sourceBindings onDuplicateUpdate

    | Replace(table, columns, rowsExprs) when tryStoredView store (splitQualified dbName table |> fst) (splitQualified dbName table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the REPLACE is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty ->
            ids, Err(1395, sprintf "Can not delete from join view '%s.%s'" view.ViewDatabase view.ViewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the REPLACE is not insertable-into" viewName)
        | Some view ->
            let viewColumns = if columns.IsEmpty then view.OrderedColumns else columns

            match resolveViewInsertTarget view viewColumns with
            | Error error -> ids, error
            | Ok(target, baseColumns) ->
                let rewritten = Replace(target.Database + "." + target.Table, baseColumns, rowsExprs)

                executeViewWrite view target rewritten

    | Replace(table, columns, rowsExprs) ->
        let db, table = splitQualified dbName table
        let literalContext = contextFactory store registry dbName Map.empty Map.empty None [||]

        match rowsExprs |> traverse (traverse (evalExpr literalContext)) with
        | Error(code, message) -> ids, Err(code, message)
        | Ok rowsValues ->
            let cols = if columns.IsEmpty then None else Some columns
            replaceEvaluated db table cols rowsValues

    | ReplaceSelect(table, columns, select) when tryStoredView store (splitQualified dbName table |> fst) (splitQualified dbName table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the REPLACE is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty ->
            ids, Err(1395, sprintf "Can not delete from join view '%s.%s'" view.ViewDatabase view.ViewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the REPLACE is not insertable-into" viewName)
        | Some view ->
            let viewColumns = if columns.IsEmpty then view.OrderedColumns else columns

            match resolveViewInsertTarget view viewColumns with
            | Error error -> ids, error
            | Ok(target, baseColumns) ->
                let rewritten = ReplaceSelect(target.Database + "." + target.Table, baseColumns, select)

                executeViewWrite view target rewritten

    | ReplaceSelect(table, columns, select) ->
        let db, table = splitQualified dbName table
        let selectResult, _, _ = runSelectStmt store registry dbName select None

        match selectResult with
        | Err(code, message) -> ids, Err(code, message)
        | Affected _ -> ids, Err(1064, "REPLACE ... SELECT source did not return a resultset")
        | MultipleResults _ -> ids, nestedResultsError "a REPLACE ... SELECT source"
        | ResultSet(_, rows) ->
            let rowsValues = rows |> List.map (List.map (function Some value -> VString value | None -> VNull))
            let cols = if columns.IsEmpty then None else Some columns
            replaceEvaluated db table cols rowsValues

    | ReplaceSet(table, assignments) when tryStoredView store (splitQualified dbName table |> fst) (splitQualified dbName table |> snd) |> Option.isSome ->
        let viewDb, viewName = splitQualified dbName table

        match tryUpdatableView store viewDb viewName with
        | None -> ids, Err(1471, sprintf "The target table '%s' of the REPLACE is not insertable-into" viewName)
        | Some view when not view.UpdateJoins.IsEmpty ->
            ids, Err(1395, sprintf "Can not delete from join view '%s.%s'" view.ViewDatabase view.ViewName)
        | Some view when not view.Insertable -> ids, Err(1471, sprintf "The target table %s of the REPLACE is not insertable-into" viewName)
        | Some view ->
            match resolveViewInsertTarget view (assignments |> List.map fst), rewriteViewAssignments view assignments with
            | Error error, _
            | _, Error error -> ids, error
            | Ok(target, _), Ok rewrittenAssignments ->
                let rewritten = ReplaceSet(target.Database + "." + target.Table, rewrittenAssignments)

                executeViewWrite view target rewritten

    | ReplaceSet(table, assignments) ->
        let db, table = splitQualified dbName table

        match scan store db table with
        | Error error -> ids, storageErr error
        | Ok(tableColumns, _) ->
            let columnIndex = columnIndexOf tableColumns
            let defaults = tableColumns |> List.map (evalDefaultWithMode (temporalCoercionMode store)) |> Array.ofList
            let functional =
                tableColumns
                |> List.indexed
                |> List.choose (fun (index, column) -> match column.Default with Some(DExpression _) -> Some index | _ -> None)
                |> Set.ofList

            let substituteDefault =
                rewriteExprWith (function
                    | FuncCall(name, [ Col column ]) when System.String.Equals(name, "DEFAULT", System.StringComparison.OrdinalIgnoreCase) ->
                        Some(Col column)
                    | FuncCall(name, [ QualifiedCol(_, column) ]) when System.String.Equals(name, "DEFAULT", System.StringComparison.OrdinalIgnoreCase) ->
                        Some(Col column)
                    | _ -> None)

            match evaluateFunctionalDefaults store db table tableColumns functional defaults with
            | Error error -> ids, storageErr error
            | Ok defaults ->
                let context = contextFactory store registry dbName columnIndex (singleQualifier table tableColumns) None defaults

                match assignments |> traverse (fun (name, value) -> evalExpr context (substituteDefault value) |> Result.map (fun result -> name, result)) with
                | Error(code, message) -> ids, Err(code, message)
                | Ok values -> replaceEvaluated db table (Some(values |> List.map fst)) [ values |> List.map snd ]

    | Do expressions ->
        let context = contextFactory store registry dbName Map.empty Map.empty None [||]

        match expressions |> traverse (evalExpr context) with
        | Ok _ -> ids, Affected 0UL
        | Error(code, message) -> ids, Err(code, message)

    | Select select ->
        let result, _, _ = runSelectStmt store registry dbName select None
        ids, result

    | Union(first, rest, orderBy, limit, offset) ->
        let result, _, _ = runUnionStmtWithOuter store registry dbName first rest orderBy limit offset None
        ids, result

    | Update updateStmt when updateStmt.Joins.IsEmpty && tryStoredView store (updateStmt.From.Database |> Option.defaultValue dbName) updateStmt.From.Table |> Option.isSome ->
        let viewDb = updateStmt.From.Database |> Option.defaultValue dbName

        match tryUpdatableView store viewDb updateStmt.From.Table with
        | None -> ids, Err(1288, sprintf "The target table '%s' of the UPDATE is not updatable" updateStmt.From.Table)
        | Some view when not view.UpdateJoins.IsEmpty && not updateStmt.OrderBy.IsEmpty ->
            ids, Err(1221, "Incorrect usage of UPDATE and ORDER BY")
        | Some view when not view.UpdateJoins.IsEmpty && updateStmt.Limit.IsSome ->
            ids, Err(1221, "Incorrect usage of UPDATE and LIMIT")
        | Some view ->
            let rewrite = rewriteViewExpression view
            match
                updateStmt.Assignments
                |> traverse (fun assignment ->
                    match Map.tryFind (assignment.Column.ToLowerInvariant()) view.Targets with
                    | Some target -> Ok(assignment, target)
                    | None when view.OrderedColumns |> List.exists (fun name -> name.Equals(assignment.Column, System.StringComparison.OrdinalIgnoreCase)) ->
                        Error(1348, sprintf "Column '%s' is not updatable" assignment.Column)
                    | None -> Error(1054, sprintf "Unknown column '%s' in field list" assignment.Column))
            with
            | Error(code, message) -> ids, Err(code, message)
            | Ok assignments ->
                let targets =
                    assignments
                    |> List.map (fun (_, target) -> target.Database.ToLowerInvariant(), target.Table.ToLowerInvariant(), target.Qualifier.ToLowerInvariant())
                    |> List.distinct

                if targets.Length > 1 then
                    ids, Err(1393, sprintf "Can not modify more than one base table through a join view '%s.%s'" view.ViewDatabase view.ViewName)
                else
                    let rewritten =
                        { updateStmt with
                            From = view.UpdateFrom
                            Joins = view.UpdateJoins
                            Assignments =
                                assignments
                                |> List.map (fun (assignment, target) ->
                                    { assignment with
                                        Table = if view.UpdateJoins.IsEmpty then None else Some target.Qualifier
                                        Column = target.Column
                                        Value = rewrite assignment.Value })
                            Where = combineViewPredicate view.Predicate (updateStmt.Where |> Option.map rewrite)
                            OrderBy = updateStmt.OrderBy |> List.map (fun (expression, direction) -> rewrite expression, direction)
                            Limit = updateStmt.Limit |> Option.map rewrite }

                    let target = assignments |> List.head |> snd
                    executeViewWrite view target (Update rewritten)

    | Update updateStmt when updateStmt.Joins.IsEmpty ->
        let db, table = (updateStmt.From.Database |> Option.defaultValue dbName), updateStmt.From.Table
        let tableAlias = updateStmt.From.Alias |> Option.defaultValue updateStmt.From.Table

        let tableRoot = tableSnapshot store db table |> Result.toOption
        let fullTextPlanResult =
            match tableRoot, updateStmt.Where with
            | Some table, Some predicate -> fullTextPredicatePlan table predicate
            | _ -> Ok None

        let fullTextPlan = fullTextPlanResult |> Result.defaultValue None

        // Candidate narrowing is a superset; mutation target selection still
        // evaluates the complete WHERE. Stable RowIds also bound the rewrite.
        let narrowed =
            fullTextPlan
            |> Option.bind (fun plan -> tableRoot |> Option.map (fun table -> table.Columns, plan.Rows))
            |> Option.orElseWith (fun () -> tryIndexedLookup store dbName updateStmt.From updateStmt.Where)
            |> Option.orElseWith (fun () -> tryRangeLookup store dbName updateStmt.From updateStmt.Where)

        let scanned =
            narrowed
            |> Option.map (fun (cols, rows) -> Ok(cols, rows |> List.map snd |> Seq.ofList))
            |> Option.defaultWith (fun () -> scan store db table)

        match fullTextPlanResult, scanned with
        | Error(code, message), _ -> ids, Err(code, message)
        | Ok _, Error e -> ids, storageErr e
        | Ok _, Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            match updateStmt.Assignments |> traverse (fun a -> resolveAssignableColumn columns table a.Column |> Result.map (fun i -> i, a.Value)) with
            | Error e -> ids, storageErr e
            | Ok indexedAssignments ->
                let qualifiers = singleQualifier tableAlias columns

                let ctxFor = contextFactory store registry dbName columnIndex qualifiers None

                let fullTextRowIds = Dictionary<Value[], RowId>(HashIdentity.Reference)

                fullTextPlan
                |> Option.iter (fun plan ->
                    for rowId, row in plan.Rows do
                        fullTextRowIds.[row] <- rowId)

                let check row =
                    match fullTextPlan with
                    | Some plan ->
                        match fullTextRowIds.TryGetValue row with
                        | true, rowId -> whereMatches ctxFor (Some(plan.PredicateFor rowId)) row
                        | false, _ -> Ok false
                    | None -> whereMatches ctxFor updateStmt.Where row

                let probePredicate =
                    fullTextPlan
                    |> Option.map _.ProbePredicate
                    |> Option.orElse updateStmt.Where

                let checkAssignments row =
                    indexedAssignments |> traverse (fun (_, expr) -> evalExpr (ctxFor row) expr)

                // Type-check WHERE/SET against a synthetic all-NULL row
                // first — same reasoning as `runSelect`'s `probeRow`: an
                // unknown column/function is a schema error, not a data
                // one, and shouldn't depend on whether any row happens to
                // match (or exist at all).
                match
                    withMetadataProbe (fun () ->
                        whereMatches ctxFor probePredicate (probeRow columns)
                        |> Result.bind (fun _ -> checkAssignments (probeRow columns)))
                with
                | Error(code, message) -> ids, Err(code, message)
                | Ok _ ->
                    match selectMutationTargets ctxFor (List.ofSeq rows) check updateStmt.OrderBy (Option.map rowCount updateStmt.Limit) with
                    | Error(code, message) -> ids, Err(code, message)
                    | Ok targetRows ->
                        let targetSet = referenceSet targetRows
                        let beforeTriggers = triggersFor store db table "BEFORE" "UPDATE"
                        let afterTriggers = triggersFor store db table "AFTER" "UPDATE"
                        let useSnapshot = not (beforeTriggers.IsEmpty && afterTriggers.IsEmpty)
                        let baseCatalog, targetStore =
                            if useSnapshot then Storage.beginTransactionSnapshotWithBase store else store.Catalog, store

                        let changedRows = ResizeArray<Value[] option * Value[] option>()

                        let predicate row =
                            match fullTextPlan, narrowed with
                            | Some _, _ -> Ok(targetSet.Contains row)
                            | None, Some _ ->
                                check row
                                |> Result.mapError ExpressionError
                                |> Result.map (fun matches -> matches && targetSet.Contains row)
                            | None, None -> Ok(targetSet.Contains row)
                        let assignedIdxs = indexedAssignments |> List.map fst |> Set.ofList

                        let updater row =
                            let updated =
                                applyAssignments store registry dbName columnIndex qualifiers indexedAssignments row
                                |> Result.map (applyOnUpdateTimestamps (temporalCoercionMode targetStore) columns assignedIdxs row)
                                |> Result.bind (computeGeneratedRow targetStore registry db table columns)

                            match updated with
                            | Error(ExpressionError(3819, message)) when updateStmt.Ignore ->
                                Diagnostics.warning 3819 message
                                Ok row
                            | Ok candidate ->
                                match fireTriggers targetStore db table Before TriggerUpdate beforeTriggers [ Some row, Some candidate ] with
                                | Some(Err(code, message)) -> Error(ExpressionError(code, message))
                                | _ ->
                                    match validateViewCandidate targetStore db table columns candidate with
                                    | Error(ExpressionError(1369, message)) when updateStmt.Ignore ->
                                        Diagnostics.warning 1369 message
                                        Ok row
                                    | Error error -> Error error
                                    | Ok candidate ->
                                        changedRows.Add(Some(Array.copy row), Some candidate)
                                        Ok candidate
                            | Error error -> Error error

                        let candidates = narrowed |> Option.map snd

                        match updateRows targetStore db table candidates predicate updater with
                        | Ok changed ->
                            match fireTriggers targetStore db table After TriggerUpdate afterTriggers (List.ofSeq changedRows) with
                            | Some error -> ids, error
                            | None ->
                                if useSnapshot then
                                    Storage.commitCatalogInto store baseCatalog targetStore

                                ids, Affected(uint64 (if foundRows then targetRows.Length else changed))
                        | Error e -> ids, storageErr e

    | Update updateStmt ->
        // Multi-table `UPDATE t1 JOIN t2 ON ... SET ...` — resolves the
        // whole join, then for each matched combined row, assigns to
        // whichever source table each `SET` target names, claiming a
        // physical row (by reference) the first time a matched row touches
        // it so a row reached through more than one join match is still
        // updated at most once (see `Ast.UpdateStmt`'s doc). Runs the writes
        // against a private snapshot store, merged back via
        // `Storage.commitCatalogInto` (see its doc), so disjoint row changes
        // can combine while overlapping changes fail with a retryable conflict.
        (
            match runMutationJoin store registry dbName updateStmt.From updateStmt.Joins with
            | Error e -> ids, e
            | Ok(sources, joinedRows) ->
                let sourceIndex = sources |> List.mapi (fun i source -> source.Qualifier.ToLowerInvariant(), i) |> Map.ofList
                let combinedColumns = sources |> List.map (fun source -> source.Qualifier, source.Columns)
                let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumns |> List.collect snd)) (qualifierRanges combinedColumns) None

                // Byte offset of each source's columns within the flat
                // combined row `ctxFor` expects — lets the left-to-right
                // fold below patch an assignment's new value straight into
                // a working copy of that row for the next assignment to see.
                let sourceOffsets =
                    combinedColumns |> List.fold (fun (offset, acc) (_, cols) -> offset + List.length cols, acc @ [ offset ]) (0, []) |> snd |> Array.ofList

                let resolveAssignment (a: Assignment) : Result<(int * int * Expr), EvalError> =
                    match a.Table with
                    | Some q ->
                        match Map.tryFind (q.ToLowerInvariant()) sourceIndex with
                        | None -> Error(unknownColumn (sprintf "%s.%s" q a.Column))
                        | Some srcIdx ->
                            let cols = sources.[srcIdx].Columns

                            resolveColumn cols a.Column
                            |> Result.mapError (fun _ -> unknownColumn (sprintf "%s.%s" q a.Column))
                            |> Result.map (fun colIdx -> srcIdx, colIdx, a.Value)
                    | None ->
                        match
                            sources
                            |> List.indexed
                            |> List.choose (fun (i, source) -> resolveColumn source.Columns a.Column |> function Ok idx -> Some(i, idx) | Error _ -> None)
                        with
                        | [ (srcIdx, colIdx) ] -> Ok(srcIdx, colIdx, a.Value)
                        | [] -> Error(unknownColumn a.Column)
                        | _ -> Error(1052, sprintf "Column '%s' in field list is ambiguous" a.Column)

                // A generated column can't be a SET target (MySQL 3105).
                let guardAssignable ((srcIdx, colIdx, v): int * int * Expr) : Result<int * int * Expr, EvalError> =
                    let source = sources.[srcIdx]
                    let col = List.item colIdx source.Columns

                    match source.PhysicalTable with
                    | None -> Error(1288, sprintf "The target table '%s' of the UPDATE is not updatable" source.Qualifier)
                    | Some tableRef when col.Generated.IsSome -> Error(toMySqlError (GeneratedColumnAssignment(col.Name, tableRef.Table)))
                    | Some _ -> Ok(srcIdx, colIdx, v)

                match updateStmt.Assignments |> traverse (resolveAssignment >> Result.bind guardAssignable) with
                | Error(code, message) -> ids, Err(code, message)
                | Ok resolvedAssignments ->
                    let check = whereMatches ctxFor updateStmt.Where

                    // Two aliases resolving to the same physical table (a
                    // self-join) must share one claim/pending bucket, keyed
                    // by the physical table rather than by source index —
                    // otherwise the same row reached through both aliases
                    // gets written by two separate `Storage.updateRows`
                    // passes below, and the first pass's row-array
                    // replacement (`updateRows` returns *new* `Value[]`s for
                    // every row it changes) breaks the second pass's
                    // by-reference match, silently dropping its write.
                    let physicalKey (tableRef: TableRef) : string * string =
                        (tableRef.Database |> Option.defaultValue dbName), Storage.normalizeTableName tableRef.Table

                    let physicalGroups =
                        sources
                        |> List.choose _.PhysicalTable
                        |> List.distinctBy physicalKey
                        |> Array.ofList

                    let sourcePhys =
                        sources
                        |> List.map (fun source -> source.PhysicalTable |> Option.map (fun tableRef -> physicalGroups |> Array.findIndex (fun t -> physicalKey t = physicalKey tableRef)))
                        |> Array.ofList

                    // Aliases claim rows independently, while pending changes
                    // share one batch per physical table. This preserves
                    // self-join roles without publishing competing row arrays.
                    let claims = sources |> List.map (fun _ -> System.Collections.Generic.HashSet<Value[]>(HashIdentity.Reference)) |> Array.ofList
                    let pending = physicalGroups |> Array.map (fun _ -> System.Collections.Generic.Dictionary<Value[], (int * Value) list>(HashIdentity.Reference))

                    let processRow ((identities, flat): Value[] option list * Value[]) : Result<unit, EvalError> =
                        check flat
                        |> Result.bind (fun isMatch ->
                            if not isMatch then
                                Ok()
                            else
                                // Left-to-right: each assignment's RHS is
                                // evaluated against `working`, patched with
                                // every earlier assignment in this same
                                // statement — matching MySQL's documented
                                // `SET a = x, b = a` evaluation order, now
                                // across tables too.
                                let working = Array.copy flat

                                // A claim spans the complete SET list so later
                                // assignments to the same alias are retained.
                                let claimedThisRow =
                                    identities
                                    |> List.mapi (fun srcIdx identity ->
                                        match identity with
                                        | None -> false
                                        | Some physRow ->
                                            if claims.[srcIdx].Contains physRow then
                                                false
                                            else
                                                claims.[srcIdx].Add physRow |> ignore
                                                true)
                                    |> Array.ofList

                                let rec go assignments =
                                    match assignments with
                                    | [] -> Ok()
                                    | (srcIdx, colIdx, expr) :: rest ->
                                        evalExpr (ctxFor working) expr
                                        |> Result.bind (fun v ->
                                            working.[sourceOffsets.[srcIdx] + colIdx] <- v

                                            match List.item srcIdx identities with
                                            | None -> ()
                                            | Some physRow ->
                                                match sourcePhys.[srcIdx] with
                                                | Some physIdx when claimedThisRow.[srcIdx] ->
                                                    let existing = match pending.[physIdx].TryGetValue physRow with true, vs -> vs | false, _ -> []
                                                    pending.[physIdx].[physRow] <- existing @ [ colIdx, v ]
                                                | _ -> ()

                                            go rest)

                                go resolvedAssignments)

                    match joinedRows |> traverse processRow with
                    | Error(code, message) -> ids, Err(code, message)
                    | Ok _ ->
                        // MySQL rolls back every target when any target or
                        // trigger fails, so publication uses one catalog merge.
                        let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store

                        let physicalColumns =
                            physicalGroups
                            |> Array.mapi (fun i _ ->
                                sources
                                |> List.find (fun source -> source.PhysicalTable |> Option.exists (fun tableRef -> physicalKey tableRef = physicalKey physicalGroups.[i]))
                                |> _.Columns)

                        let assignedIdxsByPhys =
                            physicalGroups
                            |> Array.mapi (fun i _ ->
                                resolvedAssignments
                                |> List.choose (fun (srcIdx, colIdx, _) -> if sourcePhys.[srcIdx] = Some i then Some colIdx else None)
                                |> Set.ofList)

                        let invocationTables = physicalGroups |> Array.map physicalKey |> Set.ofArray

                        let apply =
                            withTriggerInvocationTables invocationTables (fun () ->
                                physicalGroups
                                |> Array.mapi (fun i tableRef ->
                                    if pending.[i].Count = 0 then
                                        Ok 0
                                    else
                                        let tdb, tname = (tableRef.Database |> Option.defaultValue dbName), tableRef.Table
                                        let beforeTriggers = triggersFor snapshot tdb tname "BEFORE" "UPDATE"
                                        let afterTriggers = triggersFor snapshot tdb tname "AFTER" "UPDATE"
                                        let changedRows = ResizeArray<Value[] option * Value[] option>()
                                        let predicate row = Ok(pending.[i].ContainsKey row)

                                        let updater row =
                                            let updated =
                                                match pending.[i].TryGetValue row with
                                                | true, vals ->
                                                    let newRow = Array.copy row

                                                    for colIdx, v in vals do
                                                        newRow.[colIdx] <- v

                                                    Ok(
                                                        applyOnUpdateTimestamps
                                                            (temporalCoercionMode snapshot)
                                                            physicalColumns.[i]
                                                            assignedIdxsByPhys.[i]
                                                            row
                                                            newRow
                                                    )
                                                    |> Result.bind (computeGeneratedRow snapshot registry tdb tname physicalColumns.[i])
                                                | false, _ -> Ok row

                                            match updated with
                                            | Error(ExpressionError(3819, message)) when updateStmt.Ignore ->
                                                Diagnostics.warning 3819 message
                                                Ok row
                                            | Error(ExpressionError(1369, message)) when updateStmt.Ignore ->
                                                Diagnostics.warning 1369 message
                                                Ok row
                                            | Error error -> Error error
                                            | Ok candidate ->
                                                triggerStorageResult (
                                                    fireTriggers
                                                        snapshot
                                                        tdb
                                                        tname
                                                        Before
                                                        TriggerUpdate
                                                        beforeTriggers
                                                        [ Some row, Some candidate ]
                                                )
                                                |> Result.bind (fun () ->
                                                    match validateViewCandidate snapshot tdb tname physicalColumns.[i] candidate with
                                                    | Error(ExpressionError(1369, message)) when updateStmt.Ignore ->
                                                        Diagnostics.warning 1369 message
                                                        Ok row
                                                    | Error error -> Error error
                                                    | Ok candidate ->
                                                        changedRows.Add(Some(Array.copy row), Some candidate)
                                                        Ok candidate)

                                        match updateRows snapshot tdb tname None predicate updater with
                                        | Error error -> Error error
                                        | Ok changed ->
                                            triggerStorageResult (
                                                fireTriggers
                                                    snapshot
                                                    tdb
                                                    tname
                                                    After
                                                    TriggerUpdate
                                                    afterTriggers
                                                    (List.ofSeq changedRows)
                                            )
                                            |> Result.map (fun () -> changed))
                                |> Array.toList
                                |> traverse id)

                        match apply with
                        | Ok counts ->
                            Storage.commitCatalogInto store baseCatalog snapshot
                            // CLIENT_FOUND_ROWS reports claimed rows; the
                            // default reports rows changed after BEFORE triggers.
                            let matched = pending |> Array.sumBy (fun d -> d.Count)
                            ids, Affected(uint64 (if foundRows then matched else List.sum counts))
                        | Error e -> ids, storageErr e)

    | Delete deleteStmt when deleteStmt.Joins.IsEmpty && tryStoredView store (deleteStmt.From.Database |> Option.defaultValue dbName) deleteStmt.From.Table |> Option.isSome ->
        let viewDb = deleteStmt.From.Database |> Option.defaultValue dbName

        match tryUpdatableView store viewDb deleteStmt.From.Table with
        | None -> ids, Err(1288, sprintf "The target table '%s' of the DELETE is not updatable" deleteStmt.From.Table)
        | Some view when not view.UpdateJoins.IsEmpty ->
            ids, Err(1395, sprintf "Can not delete from join view '%s.%s'" view.ViewDatabase view.ViewName)
        | Some view ->
            let rewrite = rewriteViewExpression view
            let rewritten =
                { deleteStmt with
                    From = view.UpdateFrom
                    Targets = [ view.UpdateFrom.Alias |> Option.defaultValue view.Table ]
                    Where = combineViewPredicate view.Predicate (deleteStmt.Where |> Option.map rewrite)
                    OrderBy = deleteStmt.OrderBy |> List.map (fun (expression, direction) -> rewrite expression, direction)
                    Limit = deleteStmt.Limit |> Option.map rewrite }

            match registryForViewSecurity store registry view.SecurityType view.Definer view.Database (Delete rewritten) with
            | Error(code, message) -> ids, Err(code, message)
            | Ok _ -> executeAs store registry dbName ids foundRows currentAccount (Delete rewritten)

    | Delete deleteStmt when deleteStmt.Joins.IsEmpty ->
        let db, table = (deleteStmt.From.Database |> Option.defaultValue dbName), deleteStmt.From.Table
        let tableAlias = deleteStmt.From.Alias |> Option.defaultValue deleteStmt.From.Table
        let beforeTriggers = triggersFor store db table "BEFORE" "DELETE"
        let afterTriggers = triggersFor store db table "AFTER" "DELETE"
        let useSnapshot = not (beforeTriggers.IsEmpty && afterTriggers.IsEmpty)
        let baseCatalog, targetStore =
            if useSnapshot then Storage.beginTransactionSnapshotWithBase store else store.Catalog, store
        let tableRoot = tableSnapshot targetStore db table |> Result.toOption
        let fullTextPlanResult =
            match tableRoot, deleteStmt.Where with
            | Some table, Some predicate -> fullTextPredicatePlan table predicate
            | _ -> Ok None

        let fullTextPlan = fullTextPlanResult |> Result.defaultValue None

        let narrowed =
            fullTextPlan
            |> Option.bind (fun plan -> tableRoot |> Option.map (fun table -> table.Columns, plan.Rows))
            |> Option.orElseWith (fun () -> tryIndexedLookup targetStore dbName deleteStmt.From deleteStmt.Where)
            |> Option.orElseWith (fun () -> tryRangeLookup targetStore dbName deleteStmt.From deleteStmt.Where)

        let scanned =
            narrowed
            |> Option.map (fun (columns, rows) -> Ok(columns, rows |> List.map snd |> Seq.ofList))
            |> Option.defaultWith (fun () -> scan targetStore db table)

        match fullTextPlanResult, scanned with
        | Error(code, message), _ -> ids, Err(code, message)
        | Ok _, Error e -> ids, storageErr e
        | Ok _, Ok(columns, rows) ->
            let columnIndex = columnIndexOf columns

            let ctxFor = contextFactory store registry dbName columnIndex (singleQualifier tableAlias columns) None

            let fullTextRowIds = Dictionary<Value[], RowId>(HashIdentity.Reference)

            fullTextPlan
            |> Option.iter (fun plan ->
                for rowId, row in plan.Rows do
                    fullTextRowIds.[row] <- rowId)

            let check row =
                match fullTextPlan with
                | Some plan ->
                    match fullTextRowIds.TryGetValue row with
                    | true, rowId -> whereMatches ctxFor (Some(plan.PredicateFor rowId)) row
                    | false, _ -> Ok false
                | None -> whereMatches ctxFor deleteStmt.Where row

            let probePredicate =
                fullTextPlan
                |> Option.map _.ProbePredicate
                |> Option.orElse deleteStmt.Where

            match withMetadataProbe (fun () -> whereMatches ctxFor probePredicate (probeRow columns)) with
            | Error(code, message) -> ids, Err(code, message)
            | Ok _ ->
                match selectMutationTargets ctxFor (List.ofSeq rows) check deleteStmt.OrderBy (Option.map rowCount deleteStmt.Limit) with
                | Error(code, message) -> ids, Err(code, message)
                | Ok targetRows ->
                    let targetSet = referenceSet targetRows
                    let deletedRows = targetRows |> List.map (fun row -> Some row, None)

                    let predicate row =
                        match fullTextPlan, narrowed with
                        | Some _, _ -> Ok(targetSet.Contains row)
                        | None, Some _ ->
                            check row
                            |> Result.mapError ExpressionError
                            |> Result.map (fun matches -> matches && targetSet.Contains row)
                        | None, None -> Ok(targetSet.Contains row)

                    let candidates = narrowed |> Option.map snd

                    match fireTriggers targetStore db table Before TriggerDelete beforeTriggers deletedRows with
                    | Some error -> ids, error
                    | None ->
                        match
                            candidates
                            |> Option.map (fun rows -> deleteRowsCandidates targetStore db table rows predicate)
                            |> Option.defaultWith (fun () -> deleteRows targetStore db table predicate)
                        with
                        | Error e -> ids, storageErr e
                        | Ok affected ->
                            match fireTriggers targetStore db table After TriggerDelete afterTriggers deletedRows with
                            | Some error -> ids, error
                            | None ->
                                if useSnapshot then
                                    Storage.commitCatalogInto store baseCatalog targetStore

                                ids, Affected(uint64 affected)

    | Delete deleteStmt ->
        match runMutationJoin store registry dbName deleteStmt.From deleteStmt.Joins with
        | Error e -> ids, e
        | Ok(sources, joinedRows) ->
            let sourceIndex = sources |> List.mapi (fun i source -> source.Qualifier.ToLowerInvariant(), i) |> Map.ofList
            let combinedColumns = sources |> List.map (fun source -> source.Qualifier, source.Columns)
            let ctxFor = contextFactory store registry dbName (columnIndexOf (combinedColumns |> List.collect snd)) (qualifierRanges combinedColumns) None

            match
                deleteStmt.Targets
                |> traverse (fun t ->
                    match Map.tryFind (t.ToLowerInvariant()) sourceIndex with
                    | Some i when sources.[i].PhysicalTable.IsSome -> Ok i
                    | Some _ -> Error(1288, sprintf "The target table '%s' of the DELETE is not updatable" t)
                    | None -> Error(1109, sprintf "Unknown table '%s' in MULTI DELETE" t))
            with
            | Error(code, message) -> ids, Err(code, message)
            | Ok targetIndices ->
                let check = whereMatches ctxFor deleteStmt.Where
                let targetIndices = List.distinct targetIndices

                let physicalKey (tableRef: TableRef) =
                    tableRef.Database |> Option.defaultValue dbName, normalizeTableName tableRef.Table

                let targetTables =
                    targetIndices
                    |> List.choose (fun index -> sources.[index].PhysicalTable |> Option.map (fun tableRef -> index, tableRef))

                let physicalGroups = targetTables |> List.map snd |> List.distinctBy physicalKey |> Array.ofList

                let physicalIndex =
                    targetTables
                    |> List.map (fun (sourceIndex, tableRef) ->
                        sourceIndex, physicalGroups |> Array.findIndex (fun candidate -> physicalKey candidate = physicalKey tableRef))
                    |> Map.ofList

                let claimed = physicalGroups |> Array.map (fun _ -> HashSet<Value[]>(HashIdentity.Reference))
                let claimedRows = physicalGroups |> Array.map (fun _ -> ResizeArray<Value[]>())

                let processRow ((identities, flat): Value[] option list * Value[]) : Result<unit, EvalError> =
                    check flat
                    |> Result.map (fun isMatch ->
                        if isMatch then
                            for i in targetIndices do
                                match List.item i identities with
                                | Some physRow ->
                                    let group = physicalIndex.[i]

                                    if claimed.[group].Add physRow then
                                        claimedRows.[group].Add physRow
                                | None -> ())

                match joinedRows |> traverse processRow with
                | Error(code, message) -> ids, Err(code, message)
                | Ok _ ->
                    let baseCatalog, snapshot = Storage.beginTransactionSnapshotWithBase store
                    let invocationTables =
                        sources
                        |> List.choose _.PhysicalTable
                        |> List.map physicalKey
                        |> Set.ofList

                    let apply =
                        withTriggerInvocationTables invocationTables (fun () ->
                            physicalGroups
                            |> Array.mapi (fun index tableRef ->
                                if claimedRows.[index].Count = 0 then
                                    Ok 0
                                else
                                    let tdb, tname = tableRef.Database |> Option.defaultValue dbName, tableRef.Table
                                    let deletedRows = claimedRows.[index] |> Seq.map (fun row -> Some row, None) |> List.ofSeq
                                    let beforeTriggers = triggersFor snapshot tdb tname "BEFORE" "DELETE"
                                    let afterTriggers = triggersFor snapshot tdb tname "AFTER" "DELETE"

                                    triggerStorageResult (fireTriggers snapshot tdb tname Before TriggerDelete beforeTriggers deletedRows)
                                    |> Result.bind (fun () ->
                                        match deleteRows snapshot tdb tname (fun row -> Ok(claimed.[index].Contains row)) with
                                        | Error error -> Error error
                                        | Ok count ->
                                            triggerStorageResult (fireTriggers snapshot tdb tname After TriggerDelete afterTriggers deletedRows)
                                            |> Result.map (fun () -> count)))
                            |> Array.toList
                            |> traverse id)

                    match apply with
                    | Ok counts ->
                        Storage.commitCatalogInto store baseCatalog snapshot
                        ids, Affected(uint64 (List.sum counts))
                    | Error e -> ids, storageErr e

    | GrantRoles(roles, users, withAdminOption) ->
        match Auth.grantRoles store roles users withAdminOption with
        | Ok() -> ids, Affected 0UL
        | Error(code, message) -> ids, Err(code, message)
    | RevokeRoles(roles, users) ->
        match Auth.revokeRoles store roles users with
        | Ok() -> ids, Affected 0UL
        | Error(code, message) -> ids, Err(code, message)
    | SetRole _
    | SetDefaultRole _ -> ids, Err(1235, "Role statement execution requires a session")
    | ChecksumTables(tables, quick) ->
        ids, checksumTables store dbName tables quick
    | Explain(format, inner) ->
        ids, explainStatement format store registry dbName inner

let transactionWriteTargets (store: Store) (dbName: string) (statement: Statement) : (string * string * WriteLockTargets) option =
    let targets (tableRef: TableRef) predicate =
        let database = tableRef.Database |> Option.defaultValue dbName

        tryIndexedLookup store dbName tableRef predicate
        |> Option.orElseWith (fun () -> tryRangeLookup store dbName tableRef predicate)
        |> Option.map (fun (_, rows) ->
            database,
            tableRef.Table,
            { RowIds = rows |> List.map fst
              Keys = [] })

    match statement with
    | Update update when update.Ctes.IsEmpty && update.Joins.IsEmpty -> targets update.From update.Where
    | Delete delete when delete.Ctes.IsEmpty && delete.Joins.IsEmpty -> targets delete.From delete.Where
    | Insert(tableName, columns, rows, _, _) ->
        let values =
            rows
            |> traverse (traverse (function
                | Lit value -> Ok value
                | _ -> Error()))
            |> Result.toOption

        values
        |> Option.bind (fun rows ->
            let database, table = splitQualified dbName tableName
            let columns = if columns.IsEmpty then None else Some columns

            tryInsertLockTargets store database table columns rows
            |> Option.map (fun targets -> database, table, targets))
    | _ -> None

let execute (store: Store) (registry: Registry) (dbName: string) (ids: int64 * int64) (foundRows: bool) (stmt: Statement) =
    let root = Auth.account "root" "%"
    executeAs store (registryForDefiner root registry) dbName ids foundRows root stmt
