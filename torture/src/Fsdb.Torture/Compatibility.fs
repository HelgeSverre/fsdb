namespace Fsdb.Torture

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading.Tasks
open MySqlConnector

type ContractProtocol =
    | TextProtocol
    | PreparedProtocol

type ContractOperation =
    | Execute
    | Query

type OracleExpectation =
    | OracleSuccess
    | OracleValueSuccessIgnoringLabels
    | OracleError of code: int * sqlState: string

type ContractAction =
    | Run of operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Send of pending: string * operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Reap of pending: string
    | PrepareHandle of handle: string * operation: ContractOperation * sql: string * parameters: obj array
    | InvokeHandle of handle: string * parameters: obj array option
    | CloseHandle of handle: string
    | AwaitPending of pending: string

type ContractStep =
    { Name: string
      Connection: string
      Action: ContractAction
      Expectation: OracleExpectation }

type ContractCase =
    { Name: string
      Setup: string array
      Steps: ContractStep array
      Cleanup: string array
      Coverage: (string * string array) array }

[<CLIMutable>]
type ContractTargetOutcome =
    { Target: string
      Status: string
      AffectedRows: int
      Columns: string array
      ColumnTypes: string array
      Rows: string array
      DataSha256: string
      ErrorCode: int
      SqlState: string
      Message: string
      ElapsedMs: int64 }

[<CLIMutable>]
type ContractStepRecord =
    { Name: string
      Connection: string
      Action: string
      Sql: string
      MySql: ContractTargetOutcome
      Fsdb: ContractTargetOutcome
      Classification: string
      Detail: string
      Passed: bool }

[<CLIMutable>]
type ContractCaseRecord =
    { Name: string
      Steps: ContractStepRecord array
      MySqlStateRestored: bool
      FsdbStateRestored: bool
      StateDetail: string
      Passed: bool }

[<CLIMutable>]
type CompatibilityManifest =
    { SchemaVersion: int
      RunId: string
      StartedUtc: string
      FinishedUtc: string
      FsdbRevision: string
      FsdbDirty: bool
      FsdbAssemblySha256: string
      MySqlVersion: string
      Cases: ContractCaseRecord array
      Classification: string
      FailureSignature: string
      Passed: bool }

[<CLIMutable>]
type CompatibilityOptions =
    { TimeoutSeconds: int
      ArtifactRoot: string
      MySqlConnection: string }

[<RequireQualifiedAccess>]
module Contract =
    let execute name sql : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Execute, TextProtocol, sql, [||])
          Expectation = OracleSuccess }

    let query name sql : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Query, TextProtocol, sql, [||])
          Expectation = OracleSuccess }

    let preparedQuery name sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Query, PreparedProtocol, sql, parameters)
          Expectation = OracleSuccess }

    let preparedExecute name sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = Run(Execute, PreparedProtocol, sql, parameters)
          Expectation = OracleSuccess }

    let fails code sqlState (step: ContractStep) : ContractStep =
        { step with Expectation = OracleError(code, sqlState) }

    let comparingValues (step: ContractStep) : ContractStep =
        { step with Expectation = OracleValueSuccessIgnoringLabels }

    let on connection (step: ContractStep) : ContractStep = { step with Connection = connection }

    let send pending (step: ContractStep) : ContractStep =
        match step.Action with
        | Run(operation, protocol, sql, parameters) -> { step with Action = Send(pending, operation, protocol, sql, parameters) }
        | Send _
        | Reap _
        | PrepareHandle _
        | InvokeHandle _
        | CloseHandle _
        | AwaitPending _ -> invalidArg (nameof step) "only an ordinary step can be sent"

    let reap name connection pending expectation : ContractStep =
        { Name = name
          Connection = connection
          Action = Reap pending
          Expectation = expectation }

    let prepare name handle operation sql parameters : ContractStep =
        { Name = name
          Connection = "main"
          Action = PrepareHandle(handle, operation, sql, parameters)
          Expectation = OracleSuccess }

    let invoke name handle expectation : ContractStep =
        { Name = name
          Connection = "main"
          Action = InvokeHandle(handle, None)
          Expectation = expectation }

    let invokeWith name handle parameters : ContractStep =
        { invoke name handle OracleSuccess with Action = InvokeHandle(handle, Some parameters) }

    let close name handle : ContractStep =
        { Name = name
          Connection = "main"
          Action = CloseHandle handle
          Expectation = OracleSuccess }

    let awaitPending name pending : ContractStep =
        { Name = name
          Connection = "main"
          Action = AwaitPending pending
          Expectation = OracleSuccess }

[<RequireQualifiedAccess>]
module ContractCatalog =
    type private FunctionContractSpec =
        { Name: string
          Functions: string array
          TextExpression: string
          PreparedExpression: string
          Parameters: obj array
          ResultTypes: string array
          NullPrepared: (string array * string * obj array) option
          WrongArityExpressions: (string * string) array }

    let private functionProbe (name: string) (names: string array) sql =
        [| Contract.query (name + "-text") sql |> Contract.comparingValues
           Contract.preparedQuery (name + "-prepared") sql [||] |> Contract.comparingValues |],
        (names
         |> Array.map (fun functionName ->
             "function:" + functionName.ToLowerInvariant(),
             [| "parser"; "text-differential"; "prepared-protocol" |]))

    let private functionError name (functionName: string) code sqlState sql =
        Contract.query name sql |> Contract.fails code sqlState,
        ("function:" + functionName.ToLowerInvariant(), [| "error-contract" |])

    let private generatedFunctionContracts specs =
        let steps =
            specs
            |> Array.collect (fun spec ->
                [| yield Contract.query (spec.Name + "-text") ("SELECT " + spec.TextExpression) |> Contract.comparingValues
                   yield
                       Contract.preparedQuery
                           (spec.Name + "-prepared")
                           ("SELECT " + spec.PreparedExpression)
                           spec.Parameters
                       |> Contract.comparingValues

                   match spec.NullPrepared with
                   | Some(_, expression, parameters) ->
                       yield
                           Contract.preparedQuery (spec.Name + "-null") ("SELECT " + expression) parameters
                           |> Contract.comparingValues
                   | None -> ()

                   for functionName, expression in spec.WrongArityExpressions do
                       let errorName = spec.Name + "-" + functionName.ToLowerInvariant() + "-error"
                       yield Contract.query (errorName + "-text") ("SELECT " + expression) |> Contract.fails 1582 "42000"
                       yield
                           Contract.preparedQuery (errorName + "-prepared") ("SELECT " + expression) [||]
                           |> Contract.fails 1582 "42000" |])

        let coverage =
            specs
            |> Array.collect (fun spec ->
                let functionsWithErrors = spec.WrongArityExpressions |> Array.map fst |> Set.ofArray
                let functionsWithNulls =
                    spec.NullPrepared
                    |> Option.map (fun (names, _, _) -> Set.ofArray names)
                    |> Option.defaultValue Set.empty
                let functionsWithResultTypes = Set.ofArray spec.ResultTypes

                spec.Functions
                |> Array.map (fun name ->
                    let axes =
                        [| yield "parser"
                           yield "text-differential"
                           yield "prepared-protocol"
                           if functionsWithResultTypes.Contains name then
                               yield "result-type"

                           if functionsWithNulls.Contains name then
                               yield "null-semantics"

                           if functionsWithErrors.Contains name then
                               yield "error-contract" |]

                    "function:" + name.ToLowerInvariant(), axes))

        steps, coverage

    let private comments =
        { Name = "comments-and-precedence"
          Setup = [||]
          Steps =
            [| Contract.query "double-dash-without-space" "SELECT 1--1 AS value"
               Contract.query "ordinary-block-comment" "SELECT 1 /* between operands */ + 2 AS value"
               Contract.query "double-dash-with-space" "SELECT 1-- comment\n AS value"
               Contract.query "hash-comment" "SELECT 1 # comment\n + 2 AS value"
               Contract.query "executable-version-comment" "SELECT /*!80000 1 + */ 2 AS value"
               Contract.query "future-version-comment" "SELECT /*!99999 1 + */ 2 AS value"
               Contract.query
                   "comments-through-cte"
                   "WITH /* after WITH */ cte AS (SELECT 1 AS n) SELECT /* projection */ n FROM cte" |]
          Cleanup = [||]
          Coverage =
            [| "statement:select", [| "parser"; "text-differential" |]
               "syntax:comments", [| "parser"; "text-differential" |] |] }

    let private orderAliases =
        let queries =
            [| "scalar", "SELECT v AS a FROM contract_order_alias ORDER BY ABS(a)"
               "source-precedence", "SELECT -v AS v FROM contract_order_alias ORDER BY v+0"
               "bare-precedence", "SELECT -v AS v FROM contract_order_alias ORDER BY v"
               "empty", "SELECT v AS a FROM contract_order_alias WHERE FALSE ORDER BY ABS(a)"
               "grouped", "SELECT v AS a FROM contract_order_alias GROUP BY v ORDER BY ABS(a)"
               "aggregate", "SELECT SUM(v) AS s FROM contract_order_alias ORDER BY ABS(s)"
               "window", "SELECT ROW_NUMBER() OVER (ORDER BY v) AS r FROM contract_order_alias ORDER BY ABS(r)"
               "source-label", "SELECT v+1 FROM contract_order_alias ORDER BY ABS(`v+1`)"
               "limit", "SELECT v AS a FROM contract_order_alias ORDER BY ABS(a) LIMIT 1"
               "descending", "SELECT v AS a FROM contract_order_alias ORDER BY ABS(a) DESC" |]
        { Name = "order-alias-expressions"
          Setup =
            [| "DROP TABLE IF EXISTS contract_order_alias"
               "CREATE TABLE contract_order_alias(v INT)"
               "INSERT INTO contract_order_alias VALUES(2),(1)" |]
          Steps =
            [| for name, sql in queries do
                   yield Contract.query name sql
                   yield Contract.preparedQuery (name + "-prepared") sql [||]
               yield Contract.query "nested-aggregate" "SELECT SUM(v) AS s FROM contract_order_alias ORDER BY SUM(s)" |> Contract.fails 1111 "HY000"
               yield Contract.preparedQuery "nested-aggregate-prepared" "SELECT SUM(v) AS s FROM contract_order_alias ORDER BY SUM(s)" [||] |> Contract.fails 1111 "HY000"
               for expression in [ "SUM(@n:=@n+1)"; "SUM(v)+(@n:=@n+1)" ] do
                   yield Contract.execute ("reset-group-" + expression) "SET @n=0"
                   yield Contract.query ("group-alias-" + expression) ("SELECT " + expression + " AS s FROM contract_order_alias GROUP BY v ORDER BY ABS(s)")
                   yield Contract.query ("group-count-" + expression) "SELECT @n"
               yield Contract.query "group-alias-charset" "SELECT _latin1'a' AS s FROM contract_order_alias GROUP BY v ORDER BY s COLLATE utf8mb4_bin" |> Contract.fails 1253 "42000"
               yield Contract.query "group-nested-aggregate-metadata" "SELECT SUM(v) AS s FROM contract_order_alias GROUP BY v ORDER BY COERCIBILITY(SUM(s))" |> Contract.fails 1111 "HY000"
               for order in [ "n"; "n+0" ] do
                   yield Contract.execute ("reset-" + order) "SET @n=0"
                   yield Contract.query ("assignment-" + order) ("SELECT (@n:=@n+1) AS n FROM contract_order_alias ORDER BY " + order)
                   yield Contract.query ("assignment-count-" + order) "SELECT @n" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_order_alias"; "SET @n=NULL" |]
          Coverage = [| "statement:select", [| "ordering"; "aliases"; "text-differential"; "prepared-differential" |] |] }

    let private duplicateOrderAliases =
        let queries =
            [| "SELECT missing,v AS a,w AS a FROM contract_duplicate_values ORDER BY a", OracleError(1054, "42S22")
               "SELECT v AS a,w AS a FROM contract_duplicate_values WHERE missing ORDER BY a", OracleError(1054, "42S22")
               "SELECT v AS a,ROW_NUMBER() OVER (ORDER BY v DESC) AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT SUM(-v) AS a,ROW_NUMBER() OVER (ORDER BY v) AS a FROM contract_duplicate_values GROUP BY v ORDER BY a", OracleSuccess
               "SELECT SUM(-v) AS a,ROW_NUMBER() OVER (ORDER BY v) AS a FROM contract_duplicate_values GROUP BY v ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,w AS a,-v AS a FROM contract_duplicate_values ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,-v AS a,w AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT -v AS a,v AS a,w AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT -v AS a,v AS a,w AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,v AS a,w AS a FROM contract_duplicate_values ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,v AS a,w AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleError(1052, "23000")
               "SELECT v AS a,contract_duplicate_values.v AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,contract_duplicate_values.v AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT contract_duplicate_values.v AS a,v AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT contract_duplicate_values.v AS a,v AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,(v) AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,(v) AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,+v AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,+v AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,CAST(v AS SIGNED) AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,CAST(v AS SIGNED) AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT v AS a,w+0 AS a,-v AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,w+0 AS a,-v AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT *,-v AS v FROM contract_duplicate_values ORDER BY v", OracleSuccess
               "SELECT *,-v AS v FROM contract_duplicate_values ORDER BY ABS(v+3)", OracleSuccess
               "SELECT *,w AS v FROM contract_duplicate_values ORDER BY v", OracleError(1052, "23000")
               "SELECT *,w AS v FROM contract_duplicate_values ORDER BY ABS(v+3)", OracleSuccess
               "SELECT v AS a,v+0 AS a,w AS a FROM contract_duplicate_values ORDER BY a", OracleSuccess
               "SELECT v AS a,v+0 AS a,w AS a FROM contract_duplicate_values ORDER BY ABS(a+3)", OracleSuccess
               "SELECT l.v AS a,r.v AS a FROM contract_duplicate_values l JOIN contract_duplicate_values r ON l.v=r.v ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a FROM contract_duplicate_values WHERE FALSE ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,v AS a FROM contract_duplicate_values GROUP BY v ORDER BY ABS(a)", OracleSuccess
               "SELECT SUM(v) AS a,SUM(w) AS a FROM contract_duplicate_values GROUP BY v,w ORDER BY ABS(a)", OracleSuccess
               "SELECT v AS a,w AS a FROM (SELECT v,w FROM contract_duplicate_values) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM (SELECT v,w FROM contract_duplicate_values) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM (SELECT v,w FROM contract_duplicate_values) t ORDER BY a", OracleSuccess
               "SELECT v AS a,w AS a FROM (SELECT v,w FROM contract_duplicate_values LIMIT 10) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM (SELECT v,w FROM contract_duplicate_values LIMIT 10) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM (SELECT v,w FROM contract_duplicate_values LIMIT 10) t ORDER BY a", OracleSuccess
               "SELECT v AS a,w AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM (SELECT 1 AS v,2 AS w) t ORDER BY a", OracleSuccess
               "SELECT v AS a,w AS a FROM contract_duplicate_view ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM contract_duplicate_view ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM contract_duplicate_view ORDER BY a", OracleSuccess
               "SELECT v AS a,w AS a FROM contract_duplicate_constants ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,w AS a,-v AS a FROM contract_duplicate_constants ORDER BY a", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a,w AS a FROM contract_duplicate_constants ORDER BY a", OracleSuccess
               "SELECT v AS a,+w AS a,-v AS a FROM contract_duplicate_values ORDER BY a", OracleError(1052, "23000")
               "SELECT x AS a,y AS a FROM contract_duplicate_same ORDER BY a", OracleError(1052, "23000")
               "SELECT x AS a,y AS a FROM (SELECT v AS x,v AS y FROM contract_duplicate_values) t ORDER BY a", OracleError(1052, "23000")
               "SELECT x AS a,y AS a FROM (SELECT v AS x,v AS y FROM contract_duplicate_values LIMIT 10) t ORDER BY a", OracleError(1052, "23000") |]
        { Name = "duplicate-ordering-aliases"
          Setup =
            [| "DROP VIEW IF EXISTS contract_duplicate_same"
               "DROP VIEW IF EXISTS contract_duplicate_constants"
               "DROP VIEW IF EXISTS contract_duplicate_view"
               "DROP TABLE IF EXISTS contract_duplicate_values"
               "CREATE TABLE contract_duplicate_values(v INT,w INT)"
               "INSERT INTO contract_duplicate_values VALUES(2,10),(1,20)"
               "CREATE VIEW contract_duplicate_view AS SELECT v,w FROM contract_duplicate_values"
               "CREATE VIEW contract_duplicate_constants AS SELECT 1 AS v,2 AS w"
               "CREATE VIEW contract_duplicate_same AS SELECT v AS x,v AS y FROM contract_duplicate_values" |]
          Steps =
            [| for sql, expectation in queries do
                   yield { Contract.query sql sql with Expectation = expectation }
                   yield { Contract.preparedQuery ("prepared: " + sql) sql [||] with Expectation = expectation } |]
          Cleanup =
            [| "DROP VIEW IF EXISTS contract_duplicate_same"
               "DROP VIEW IF EXISTS contract_duplicate_constants"
               "DROP VIEW IF EXISTS contract_duplicate_view"
               "DROP TABLE IF EXISTS contract_duplicate_values" |]
          Coverage = [| "statement:select", [| "ordering"; "aliases"; "name-binding"; "text-differential"; "prepared-differential" |] |] }

    let private correlatedOrderAliases =
        let queries =
            [| "SELECT -v AS a FROM contract_ordering_scope ORDER BY -(SELECT a)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY ABS((SELECT a)+3)", OracleSuccess
               "SELECT _latin1'a' AS a FROM contract_ordering_scope ORDER BY (SELECT a COLLATE utf8mb4_bin)", OracleError(1253, "42000")
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT a)", OracleSuccess
               "SELECT -v AS v FROM contract_ordering_scope ORDER BY (SELECT v)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT a+0)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT (SELECT a))", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS a) t)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT a FROM (SELECT 3 AS x) t)", OracleSuccess
               "SELECT v AS a,w AS a FROM contract_ordering_scope ORDER BY (SELECT a)", OracleError(1052, "23000")
               "SELECT v AS a,-v AS a FROM contract_ordering_scope ORDER BY (SELECT a)", OracleSuccess
               "SELECT SUM(-v) AS a FROM contract_ordering_scope GROUP BY v ORDER BY (SELECT a)", OracleError(1247, "42S22")
               "SELECT ROW_NUMBER() OVER (ORDER BY v DESC) AS a FROM contract_ordering_scope ORDER BY (SELECT a)", OracleError(3594, "HY000")
               "SELECT -v AS a FROM contract_ordering_scope WHERE FALSE ORDER BY (SELECT a)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY EXISTS(SELECT a)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT 1 WHERE a=-1)", OracleSuccess
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT contract_ordering_scope.a)", OracleError(1054, "42S22")
               "SELECT -v AS a FROM contract_ordering_scope ORDER BY (SELECT a FROM contract_ordering_scope inner_scope LIMIT 1)", OracleSuccess |]
        { Name = "correlated-ordering-aliases"
          Setup =
            [| "DROP TABLE IF EXISTS contract_ordering_scope"
               "CREATE TABLE contract_ordering_scope(v INT,w INT)"
               "INSERT INTO contract_ordering_scope VALUES(2,10),(1,20)" |]
          Steps =
            [| for sql, expectation in queries do
                   yield { Contract.query sql sql with Expectation = expectation }
                   yield { Contract.preparedQuery ("prepared: " + sql) sql [||] with Expectation = expectation }
               for expression in [ "(SELECT a)"; "(SELECT a+0)" ] do
                   let sql = "SELECT (@n := @n + 1) AS a FROM contract_ordering_scope ORDER BY " + expression
                   yield Contract.query "reset assignment" "SET @n=0"
                   yield Contract.query sql sql
                   yield Contract.query "assignment count" "SELECT @n"
                   yield Contract.query "reset prepared assignment" "SET @n=0"
                   yield Contract.execute "prepare assignment" ("PREPARE correlated_assignment FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query ("prepared: " + sql) "EXECUTE correlated_assignment"
                   yield Contract.query "prepared assignment count" "SELECT @n"
                   yield Contract.execute "deallocate assignment" "DEALLOCATE PREPARE correlated_assignment" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_ordering_scope" |]
          Coverage = [| "statement:select", [| "ordering"; "aliases"; "subqueries"; "text-differential"; "prepared-differential" |] |] }

    let private aggregateOwnership =
        let queries =
            [| "SELECT (SELECT SUM(v)) AS total,ROW_NUMBER() OVER () AS rn FROM contract_aggregate_owner", OracleSuccess
               "SELECT v,(SELECT SUM(v)) AS total,ROW_NUMBER() OVER (ORDER BY v) AS rn FROM contract_aggregate_owner GROUP BY v ORDER BY v", OracleSuccess
               "SELECT ANY_VALUE((SELECT v)) AS value,(SELECT SUM(v)) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT ANY_VALUE(v)) AS value,(SELECT SUM(v)) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT SUM(v)) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT SUM(v)) AS total FROM contract_aggregate_owner WHERE FALSE", OracleSuccess
               "SELECT (SELECT SUM(v) WHERE FALSE) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT SUM(v) FROM contract_aggregate_owner i) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT v,(SELECT SUM(v)) AS total FROM contract_aggregate_owner GROUP BY v ORDER BY v", OracleSuccess
               "SELECT (SELECT (SELECT SUM(v))) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT COUNT(v)) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT v) AS value,(SELECT SUM(v)) AS total FROM contract_aggregate_owner", OracleError(1140, "42000")
               "SELECT (SELECT SUM(x)) AS total FROM (SELECT v AS x FROM contract_aggregate_owner) d", OracleSuccess
               "SELECT 3 IN (SELECT SUM(v)) AS hit FROM contract_aggregate_owner", OracleSuccess
               "SELECT 3=ANY(SELECT SUM(v)) AS hit FROM contract_aggregate_owner", OracleSuccess
               "SELECT EXISTS(SELECT SUM(v)) AS hit FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT SUM(contract_aggregate_owner.v) FROM contract_aggregate_owner i LIMIT 1) AS total FROM contract_aggregate_owner", OracleSuccess
               "SELECT (SELECT SUM(contract_aggregate_owner.v) FROM contract_aggregate_owner i) AS total FROM contract_aggregate_owner", OracleError(1242, "21000") |]
        { Name = "correlated-aggregate-ownership"
          Setup =
            [| "DROP TABLE IF EXISTS contract_aggregate_owner"
               "CREATE TABLE contract_aggregate_owner(v INT)"
               "INSERT INTO contract_aggregate_owner VALUES(2),(1)" |]
          Steps =
            [| for sql, expectation in queries do
                   yield { Contract.query sql sql with Expectation = expectation }
                   yield { Contract.preparedQuery ("prepared: " + sql) sql [||] with Expectation = expectation } |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_aggregate_owner" |]
          Coverage = [| "statement:select", [| "aggregation"; "subqueries"; "text-differential"; "prepared-differential" |] |] }

    let private aggregateOrdering =
        let queries =
            [| "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(i.v) FROM contract_aggregate_ordering i)", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering ORDER BY missing,SUM(v)", OracleError(1054, "42S22")
               "SELECT v FROM contract_aggregate_ordering ORDER BY SUM(v),missing", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY ABS(missing),SUM(v)", OracleError(1054, "42S22")
               "SELECT (SELECT missing) FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(1054, "42S22")
               "SELECT v FROM contract_aggregate_ordering HAVING missing ORDER BY SUM(v)", OracleError(1054, "42S22")
               "SELECT v AS a FROM contract_aggregate_ordering ORDER BY a,SUM(v)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(1))", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(v+1))", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(i.v+contract_aggregate_ordering.v) FROM contract_aggregate_ordering i)", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(contract_aggregate_ordering.v) FROM contract_aggregate_ordering i LIMIT 1)", OracleError(3029, "HY000")
               "SELECT SUM(v) AS s FROM contract_aggregate_ordering ORDER BY (SELECT SUM(v))", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM((SELECT v)))", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT v AS a FROM contract_aggregate_ordering ORDER BY SUM(a)", OracleError(3029, "HY000")
               "SELECT 1 FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT 1 ORDER BY SUM(1)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY v,SUM(v)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY SUM(missing)", OracleError(1054, "42S22")
               "SELECT missing FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(1054, "42S22")
               "SELECT v FROM contract_aggregate_ordering WHERE missing ORDER BY SUM(v)", OracleError(1054, "42S22")
               "SELECT v FROM contract_aggregate_ordering WHERE FALSE ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY SUM(v) LIMIT 0", OracleError(3029, "HY000")
               "SELECT SUM(v) FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering GROUP BY v ORDER BY SUM(v)", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering HAVING COUNT(*)>0 ORDER BY SUM(v)", OracleError(1140, "42000")
               "SELECT 1 FROM contract_aggregate_ordering HAVING COUNT(*)>0 ORDER BY SUM(v)", OracleSuccess
               "SELECT v FROM contract_aggregate_ordering HAVING TRUE ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY SUM(SUM(v))", OracleError(1111, "HY000")
               "SELECT SUM(v) AS a FROM contract_aggregate_ordering ORDER BY SUM(a)", OracleError(1111, "HY000")
               "SELECT ROW_NUMBER() OVER (ORDER BY v) FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY (SELECT SUM(v))", OracleError(3029, "HY000")
               "SELECT v FROM contract_aggregate_ordering ORDER BY ABS(SUM(v))", OracleError(3029, "HY000")
               "SELECT DISTINCT v FROM contract_aggregate_ordering ORDER BY SUM(v)", OracleError(3029, "HY000")
               "SELECT v,(SELECT SUM(v)) AS total FROM contract_aggregate_ordering GROUP BY v ORDER BY v", OracleSuccess
               "SELECT v,(SELECT SUM(v)) AS total,ROW_NUMBER() OVER (ORDER BY v) AS rn FROM contract_aggregate_ordering GROUP BY v ORDER BY v", OracleSuccess |]
        { Name = "aggregate-ordering-classification"
          Setup =
            [| "DROP TABLE IF EXISTS contract_aggregate_ordering"
               "CREATE TABLE contract_aggregate_ordering(v INT)"
               "INSERT INTO contract_aggregate_ordering VALUES(2),(1)" |]
          Steps =
            [| for sql, expectation in queries do
                   yield { Contract.query sql sql with Expectation = expectation }
                   yield { Contract.preparedQuery ("prepared: " + sql) sql [||] with Expectation = expectation } |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_aggregate_ordering" |]
          Coverage = [| "statement:select", [| "aggregation"; "ordering"; "text-differential"; "prepared-differential" |] |] }

    let private groupedAggregateInputs =
        let queries =
            [| "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs ORDER BY s"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs ORDER BY ABS(s)"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs ORDER BY SUM(@n:=@n+1)"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs ORDER BY ABS(SUM(@n:=@n+1))"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v ORDER BY s"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v ORDER BY ABS(s)"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v ORDER BY SUM(@n:=@n+1)"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))"
               "SELECT SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM contract_grouped_inputs GROUP BY v ORDER BY ABS(SUM(@n:=@n+1))"
               "SELECT SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v HAVING TRUE ORDER BY ABS(SUM(@n:=@n+1))"
               "SELECT SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM contract_grouped_inputs"
               "SELECT v,MIN(@n:=@n+1) AS lo,MAX(@n:=@n+1) AS hi FROM contract_grouped_inputs GROUP BY v ORDER BY v"
               "SELECT v,COUNT(DISTINCT (@n:=@n+1)) AS n FROM contract_grouped_inputs GROUP BY v ORDER BY v"
               "SELECT v,GROUP_CONCAT(@n:=@n+1 ORDER BY v) AS s FROM contract_grouped_inputs GROUP BY v ORDER BY v"
               "SELECT v,SUM(v) AS s FROM contract_grouped_inputs GROUP BY v WITH ROLLUP"
               "SELECT v,SUM(@n:=@n+1) AS s FROM contract_grouped_inputs GROUP BY v HAVING SUM(@n:=@n+1)>0 ORDER BY v"
               "SELECT COERCIBILITY(SUM(@n:=@n+1)) AS c FROM contract_grouped_inputs GROUP BY v ORDER BY v" |]
        { Name = "grouped-aggregate-input-order"
          Setup = [| "DROP TABLE IF EXISTS contract_grouped_inputs"; "CREATE TABLE contract_grouped_inputs(v INT)" |]
          Steps =
            [| for values in [ "(2),(1)"; "(2),(1),(3)"; "(2),(1),(2)" ] do
                   yield Contract.execute "reset input rows" "DELETE FROM contract_grouped_inputs"
                   yield Contract.execute "input rows" ("INSERT INTO contract_grouped_inputs VALUES" + values)
                   for sql in queries do
                       yield Contract.execute "reset counter" "SET @n=0"
                       yield Contract.query sql sql
                       yield Contract.query "counter" "SELECT @n"
                       yield Contract.execute "reset prepared counter" "SET @n=0"
                       yield Contract.execute "prepare grouped inputs" ("PREPARE grouped_inputs FROM '" + sql.Replace("'", "''") + "'")
                       yield Contract.query ("prepared: " + sql) "EXECUTE grouped_inputs"
                       yield Contract.query "prepared counter" "SELECT @n"
                       yield Contract.execute "deallocate grouped inputs" "DEALLOCATE PREPARE grouped_inputs" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_grouped_inputs"; "SET @n=NULL" |]
          Coverage = [| "statement:select", [| "aggregation"; "evaluation-order"; "text-differential"; "prepared-differential" |] |] }

    let private groupedAggregateFamilies =
        let queries =
            [| "SELECT g,SUM(@n:=@n+1) AS s, MIN(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, MAX(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, MIN(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, MAX(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, JSON_ARRAYAGG(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, JSON_OBJECTAGG(id,v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, BIT_AND(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s, MIN(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, MAX(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT 1) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, SUM(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, AVG(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, MIN(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, MAX(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, GROUP_CONCAT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, JSON_ARRAYAGG(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, JSON_OBJECTAGG(id,v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s, BIT_AND(v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g,SUM(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g HAVING COUNT(DISTINCT v)>0 ORDER BY g"
               "SELECT g,SUM(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g ORDER BY COUNT(DISTINCT v),g"
               "SELECT g,JSON_ARRAYAGG(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g,GROUP_CONCAT(@n:=@n+1) AS s FROM contract_group_plan GROUP BY g ORDER BY g"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY 1 DESC"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY x DESC"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g+0 DESC"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY g DESC,s"
               "SELECT g AS x,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other FROM contract_group_plan GROUP BY g ORDER BY s,g DESC" |]
        { Name = "grouped-aggregate-families"
          Setup = [| "CREATE TABLE contract_group_plan(id INT PRIMARY KEY,g INT,v INT)"
                     "INSERT INTO contract_group_plan VALUES(1,2,10),(2,1,20),(3,2,30)" |]
          Steps =
            [| for sql in queries do
                   yield Contract.execute "reset counter" "SET @n=0"
                   yield Contract.query sql sql
                   yield Contract.query "counter" "SELECT @n"
                   yield Contract.execute "reset prepared counter" "SET @n=0"
                   yield Contract.execute "prepare grouped family" ("PREPARE grouped_family FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query ("prepared: " + sql) "EXECUTE grouped_family"
                   yield Contract.query "prepared counter" "SELECT @n"
                   yield Contract.execute "deallocate grouped family" "DEALLOCATE PREPARE grouped_family" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_group_plan"; "SET @n=NULL" |]
          Coverage = [| "statement:select", [| "aggregation"; "evaluation-order"; "text-differential"; "prepared-differential" |] |] }

    let private groupConcatOrdering =
        let queries =
            [| "SELECT GROUP_CONCAT(id ORDER BY k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(id ORDER BY k DESC) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(id ORDER BY k,id) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(id ORDER BY k,id DESC) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(id ORDER BY k-k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(v) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT v) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT v ORDER BY k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT v ORDER BY k DESC) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(v ORDER BY k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT v ORDER BY k-k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT id ORDER BY k-k) AS s FROM concat_order"
               "SELECT GROUP_CONCAT(DISTINCT n) AS s FROM concat_types"
               "SELECT GROUP_CONCAT(DISTINCT e) AS s FROM concat_types"
               "SELECT GROUP_CONCAT(DISTINCT e ORDER BY e) AS s FROM concat_types"
               "SELECT GROUP_CONCAT(DISTINCT v) AS s FROM concat_types"
               "SELECT GROUP_CONCAT(id ORDER BY v) AS s FROM concat_types" |]
        { Name = "group-concat-ordering"
          Setup = [| "CREATE TABLE concat_order(id INT PRIMARY KEY,k INT,v VARCHAR(8) COLLATE utf8mb4_0900_ai_ci)"
                     "INSERT INTO concat_order VALUES(1,2,'b'),(2,1,'a'),(3,2,'c'),(4,1,'B'),(5,2,'d'),(6,1,'e'),(7,2,'f'),(8,1,'A')"
                     "CREATE TABLE concat_types(id INT,n INT,e ENUM('z','a','m'),v VARCHAR(8) COLLATE utf8mb4_bin)"
                     "INSERT INTO concat_types VALUES(1,10,'m','b'),(2,2,'z','a'),(3,1,'a','B'),(4,2,'z',NULL),(5,NULL,NULL,'A')" |]
          Steps =
            [| for sql in queries do
                   yield Contract.query sql sql
                   yield Contract.execute "prepare concat ordering" ("PREPARE concat_ordering FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query ("prepared: " + sql) "EXECUTE concat_ordering"
                   yield Contract.execute "deallocate concat ordering" "DEALLOCATE PREPARE concat_ordering"
               yield Contract.execute "short concat limit" "SET group_concat_max_len=4"
               yield Contract.query "truncated tied order" "SELECT GROUP_CONCAT(id ORDER BY k) AS s FROM concat_order"
               yield Contract.query "truncation warnings" "SHOW WARNINGS"
               yield Contract.execute "restore concat limit" "SET group_concat_max_len=DEFAULT" |]
          Cleanup = [| "DROP TABLE IF EXISTS concat_order"; "DROP TABLE IF EXISTS concat_types"; "SET group_concat_max_len=DEFAULT" |]
          Coverage = [| "statement:select", [| "aggregation"; "ordering"; "text-differential"; "prepared-differential" |] |] }

    let private rollupEvaluation =
        let queries =
            [| "SELECT g,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS a,SUM(@n:=@n+1) AS b FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=0"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 0"
               "SELECT g,h,COUNT(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,GROUP_CONCAT(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,JSON_ARRAYAGG(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input WHERE FALSE GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,MIN(@n:=@n+1) AS lo,MAX(@n:=@n+1) AS hi FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s,COUNT(DISTINCT (@n:=@n+1)) AS n FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s,GROUP_CONCAT(@n:=@n+1) AS c FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC LIMIT 1"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY s DESC LIMIT 1"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1 OFFSET 2"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP HAVING GROUPING(g,h)=3 LIMIT 1"
               "SELECT g,h,COUNT(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,AVG(DISTINCT (@n:=@n+1)) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,JSON_OBJECTAGG(id,@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP"
               "SELECT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP ORDER BY g DESC,h DESC LIMIT 0"
               "SELECT DISTINCT g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1"
               "SELECT SQL_CALC_FOUND_ROWS g,h,SUM(@n:=@n+1) AS s FROM rollup_input GROUP BY g,h WITH ROLLUP LIMIT 1" |]
        { Name = "rollup-evaluation"
          Setup = [| "CREATE TABLE rollup_input(id INT PRIMARY KEY,g INT,h INT)"
                     "INSERT INTO rollup_input VALUES(1,2,2),(2,1,1),(3,2,1),(4,1,2),(5,2,1)" |]
          Steps =
            [| for sql in queries do
                   yield Contract.execute "reset counter" "SET @n=0"
                   yield Contract.query sql sql
                   if sql.Contains("SQL_CALC_FOUND_ROWS") then
                       yield Contract.query "found rows" "SELECT FOUND_ROWS()"
                   yield Contract.query "counter" "SELECT @n"
                   yield Contract.execute "reset prepared counter" "SET @n=0"
                   yield Contract.execute "prepare rollup" ("PREPARE rollup_evaluation FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query ("prepared: " + sql) "EXECUTE rollup_evaluation"
                   if sql.Contains("SQL_CALC_FOUND_ROWS") then
                       yield Contract.query "prepared found rows" "SELECT FOUND_ROWS()"
                   yield Contract.query "prepared counter" "SELECT @n"
                   yield Contract.execute "deallocate rollup" "DEALLOCATE PREPARE rollup_evaluation" |]
          Cleanup = [| "DROP TABLE IF EXISTS rollup_input"; "SET @n=NULL" |]
          Coverage = [| "statement:select", [| "aggregation"; "rollup"; "evaluation-order"; "text-differential"; "prepared-differential" |] |] }

    let private groupedProjectionReplay =
        let queries =
            [| "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10"
               "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)"
               "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1"
               "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT 100+(@n:=@n+1) AS a,100+(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10"
               "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1"
               "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT (@n:=100+(@m:=@m+1)) AS a,(@n:=100+(@m:=@m+1)) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10"
               "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1"
               "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT (@n:=@n+1) AS a,@n AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10"
               "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 1"
               "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT (@n:=@n+1) AS a,(@m:=@n) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 10"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY (@m:=v) DESC LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input WHERE v>1 GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY v"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v)"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1 OFFSET 2"
               "SELECT SQL_CALC_FOUND_ROWS (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 0"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,COUNT(DISTINCT v) AS c FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,COUNT(DISTINCT v) AS c FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,SUM(v) AS c FROM replay_input GROUP BY v ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v,SUM(v) AS c FROM replay_input GROUP BY v HAVING v>1 ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v WITH ROLLUP ORDER BY IF(a=b,v,-v) LIMIT 1"
               "SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM replay_input GROUP BY v HAVING a>2 ORDER BY v"
               "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING FALSE ORDER BY a+0"
               "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING 1=0 ORDER BY a+0"
               "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING v<0 ORDER BY a+0"
               "SELECT (@n:=@n+1) AS a,v FROM replay_input GROUP BY v HAVING TRUE ORDER BY a+0" |]
        { Name = "grouped-projection-replay"
          Setup = [| "CREATE TABLE replay_input(v INT)"; "INSERT INTO replay_input VALUES(2),(1),(3)" |]
          Steps =
            [| for sql in queries do
                   yield Contract.execute "reset variables" "SET @n=0,@m=0"
                   yield Contract.query sql sql
                   if sql.Contains("SQL_CALC_FOUND_ROWS") then
                       yield Contract.query "found rows" "SELECT FOUND_ROWS()"
                   yield Contract.query "variables" "SELECT @n,@m"
                   yield Contract.execute "reset prepared variables" "SET @n=0,@m=0"
                   yield Contract.execute "prepare replay" ("PREPARE projection_replay FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query ("prepared: " + sql) "EXECUTE projection_replay"
                   if sql.Contains("SQL_CALC_FOUND_ROWS") then
                       yield Contract.query "prepared found rows" "SELECT FOUND_ROWS()"
                   yield Contract.query "prepared variables" "SELECT @n,@m"
                   yield Contract.execute "deallocate replay" "DEALLOCATE PREPARE projection_replay" |]
          Cleanup = [| "DROP TABLE IF EXISTS replay_input"; "SET @n=NULL,@m=NULL" |]
          Coverage = [| "statement:select", [| "aggregation"; "evaluation-order"; "text-differential"; "prepared-differential" |] |] }

    let private windowBindings =
        let cases =
            [
                "SELECT v AS a FROM window_clause ORDER BY ROW_NUMBER() OVER (ORDER BY a)", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (PARTITION BY missing) FROM window_clause", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER w FROM window_clause WINDOW w AS (ORDER BY missing)", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE FALSE", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause LIMIT 0", 1054, "42S22"
                "SELECT SUM(v) OVER (ORDER BY missing RANGE BETWEEN 1 PRECEDING AND CURRENT ROW) FROM window_clause", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (ORDER BY absent.v) FROM window_clause", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (PARTITION BY absent.v) FROM window_clause", 1054, "42S22"
                "SELECT 1 FROM window_clause WINDOW w AS (ORDER BY missing)", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER (ORDER BY absent.v)", 1109, "42S02"
                "SELECT ROW_NUMBER() OVER (PARTITION BY absent.v)", 1109, "42S02"
                "SELECT ROW_NUMBER() OVER (ORDER BY missing) FROM window_clause WHERE absent=1", 1054, "42S22"
                "SELECT v AS a,ROW_NUMBER() OVER (PARTITION BY a) FROM window_clause", 1054, "42S22"
                "SELECT ROW_NUMBER() OVER child FROM window_clause WINDOW parent AS (ORDER BY missing),child AS (parent)", 1054, "42S22" ]
        { Name = "window-binding-diagnostics"
          Setup = [| "CREATE TABLE window_clause(v INT)"; "INSERT INTO window_clause VALUES(2),(1)" |]
          Steps =
            [| for index, (sql, code, state) in List.indexed cases do
                   yield Contract.query (sprintf "direct-%d" index) sql |> Contract.fails code state
                   yield Contract.execute (sprintf "sql-prepare-%d" index) ("PREPARE window_binding FROM '" + sql.Replace("'", "''") + "'") |> Contract.fails code state
                   yield Contract.prepare (sprintf "binary-prepare-%d" index) (sprintf "window-%d" index) Query sql [||] |> Contract.fails code state |]
          Cleanup = [| "DROP TABLE IF EXISTS window_clause" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private sourceExpressionCollation =
        let queries =
            [
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_base"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_base GROUP BY v WITH ROLLUP"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_literal"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_literal GROUP BY v WITH ROLLUP"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_expression"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_expression GROUP BY v WITH ROLLUP"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_column"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM identity_column GROUP BY v WITH ROLLUP"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM (SELECT _latin1'a' AS v) d"
                "SELECT v,CHARSET(v) AS cs,COLLATION(v) AS co,COERCIBILITY(v) AS c FROM (SELECT _latin1'a' AS v) d GROUP BY v WITH ROLLUP"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT _latin1'a' AS v LIMIT 1) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT DISTINCT _latin1'a' AS v) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT CONCAT(v,'x') AS v FROM identity_base) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT SUM(1) AS v FROM identity_base) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT COUNT(*) AS v FROM identity_base) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT NULL AS v) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT 1 AS v) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT _latin1'a' AS v UNION ALL SELECT _latin1'b') d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT _latin1'a' AS v FROM identity_base GROUP BY v) d"
                "SELECT v,CHARSET(v),COLLATION(v),COERCIBILITY(v) FROM (SELECT CONCAT('a','b') AS v LIMIT 1) d"
                "SELECT COERCIBILITY(v) AS n,(SELECT 1) AS s FROM identity_literal"
                "SELECT COERCIBILITY(v) AS n FROM identity_literal JOIN (SELECT 1 AS x) t ON 1"
                "SELECT COERCIBILITY(v),CHARSET(v),COLLATION(v) FROM (SELECT * FROM identity_base UNION ALL SELECT _latin1'b' COLLATE latin1_bin) d"
                "SELECT COERCIBILITY(v),CHARSET(v),COLLATION(v) FROM (SELECT v FROM identity_base UNION ALL SELECT _latin1'b' COLLATE latin1_bin) d"
                "SELECT COERCIBILITY(v),CHARSET(v),COLLATION(v) FROM (SELECT _latin1'b' COLLATE latin1_bin AS v UNION ALL SELECT * FROM identity_base) d"
                "SELECT COERCIBILITY(id),CHARSET(id),COLLATION(id) FROM identity_numbers"
                "SELECT COERCIBILITY(id),CHARSET(id),COLLATION(id) FROM (SELECT * FROM identity_numbers) d"
                "SELECT (SELECT COERCIBILITY(v)) FROM identity_literal"
                "SELECT COERCIBILITY(v),CHARSET(v),COLLATION(v) FROM (SELECT * FROM identity_literal UNION ALL SELECT * FROM identity_literal) d"
            ]
        { Name = "source-expression-collation"
          Setup = [| "SET NAMES latin1 COLLATE latin1_bin"; "CREATE TABLE identity_base(v VARCHAR(8) CHARACTER SET latin1 COLLATE latin1_bin)"; "INSERT INTO identity_base VALUES('a')"; "CREATE VIEW identity_literal AS SELECT 'a' AS v"; "CREATE VIEW identity_expression AS SELECT CONCAT('a','b') AS v"; "CREATE VIEW identity_column AS SELECT v FROM identity_base"; "CREATE TABLE identity_numbers(id INT)"; "INSERT INTO identity_numbers VALUES(1)"; "SET NAMES utf8mb4" |]
          Steps =
            [| for index, sql in List.indexed queries do
                   yield Contract.query (sprintf "direct-%d" index) sql
                   yield Contract.preparedQuery (sprintf "binary-%d" index) sql [||]
                   yield Contract.execute (sprintf "prepare-%d" index) ("PREPARE source_identity FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query (sprintf "execute-%d" index) "EXECUTE source_identity"
                   yield Contract.execute (sprintf "deallocate-%d" index) "DEALLOCATE PREPARE source_identity"
               for columnType in [ "TINYINT"; "SMALLINT"; "MEDIUMINT"; "INT"; "BIGINT"; "DECIMAL(8,2)"; "FLOAT"; "DOUBLE"; "DATE"; "DATETIME"; "TIMESTAMP NULL"; "TIME"; "YEAR"; "BIT(2)"; "BINARY(2)"; "JSON"; "GEOMETRY" ] do
                   yield Contract.execute ("create-" + columnType) ("CREATE TEMPORARY TABLE type_identity(v " + columnType + ")")
                   yield Contract.execute ("insert-" + columnType) "INSERT INTO type_identity VALUES(NULL)"
                   let sql = "SELECT COERCIBILITY(v),CHARSET(v),COLLATION(v) FROM type_identity"
                   yield Contract.query ("type-direct-" + columnType) sql
                   yield Contract.preparedQuery ("type-binary-" + columnType) sql [||]
                   yield Contract.execute ("drop-" + columnType) "DROP TEMPORARY TABLE type_identity" |]
          Cleanup = [| "DROP VIEW IF EXISTS identity_column,identity_expression,identity_literal"; "DROP TABLE IF EXISTS identity_base,identity_numbers"; "SET NAMES utf8mb4" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private introducedEncoding =
        let invalid =
            [
                "SELECT _utf8mb4 X'FF'"
                "SELECT _utf8mb4 X'C080'"
                "SELECT _utf8mb4 X'E282'"
                "SELECT _utf8mb3 X'F09F9880'"
                "SELECT _utf8mb4 X'41FF42' WHERE FALSE"
                "SELECT CHARSET(_utf8mb4 X'FF')"
                "SELECT IF(FALSE,_utf8mb4 X'FF','ok')"
                "SELECT _utf8mb4 X'FF' COLLATE latin1_bin"
                "SELECT _utf8 X'F09F9880'"
                "SELECT _utf8mb4 0xFF"
                "SELECT _utf8mb4 0b11111111"
                "SELECT _utf16 X'D800'"
                "SELECT _utf16 X'0041D8000042'"
                "SELECT _utf32 X'00110000'"
                "SELECT _utf8mb4 X'41FF42434445'"
                "SELECT _utf16le X'410000D84200'"
                "SELECT _utf32 X'000000410000'"
                "SELECT _utf8mb3 X'41F09F9880FF'"
                "SELECT HEX(_utf32 X'110000') AS h"
            ]
        let valid =
            [
                "SELECT HEX(_utf8mb4 X'41F09F988042') AS h"
                "SELECT HEX(_utf8mb3 X'E282AC') AS h"
                "SELECT HEX(_utf16 X'0041D83DDE000042') AS h"
                "SELECT HEX(_utf16le X'41003DD800DE4200') AS h"
                "SELECT HEX(_utf32 X'000000410001F60000000042') AS h"
                "SELECT HEX(_utf8mb4 X'') AS h"
                "SELECT HEX(_latin1 X'FF') AS h"
                "SELECT HEX(_binary X'FF') AS h"
                "SELECT HEX(_utf16 X'FFFE') AS h"
                "SELECT HEX(_utf16 X'41') AS h"
                "SELECT HEX(_utf16 X'0041FF') AS h"
                "SELECT HEX(_utf16 X'110000') AS h"
                "SELECT HEX(_utf16 X'') AS h"
                "SELECT HEX(_utf16le X'41') AS h"
                "SELECT HEX(_utf16le X'0041FF') AS h"
                "SELECT HEX(_utf16le X'110000') AS h"
                "SELECT HEX(_utf16le X'') AS h"
                "SELECT HEX(_ucs2 X'41') AS h"
                "SELECT HEX(_ucs2 X'0041FF') AS h"
                "SELECT HEX(_ucs2 X'110000') AS h"
                "SELECT HEX(_ucs2 X'') AS h"
                "SELECT HEX(_utf32 X'41') AS h"
                "SELECT HEX(_utf32 X'0041FF') AS h"
                "SELECT HEX(_utf32 X'') AS h"
            ]
        let bytePreservation =
            [
                "SELECT HEX(_ascii X'80') AS h,LENGTH(_ascii X'80') AS n;SHOW WARNINGS"
                "SELECT HEX(_ucs2 X'D800') AS h,LENGTH(_ucs2 X'D800') AS n;SHOW WARNINGS"
                "SELECT HEX(_utf8mb3'😀') AS h,LENGTH(_utf8mb3'😀') AS n;SHOW WARNINGS"
                "SELECT HEX(N'😀') AS h,LENGTH(N'😀') AS n;SHOW WARNINGS"
                "SELECT HEX(_ascii'é') AS h,LENGTH(_ascii'é') AS n;SHOW WARNINGS"
                "SELECT HEX(_utf8mb3'a😀b') AS h;SHOW WARNINGS"
                "PREPARE p FROM 'SELECT HEX(_ascii X''80'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "PREPARE p FROM 'SELECT HEX(_utf8mb3''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "PREPARE p FROM 'SELECT HEX(N''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "PREPARE p FROM 'SELECT HEX(_ucs2 X''D800'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET @v=_ascii X'80';SHOW WARNINGS;SELECT HEX(@v) AS h;SHOW WARNINGS"
                "SELECT HEX(v) AS h FROM (SELECT _ascii X'80' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _ascii X'80' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _ascii X'418042' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _ascii'é' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _utf8mb3'😀' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _utf8mb3'a😀b' AS v) d;SHOW WARNINGS"
                "SELECT HEX(v) AS h,LENGTH(v) AS n FROM (SELECT _ucs2 X'D800' AS v) d;SHOW WARNINGS"
                "SET NAMES utf8mb4;PREPARE p FROM 'SELECT HEX(''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb4;SET @src='SELECT HEX(''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb4;PREPARE p FROM 'SELECT HEX(_utf8mb3''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb4;SET @src='SELECT HEX(_utf8mb3''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb4;PREPARE p FROM 'SELECT HEX(N''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb4;SET @src='SELECT HEX(N''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;PREPARE p FROM 'SELECT HEX(''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;SET @src='SELECT HEX(''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;PREPARE p FROM 'SELECT HEX(_utf8mb3''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;SET @src='SELECT HEX(_utf8mb3''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;PREPARE p FROM 'SELECT HEX(N''😀'') AS h';SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
                "SET NAMES utf8mb3;SET @src='SELECT HEX(N''😀'') AS h';SELECT HEX(@src) AS source_hex;PREPARE p FROM @src;SHOW WARNINGS;EXECUTE p;SHOW WARNINGS"
            ]
        { Name = "introduced-literal-encoding"
          Setup = [||]
          Steps =
            [| for index, sql in List.indexed invalid do
                   yield Contract.query (sprintf "invalid-direct-%d" index) sql |> Contract.fails 1300 "HY000"
                   yield Contract.prepare (sprintf "invalid-binary-%d" index) (sprintf "invalid-encoding-%d" index) Query sql [||] |> Contract.fails 1300 "HY000"
                   yield Contract.execute (sprintf "invalid-prepare-%d" index) ("PREPARE invalid_encoding FROM '" + sql.Replace("'", "''") + "'") |> Contract.fails 1300 "HY000"
               for index, sql in List.indexed valid do
                   yield Contract.query (sprintf "valid-direct-%d" index) sql
                   yield Contract.preparedQuery (sprintf "valid-binary-%d" index) sql [||]
                   yield Contract.execute (sprintf "valid-prepare-%d" index) ("PREPARE valid_encoding FROM '" + sql.Replace("'", "''") + "'")
                   yield Contract.query (sprintf "valid-execute-%d" index) "EXECUTE valid_encoding"
                   yield Contract.execute (sprintf "valid-close-%d" index) "DEALLOCATE PREPARE valid_encoding"
               for index, sql in List.indexed bytePreservation do
                   yield Contract.execute (sprintf "bytes-charset-%d" index) "SET NAMES utf8mb4"
                   for step, fragment in sql.Split(';') |> Array.indexed do
                       let label = sprintf "bytes-%d-%d" index step
                       if fragment.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                          || fragment.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase)
                          || fragment.StartsWith("EXECUTE", StringComparison.OrdinalIgnoreCase) then
                           yield Contract.query label fragment
                       else
                           yield Contract.execute label fragment
               yield Contract.execute "bytes-reset-charset" "SET NAMES utf8mb4"
               for index, (charset, literal) in
                   [ "ascii", "_ascii X'418042'"
                     "utf8mb3", "_utf8mb3'a😀b'"
                     "utf8mb4", "_utf8mb3'a😀b'"
                     "utf8mb4", "_ascii X'418042'" ] |> List.indexed do
                   let label name = sprintf "encoded-storage-%d-%s" index name
                   yield Contract.execute (label "drop") "DROP TABLE IF EXISTS encoded_target"
                   yield Contract.execute (label "create") ("CREATE TABLE encoded_target(v VARCHAR(20) CHARACTER SET " + charset + ")")
                   yield Contract.execute (label "permissive") "SET sql_mode=''"
                   yield Contract.execute (label "insert") ("INSERT INTO encoded_target VALUES(" + literal + ")")
                   yield Contract.query (label "warnings") "SHOW WARNINGS"
                   yield Contract.query (label "value") "SELECT HEX(v) AS h FROM encoded_target"
                   yield Contract.execute (label "truncate") "TRUNCATE encoded_target"
                   yield Contract.execute (label "strict") "SET sql_mode='STRICT_TRANS_TABLES'"
                   yield Contract.execute (label "reject") ("INSERT INTO encoded_target VALUES(" + literal + ")") |> Contract.fails 1366 "HY000"
                   yield Contract.query (label "strict-warnings") "SHOW WARNINGS"
                   yield Contract.query (label "empty") "SELECT COUNT(*) AS n FROM encoded_target"
               yield Contract.execute "encoded-storage-drop" "DROP TABLE encoded_target"
               for index, (sql, rejected) in
                   [ "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ucs2);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET utf8mb4);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", true
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(1) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(2) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(3) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(1) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(2) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(3) CHARACTER SET ucs2);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(1) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(2) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(3) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D8000042');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(1) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(2) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(3) CHARACTER SET utf8mb4);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'D83DDE00');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n,CHAR_LENGTH(v) AS c FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D83DDE0000420043');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", false
                     "DROP TABLE IF EXISTS surrogate_target;CREATE TABLE surrogate_target(v VARCHAR(20) CHARACTER SET ascii);SET sql_mode='STRICT_TRANS_TABLES';INSERT INTO surrogate_target VALUES(_ucs2 X'0041D83DDE0000420043');SHOW WARNINGS;SELECT HEX(v) AS h,LENGTH(v) AS n FROM surrogate_target", true ] |> List.indexed do
                   for step, fragment in sql.Split(';') |> Array.indexed do
                       let label = sprintf "ucs2-storage-%d-%d" index step
                       if fragment.StartsWith("SELECT") || fragment.StartsWith("SHOW") then
                           yield Contract.query label fragment
                       elif rejected && fragment.StartsWith("INSERT") then
                           yield Contract.execute label fragment |> Contract.fails 1366 "HY000"
                       else
                           yield Contract.execute label fragment
               yield Contract.execute "ucs2-storage-drop" "DROP TABLE surrogate_target"
               for index, (sql, rejected) in
                   [ "SELECT HEX(REVERSE(_ucs2 X'0041D83DDE000042')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(LEFT(_ucs2 X'0041D83DDE000042',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(RIGHT(_ucs2 X'0041D83DDE000042',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(SUBSTRING(_ucs2 X'0041D83DDE000042',2,1)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ucs2 X'0041D83DDE000042',_ucs2 X'0041D83DDE000042')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(REVERSE(_ascii X'418042')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(LEFT(_ascii X'418042',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(RIGHT(_ascii X'418042',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(SUBSTRING(_ascii X'418042',2,1)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ascii X'418042',_ascii X'418042')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(REVERSE(_utf8mb3'a😀b')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(LEFT(_utf8mb3'a😀b',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(RIGHT(_utf8mb3'a😀b',2)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(SUBSTRING(_utf8mb3'a😀b',2,1)) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_utf8mb3'a😀b',_utf8mb3'a😀b')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ucs2 X'0041D83DDE000042','x')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ucs2 X'0041D83DDE000042',_binary X'78')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ucs2 X'0041D83DDE000042',LEFT(_ucs2 X'0041D83DDE000042',1))) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ascii X'418042','x')) AS h;SHOW WARNINGS", true
                     "SELECT HEX(CONCAT(_ascii X'418042',_binary X'78')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_ascii X'418042',LEFT(_ascii X'418042',1))) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_utf8mb3'a😀b','x')) AS h;SHOW WARNINGS", true
                     "SELECT HEX(CONCAT(_utf8mb3'a😀b',_binary X'78')) AS h;SHOW WARNINGS", false
                     "SELECT HEX(CONCAT(_utf8mb3'a😀b',LEFT(_utf8mb3'a😀b',1))) AS h;SHOW WARNINGS", false ] |> List.indexed do
                   yield Contract.execute (sprintf "encoded-expression-charset-%d" index) "SET NAMES utf8mb4"
                   if rejected then
                       yield Contract.query (sprintf "encoded-expression-error-%d" index) (sql.Split(';').[0]) |> Contract.fails 1267 "HY000"
                   else
                       for step, fragment in sql.Split(';') |> Array.indexed do
                           yield Contract.query (sprintf "encoded-expression-%d-%d" index step) fragment
               for index, sql in
                   [ "SELECT HEX(REVERSE(_sjis X'41814042')) AS h,CHAR_LENGTH(_sjis X'41814042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41817E42')) AS h,CHAR_LENGTH(_sjis X'41817E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41818042')) AS h,CHAR_LENGTH(_sjis X'41818042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'4181FC42')) AS h,CHAR_LENGTH(_sjis X'4181FC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419F4042')) AS h,CHAR_LENGTH(_sjis X'419F4042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419F7E42')) AS h,CHAR_LENGTH(_sjis X'419F7E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419F8042')) AS h,CHAR_LENGTH(_sjis X'419F8042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419FFC42')) AS h,CHAR_LENGTH(_sjis X'419FFC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DF3F42')) AS h,CHAR_LENGTH(_sjis X'41DF3F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DF4042')) AS h,CHAR_LENGTH(_sjis X'41DF4042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DF7E42')) AS h,CHAR_LENGTH(_sjis X'41DF7E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DF7F42')) AS h,CHAR_LENGTH(_sjis X'41DF7F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DFFC42')) AS h,CHAR_LENGTH(_sjis X'41DFFC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E04042')) AS h,CHAR_LENGTH(_sjis X'41E04042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E07E42')) AS h,CHAR_LENGTH(_sjis X'41E07E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E08042')) AS h,CHAR_LENGTH(_sjis X'41E08042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E0FC42')) AS h,CHAR_LENGTH(_sjis X'41E0FC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EF4042')) AS h,CHAR_LENGTH(_sjis X'41EF4042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EF7E42')) AS h,CHAR_LENGTH(_sjis X'41EF7E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EF8042')) AS h,CHAR_LENGTH(_sjis X'41EF8042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EFFC42')) AS h,CHAR_LENGTH(_sjis X'41EFFC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F04042')) AS h,CHAR_LENGTH(_sjis X'41F04042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F07E42')) AS h,CHAR_LENGTH(_sjis X'41F07E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F08042')) AS h,CHAR_LENGTH(_sjis X'41F08042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F0FC42')) AS h,CHAR_LENGTH(_sjis X'41F0FC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FC4042')) AS h,CHAR_LENGTH(_sjis X'41FC4042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FC7E42')) AS h,CHAR_LENGTH(_sjis X'41FC7E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FC8042')) AS h,CHAR_LENGTH(_sjis X'41FC8042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FCFC42')) AS h,CHAR_LENGTH(_sjis X'41FCFC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41814042')) AS h,CHAR_LENGTH(_cp932 X'41814042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41817E42')) AS h,CHAR_LENGTH(_cp932 X'41817E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41818042')) AS h,CHAR_LENGTH(_cp932 X'41818042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'4181FC42')) AS h,CHAR_LENGTH(_cp932 X'4181FC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419F4042')) AS h,CHAR_LENGTH(_cp932 X'419F4042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419F7E42')) AS h,CHAR_LENGTH(_cp932 X'419F7E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419F8042')) AS h,CHAR_LENGTH(_cp932 X'419F8042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419FFC42')) AS h,CHAR_LENGTH(_cp932 X'419FFC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DF3F42')) AS h,CHAR_LENGTH(_cp932 X'41DF3F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DF4042')) AS h,CHAR_LENGTH(_cp932 X'41DF4042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DF7E42')) AS h,CHAR_LENGTH(_cp932 X'41DF7E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DF7F42')) AS h,CHAR_LENGTH(_cp932 X'41DF7F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DFFC42')) AS h,CHAR_LENGTH(_cp932 X'41DFFC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E04042')) AS h,CHAR_LENGTH(_cp932 X'41E04042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E07E42')) AS h,CHAR_LENGTH(_cp932 X'41E07E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E08042')) AS h,CHAR_LENGTH(_cp932 X'41E08042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E0FC42')) AS h,CHAR_LENGTH(_cp932 X'41E0FC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EF4042')) AS h,CHAR_LENGTH(_cp932 X'41EF4042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EF7E42')) AS h,CHAR_LENGTH(_cp932 X'41EF7E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EF8042')) AS h,CHAR_LENGTH(_cp932 X'41EF8042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EFFC42')) AS h,CHAR_LENGTH(_cp932 X'41EFFC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F04042')) AS h,CHAR_LENGTH(_cp932 X'41F04042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F07E42')) AS h,CHAR_LENGTH(_cp932 X'41F07E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F08042')) AS h,CHAR_LENGTH(_cp932 X'41F08042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F0FC42')) AS h,CHAR_LENGTH(_cp932 X'41F0FC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FC4042')) AS h,CHAR_LENGTH(_cp932 X'41FC4042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FC7E42')) AS h,CHAR_LENGTH(_cp932 X'41FC7E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FC8042')) AS h,CHAR_LENGTH(_cp932 X'41FC8042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FCFC42')) AS h,CHAR_LENGTH(_cp932 X'41FCFC42') AS n" ] |> List.indexed do
                   yield Contract.query (sprintf "sjis-boundaries-%d" index) sql
               for index, sql in
                   [ "SELECT HEX(REVERSE(_sjis X'41803F42')) AS h,CHAR_LENGTH(_sjis X'41803F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41804042')) AS h,CHAR_LENGTH(_sjis X'41804042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41807E42')) AS h,CHAR_LENGTH(_sjis X'41807E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41807F42')) AS h,CHAR_LENGTH(_sjis X'41807F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41808042')) AS h,CHAR_LENGTH(_sjis X'41808042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'4180FC42')) AS h,CHAR_LENGTH(_sjis X'4180FC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'4180FD42')) AS h,CHAR_LENGTH(_sjis X'4180FD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41813F42')) AS h,CHAR_LENGTH(_sjis X'41813F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41817F42')) AS h,CHAR_LENGTH(_sjis X'41817F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'4181FD42')) AS h,CHAR_LENGTH(_sjis X'4181FD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419F3F42')) AS h,CHAR_LENGTH(_sjis X'419F3F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419F7F42')) AS h,CHAR_LENGTH(_sjis X'419F7F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'419FFD42')) AS h,CHAR_LENGTH(_sjis X'419FFD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A03F42')) AS h,CHAR_LENGTH(_sjis X'41A03F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A04042')) AS h,CHAR_LENGTH(_sjis X'41A04042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A07E42')) AS h,CHAR_LENGTH(_sjis X'41A07E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A07F42')) AS h,CHAR_LENGTH(_sjis X'41A07F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A08042')) AS h,CHAR_LENGTH(_sjis X'41A08042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A0FC42')) AS h,CHAR_LENGTH(_sjis X'41A0FC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41A0FD42')) AS h,CHAR_LENGTH(_sjis X'41A0FD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DF8042')) AS h,CHAR_LENGTH(_sjis X'41DF8042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41DFFD42')) AS h,CHAR_LENGTH(_sjis X'41DFFD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E03F42')) AS h,CHAR_LENGTH(_sjis X'41E03F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E07F42')) AS h,CHAR_LENGTH(_sjis X'41E07F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41E0FD42')) AS h,CHAR_LENGTH(_sjis X'41E0FD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EF3F42')) AS h,CHAR_LENGTH(_sjis X'41EF3F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EF7F42')) AS h,CHAR_LENGTH(_sjis X'41EF7F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41EFFD42')) AS h,CHAR_LENGTH(_sjis X'41EFFD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F03F42')) AS h,CHAR_LENGTH(_sjis X'41F03F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F07F42')) AS h,CHAR_LENGTH(_sjis X'41F07F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41F0FD42')) AS h,CHAR_LENGTH(_sjis X'41F0FD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FC3F42')) AS h,CHAR_LENGTH(_sjis X'41FC3F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FC7F42')) AS h,CHAR_LENGTH(_sjis X'41FC7F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FCFD42')) AS h,CHAR_LENGTH(_sjis X'41FCFD42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FD3F42')) AS h,CHAR_LENGTH(_sjis X'41FD3F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FD4042')) AS h,CHAR_LENGTH(_sjis X'41FD4042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FD7E42')) AS h,CHAR_LENGTH(_sjis X'41FD7E42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FD7F42')) AS h,CHAR_LENGTH(_sjis X'41FD7F42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FD8042')) AS h,CHAR_LENGTH(_sjis X'41FD8042') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FDFC42')) AS h,CHAR_LENGTH(_sjis X'41FDFC42') AS n"
                     "SELECT HEX(REVERSE(_sjis X'41FDFD42')) AS h,CHAR_LENGTH(_sjis X'41FDFD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41803F42')) AS h,CHAR_LENGTH(_cp932 X'41803F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41804042')) AS h,CHAR_LENGTH(_cp932 X'41804042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41807E42')) AS h,CHAR_LENGTH(_cp932 X'41807E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41807F42')) AS h,CHAR_LENGTH(_cp932 X'41807F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41808042')) AS h,CHAR_LENGTH(_cp932 X'41808042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'4180FC42')) AS h,CHAR_LENGTH(_cp932 X'4180FC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'4180FD42')) AS h,CHAR_LENGTH(_cp932 X'4180FD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41813F42')) AS h,CHAR_LENGTH(_cp932 X'41813F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41817F42')) AS h,CHAR_LENGTH(_cp932 X'41817F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'4181FD42')) AS h,CHAR_LENGTH(_cp932 X'4181FD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419F3F42')) AS h,CHAR_LENGTH(_cp932 X'419F3F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419F7F42')) AS h,CHAR_LENGTH(_cp932 X'419F7F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'419FFD42')) AS h,CHAR_LENGTH(_cp932 X'419FFD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A03F42')) AS h,CHAR_LENGTH(_cp932 X'41A03F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A04042')) AS h,CHAR_LENGTH(_cp932 X'41A04042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A07E42')) AS h,CHAR_LENGTH(_cp932 X'41A07E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A07F42')) AS h,CHAR_LENGTH(_cp932 X'41A07F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A08042')) AS h,CHAR_LENGTH(_cp932 X'41A08042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A0FC42')) AS h,CHAR_LENGTH(_cp932 X'41A0FC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41A0FD42')) AS h,CHAR_LENGTH(_cp932 X'41A0FD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DF8042')) AS h,CHAR_LENGTH(_cp932 X'41DF8042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41DFFD42')) AS h,CHAR_LENGTH(_cp932 X'41DFFD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E03F42')) AS h,CHAR_LENGTH(_cp932 X'41E03F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E07F42')) AS h,CHAR_LENGTH(_cp932 X'41E07F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41E0FD42')) AS h,CHAR_LENGTH(_cp932 X'41E0FD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EF3F42')) AS h,CHAR_LENGTH(_cp932 X'41EF3F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EF7F42')) AS h,CHAR_LENGTH(_cp932 X'41EF7F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41EFFD42')) AS h,CHAR_LENGTH(_cp932 X'41EFFD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F03F42')) AS h,CHAR_LENGTH(_cp932 X'41F03F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F07F42')) AS h,CHAR_LENGTH(_cp932 X'41F07F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41F0FD42')) AS h,CHAR_LENGTH(_cp932 X'41F0FD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FC3F42')) AS h,CHAR_LENGTH(_cp932 X'41FC3F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FC7F42')) AS h,CHAR_LENGTH(_cp932 X'41FC7F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FCFD42')) AS h,CHAR_LENGTH(_cp932 X'41FCFD42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FD3F42')) AS h,CHAR_LENGTH(_cp932 X'41FD3F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FD4042')) AS h,CHAR_LENGTH(_cp932 X'41FD4042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FD7E42')) AS h,CHAR_LENGTH(_cp932 X'41FD7E42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FD7F42')) AS h,CHAR_LENGTH(_cp932 X'41FD7F42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FD8042')) AS h,CHAR_LENGTH(_cp932 X'41FD8042') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FDFC42')) AS h,CHAR_LENGTH(_cp932 X'41FDFC42') AS n"
                     "SELECT HEX(REVERSE(_cp932 X'41FDFD42')) AS h,CHAR_LENGTH(_cp932 X'41FDFD42') AS n" ] |> List.indexed do
                   yield Contract.query (sprintf "sjis-invalid-%d" index) sql |> Contract.fails 1300 "HY000"
               for index, (sql, rejected) in
                   [ "SELECT HEX(REVERSE(_big5 X'41803F42')) AS h,CHAR_LENGTH(_big5 X'41803F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41804042')) AS h,CHAR_LENGTH(_big5 X'41804042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41807E42')) AS h,CHAR_LENGTH(_big5 X'41807E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41807F42')) AS h,CHAR_LENGTH(_big5 X'41807F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41808042')) AS h,CHAR_LENGTH(_big5 X'41808042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4180A042')) AS h,CHAR_LENGTH(_big5 X'4180A042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4180A142')) AS h,CHAR_LENGTH(_big5 X'4180A142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4180FE42')) AS h,CHAR_LENGTH(_big5 X'4180FE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4180FF42')) AS h,CHAR_LENGTH(_big5 X'4180FF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41813F42')) AS h,CHAR_LENGTH(_big5 X'41813F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41814042')) AS h,CHAR_LENGTH(_big5 X'41814042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41817E42')) AS h,CHAR_LENGTH(_big5 X'41817E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41817F42')) AS h,CHAR_LENGTH(_big5 X'41817F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41818042')) AS h,CHAR_LENGTH(_big5 X'41818042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4181A042')) AS h,CHAR_LENGTH(_big5 X'4181A042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4181A142')) AS h,CHAR_LENGTH(_big5 X'4181A142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4181FE42')) AS h,CHAR_LENGTH(_big5 X'4181FE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'4181FF42')) AS h,CHAR_LENGTH(_big5 X'4181FF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A03F42')) AS h,CHAR_LENGTH(_big5 X'41A03F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A04042')) AS h,CHAR_LENGTH(_big5 X'41A04042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A07E42')) AS h,CHAR_LENGTH(_big5 X'41A07E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A07F42')) AS h,CHAR_LENGTH(_big5 X'41A07F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A08042')) AS h,CHAR_LENGTH(_big5 X'41A08042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A0A042')) AS h,CHAR_LENGTH(_big5 X'41A0A042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A0A142')) AS h,CHAR_LENGTH(_big5 X'41A0A142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A0FE42')) AS h,CHAR_LENGTH(_big5 X'41A0FE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A0FF42')) AS h,CHAR_LENGTH(_big5 X'41A0FF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A13F42')) AS h,CHAR_LENGTH(_big5 X'41A13F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A14042')) AS h,CHAR_LENGTH(_big5 X'41A14042') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41A17E42')) AS h,CHAR_LENGTH(_big5 X'41A17E42') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41A17F42')) AS h,CHAR_LENGTH(_big5 X'41A17F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A18042')) AS h,CHAR_LENGTH(_big5 X'41A18042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A1A042')) AS h,CHAR_LENGTH(_big5 X'41A1A042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41A1A142')) AS h,CHAR_LENGTH(_big5 X'41A1A142') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41A1FE42')) AS h,CHAR_LENGTH(_big5 X'41A1FE42') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41A1FF42')) AS h,CHAR_LENGTH(_big5 X'41A1FF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41F93F42')) AS h,CHAR_LENGTH(_big5 X'41F93F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41F94042')) AS h,CHAR_LENGTH(_big5 X'41F94042') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41F97E42')) AS h,CHAR_LENGTH(_big5 X'41F97E42') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41F97F42')) AS h,CHAR_LENGTH(_big5 X'41F97F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41F98042')) AS h,CHAR_LENGTH(_big5 X'41F98042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41F9A042')) AS h,CHAR_LENGTH(_big5 X'41F9A042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41F9A142')) AS h,CHAR_LENGTH(_big5 X'41F9A142') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41F9FE42')) AS h,CHAR_LENGTH(_big5 X'41F9FE42') AS n", false
                     "SELECT HEX(REVERSE(_big5 X'41F9FF42')) AS h,CHAR_LENGTH(_big5 X'41F9FF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FA3F42')) AS h,CHAR_LENGTH(_big5 X'41FA3F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FA4042')) AS h,CHAR_LENGTH(_big5 X'41FA4042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FA7E42')) AS h,CHAR_LENGTH(_big5 X'41FA7E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FA7F42')) AS h,CHAR_LENGTH(_big5 X'41FA7F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FA8042')) AS h,CHAR_LENGTH(_big5 X'41FA8042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FAA042')) AS h,CHAR_LENGTH(_big5 X'41FAA042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FAA142')) AS h,CHAR_LENGTH(_big5 X'41FAA142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FAFE42')) AS h,CHAR_LENGTH(_big5 X'41FAFE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FAFF42')) AS h,CHAR_LENGTH(_big5 X'41FAFF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FE3F42')) AS h,CHAR_LENGTH(_big5 X'41FE3F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FE4042')) AS h,CHAR_LENGTH(_big5 X'41FE4042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FE7E42')) AS h,CHAR_LENGTH(_big5 X'41FE7E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FE7F42')) AS h,CHAR_LENGTH(_big5 X'41FE7F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FE8042')) AS h,CHAR_LENGTH(_big5 X'41FE8042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FEA042')) AS h,CHAR_LENGTH(_big5 X'41FEA042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FEA142')) AS h,CHAR_LENGTH(_big5 X'41FEA142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FEFE42')) AS h,CHAR_LENGTH(_big5 X'41FEFE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FEFF42')) AS h,CHAR_LENGTH(_big5 X'41FEFF42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FF3F42')) AS h,CHAR_LENGTH(_big5 X'41FF3F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FF4042')) AS h,CHAR_LENGTH(_big5 X'41FF4042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FF7E42')) AS h,CHAR_LENGTH(_big5 X'41FF7E42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FF7F42')) AS h,CHAR_LENGTH(_big5 X'41FF7F42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FF8042')) AS h,CHAR_LENGTH(_big5 X'41FF8042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FFA042')) AS h,CHAR_LENGTH(_big5 X'41FFA042') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FFA142')) AS h,CHAR_LENGTH(_big5 X'41FFA142') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FFFE42')) AS h,CHAR_LENGTH(_big5 X'41FFFE42') AS n", true
                     "SELECT HEX(REVERSE(_big5 X'41FFFF42')) AS h,CHAR_LENGTH(_big5 X'41FFFF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41803F42')) AS h,CHAR_LENGTH(_gbk X'41803F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41804042')) AS h,CHAR_LENGTH(_gbk X'41804042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41807E42')) AS h,CHAR_LENGTH(_gbk X'41807E42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41807F42')) AS h,CHAR_LENGTH(_gbk X'41807F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41808042')) AS h,CHAR_LENGTH(_gbk X'41808042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'4180A042')) AS h,CHAR_LENGTH(_gbk X'4180A042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'4180A142')) AS h,CHAR_LENGTH(_gbk X'4180A142') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'4180FE42')) AS h,CHAR_LENGTH(_gbk X'4180FE42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'4180FF42')) AS h,CHAR_LENGTH(_gbk X'4180FF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41813F42')) AS h,CHAR_LENGTH(_gbk X'41813F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41814042')) AS h,CHAR_LENGTH(_gbk X'41814042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41817E42')) AS h,CHAR_LENGTH(_gbk X'41817E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41817F42')) AS h,CHAR_LENGTH(_gbk X'41817F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41818042')) AS h,CHAR_LENGTH(_gbk X'41818042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'4181A042')) AS h,CHAR_LENGTH(_gbk X'4181A042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'4181A142')) AS h,CHAR_LENGTH(_gbk X'4181A142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'4181FE42')) AS h,CHAR_LENGTH(_gbk X'4181FE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'4181FF42')) AS h,CHAR_LENGTH(_gbk X'4181FF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A03F42')) AS h,CHAR_LENGTH(_gbk X'41A03F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A04042')) AS h,CHAR_LENGTH(_gbk X'41A04042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A07E42')) AS h,CHAR_LENGTH(_gbk X'41A07E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A07F42')) AS h,CHAR_LENGTH(_gbk X'41A07F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A08042')) AS h,CHAR_LENGTH(_gbk X'41A08042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A0A042')) AS h,CHAR_LENGTH(_gbk X'41A0A042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A0A142')) AS h,CHAR_LENGTH(_gbk X'41A0A142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A0FE42')) AS h,CHAR_LENGTH(_gbk X'41A0FE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A0FF42')) AS h,CHAR_LENGTH(_gbk X'41A0FF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A13F42')) AS h,CHAR_LENGTH(_gbk X'41A13F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A14042')) AS h,CHAR_LENGTH(_gbk X'41A14042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A17E42')) AS h,CHAR_LENGTH(_gbk X'41A17E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A17F42')) AS h,CHAR_LENGTH(_gbk X'41A17F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41A18042')) AS h,CHAR_LENGTH(_gbk X'41A18042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A1A042')) AS h,CHAR_LENGTH(_gbk X'41A1A042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A1A142')) AS h,CHAR_LENGTH(_gbk X'41A1A142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A1FE42')) AS h,CHAR_LENGTH(_gbk X'41A1FE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41A1FF42')) AS h,CHAR_LENGTH(_gbk X'41A1FF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41F93F42')) AS h,CHAR_LENGTH(_gbk X'41F93F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41F94042')) AS h,CHAR_LENGTH(_gbk X'41F94042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F97E42')) AS h,CHAR_LENGTH(_gbk X'41F97E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F97F42')) AS h,CHAR_LENGTH(_gbk X'41F97F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41F98042')) AS h,CHAR_LENGTH(_gbk X'41F98042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F9A042')) AS h,CHAR_LENGTH(_gbk X'41F9A042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F9A142')) AS h,CHAR_LENGTH(_gbk X'41F9A142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F9FE42')) AS h,CHAR_LENGTH(_gbk X'41F9FE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41F9FF42')) AS h,CHAR_LENGTH(_gbk X'41F9FF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FA3F42')) AS h,CHAR_LENGTH(_gbk X'41FA3F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FA4042')) AS h,CHAR_LENGTH(_gbk X'41FA4042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FA7E42')) AS h,CHAR_LENGTH(_gbk X'41FA7E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FA7F42')) AS h,CHAR_LENGTH(_gbk X'41FA7F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FA8042')) AS h,CHAR_LENGTH(_gbk X'41FA8042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FAA042')) AS h,CHAR_LENGTH(_gbk X'41FAA042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FAA142')) AS h,CHAR_LENGTH(_gbk X'41FAA142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FAFE42')) AS h,CHAR_LENGTH(_gbk X'41FAFE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FAFF42')) AS h,CHAR_LENGTH(_gbk X'41FAFF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FE3F42')) AS h,CHAR_LENGTH(_gbk X'41FE3F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FE4042')) AS h,CHAR_LENGTH(_gbk X'41FE4042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FE7E42')) AS h,CHAR_LENGTH(_gbk X'41FE7E42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FE7F42')) AS h,CHAR_LENGTH(_gbk X'41FE7F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FE8042')) AS h,CHAR_LENGTH(_gbk X'41FE8042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FEA042')) AS h,CHAR_LENGTH(_gbk X'41FEA042') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FEA142')) AS h,CHAR_LENGTH(_gbk X'41FEA142') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FEFE42')) AS h,CHAR_LENGTH(_gbk X'41FEFE42') AS n", false
                     "SELECT HEX(REVERSE(_gbk X'41FEFF42')) AS h,CHAR_LENGTH(_gbk X'41FEFF42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FF3F42')) AS h,CHAR_LENGTH(_gbk X'41FF3F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FF4042')) AS h,CHAR_LENGTH(_gbk X'41FF4042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FF7E42')) AS h,CHAR_LENGTH(_gbk X'41FF7E42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FF7F42')) AS h,CHAR_LENGTH(_gbk X'41FF7F42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FF8042')) AS h,CHAR_LENGTH(_gbk X'41FF8042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FFA042')) AS h,CHAR_LENGTH(_gbk X'41FFA042') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FFA142')) AS h,CHAR_LENGTH(_gbk X'41FFA142') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FFFE42')) AS h,CHAR_LENGTH(_gbk X'41FFFE42') AS n", true
                     "SELECT HEX(REVERSE(_gbk X'41FFFF42')) AS h,CHAR_LENGTH(_gbk X'41FFFF42') AS n", true ] |> List.indexed do
                   let step = Contract.query (sprintf "big5-gbk-boundaries-%d" index) sql
                   yield if rejected then step |> Contract.fails 1300 "HY000" else step
               for index, (charset, bytes, rejected) in
                   [ "ujis", "41804042", true
                     "ujis", "4180A042", true
                     "ujis", "4180A142", true
                     "ujis", "4180DF42", true
                     "ujis", "4180E042", true
                     "ujis", "4180FE42", true
                     "ujis", "4180FF42", true
                     "ujis", "418E4042", true
                     "ujis", "418EA042", false
                     "ujis", "418EA142", false
                     "ujis", "418EDF42", false
                     "ujis", "418EE042", true
                     "ujis", "418EFE42", true
                     "ujis", "418EFF42", true
                     "ujis", "418F4042", true
                     "ujis", "418FA042", true
                     "ujis", "418FA142", true
                     "ujis", "418FDF42", true
                     "ujis", "418FE042", true
                     "ujis", "418FFE42", true
                     "ujis", "418FFF42", true
                     "ujis", "41A04042", true
                     "ujis", "41A0A042", true
                     "ujis", "41A0A142", true
                     "ujis", "41A0DF42", true
                     "ujis", "41A0E042", true
                     "ujis", "41A0FE42", true
                     "ujis", "41A0FF42", true
                     "ujis", "41A14042", true
                     "ujis", "41A1A042", true
                     "ujis", "41A1A142", false
                     "ujis", "41A1DF42", false
                     "ujis", "41A1E042", false
                     "ujis", "41A1FE42", false
                     "ujis", "41A1FF42", true
                     "ujis", "41DF4042", true
                     "ujis", "41DFA042", true
                     "ujis", "41DFA142", false
                     "ujis", "41DFDF42", false
                     "ujis", "41DFE042", false
                     "ujis", "41DFFE42", false
                     "ujis", "41DFFF42", true
                     "ujis", "41E04042", true
                     "ujis", "41E0A042", true
                     "ujis", "41E0A142", false
                     "ujis", "41E0DF42", false
                     "ujis", "41E0E042", false
                     "ujis", "41E0FE42", false
                     "ujis", "41E0FF42", true
                     "ujis", "41FE4042", true
                     "ujis", "41FEA042", true
                     "ujis", "41FEA142", false
                     "ujis", "41FEDF42", false
                     "ujis", "41FEE042", false
                     "ujis", "41FEFE42", false
                     "ujis", "41FEFF42", true
                     "ujis", "41FF4042", true
                     "ujis", "41FFA042", true
                     "ujis", "41FFA142", true
                     "ujis", "41FFDF42", true
                     "ujis", "41FFE042", true
                     "ujis", "41FFFE42", true
                     "ujis", "41FFFF42", true
                     "euckr", "41804042", true
                     "euckr", "4180A042", true
                     "euckr", "4180A142", true
                     "euckr", "4180DF42", true
                     "euckr", "4180E042", true
                     "euckr", "4180FE42", true
                     "euckr", "4180FF42", true
                     "euckr", "418E4042", true
                     "euckr", "418EA042", false
                     "euckr", "418EA142", false
                     "euckr", "418EDF42", false
                     "euckr", "418EE042", false
                     "euckr", "418EFE42", false
                     "euckr", "418EFF42", true
                     "euckr", "418F4042", true
                     "euckr", "418FA042", false
                     "euckr", "418FA142", false
                     "euckr", "418FDF42", false
                     "euckr", "418FE042", false
                     "euckr", "418FFE42", false
                     "euckr", "418FFF42", true
                     "euckr", "41A04042", true
                     "euckr", "41A0A042", false
                     "euckr", "41A0A142", false
                     "euckr", "41A0DF42", false
                     "euckr", "41A0E042", false
                     "euckr", "41A0FE42", false
                     "euckr", "41A0FF42", true
                     "euckr", "41A14042", true
                     "euckr", "41A1A042", false
                     "euckr", "41A1A142", false
                     "euckr", "41A1DF42", false
                     "euckr", "41A1E042", false
                     "euckr", "41A1FE42", false
                     "euckr", "41A1FF42", true
                     "euckr", "41DF4042", true
                     "euckr", "41DFA042", false
                     "euckr", "41DFA142", false
                     "euckr", "41DFDF42", false
                     "euckr", "41DFE042", false
                     "euckr", "41DFFE42", false
                     "euckr", "41DFFF42", true
                     "euckr", "41E04042", true
                     "euckr", "41E0A042", false
                     "euckr", "41E0A142", false
                     "euckr", "41E0DF42", false
                     "euckr", "41E0E042", false
                     "euckr", "41E0FE42", false
                     "euckr", "41E0FF42", true
                     "euckr", "41FE4042", true
                     "euckr", "41FEA042", false
                     "euckr", "41FEA142", false
                     "euckr", "41FEDF42", false
                     "euckr", "41FEE042", false
                     "euckr", "41FEFE42", false
                     "euckr", "41FEFF42", true
                     "euckr", "41FF4042", true
                     "euckr", "41FFA042", true
                     "euckr", "41FFA142", true
                     "euckr", "41FFDF42", true
                     "euckr", "41FFE042", true
                     "euckr", "41FFFE42", true
                     "euckr", "41FFFF42", true
                     "gb2312", "41804042", true
                     "gb2312", "4180A042", true
                     "gb2312", "4180A142", true
                     "gb2312", "4180DF42", true
                     "gb2312", "4180E042", true
                     "gb2312", "4180FE42", true
                     "gb2312", "4180FF42", true
                     "gb2312", "418E4042", true
                     "gb2312", "418EA042", true
                     "gb2312", "418EA142", true
                     "gb2312", "418EDF42", true
                     "gb2312", "418EE042", true
                     "gb2312", "418EFE42", true
                     "gb2312", "418EFF42", true
                     "gb2312", "418F4042", true
                     "gb2312", "418FA042", true
                     "gb2312", "418FA142", true
                     "gb2312", "418FDF42", true
                     "gb2312", "418FE042", true
                     "gb2312", "418FFE42", true
                     "gb2312", "418FFF42", true
                     "gb2312", "41A04042", true
                     "gb2312", "41A0A042", true
                     "gb2312", "41A0A142", true
                     "gb2312", "41A0DF42", true
                     "gb2312", "41A0E042", true
                     "gb2312", "41A0FE42", true
                     "gb2312", "41A0FF42", true
                     "gb2312", "41A14042", true
                     "gb2312", "41A1A042", true
                     "gb2312", "41A1A142", false
                     "gb2312", "41A1DF42", false
                     "gb2312", "41A1E042", false
                     "gb2312", "41A1FE42", false
                     "gb2312", "41A1FF42", true
                     "gb2312", "41DF4042", true
                     "gb2312", "41DFA042", true
                     "gb2312", "41DFA142", false
                     "gb2312", "41DFDF42", false
                     "gb2312", "41DFE042", false
                     "gb2312", "41DFFE42", false
                     "gb2312", "41DFFF42", true
                     "gb2312", "41E04042", true
                     "gb2312", "41E0A042", true
                     "gb2312", "41E0A142", false
                     "gb2312", "41E0DF42", false
                     "gb2312", "41E0E042", false
                     "gb2312", "41E0FE42", false
                     "gb2312", "41E0FF42", true
                     "gb2312", "41FE4042", true
                     "gb2312", "41FEA042", true
                     "gb2312", "41FEA142", true
                     "gb2312", "41FEDF42", true
                     "gb2312", "41FEE042", true
                     "gb2312", "41FEFE42", true
                     "gb2312", "41FEFF42", true
                     "gb2312", "41FF4042", true
                     "gb2312", "41FFA042", true
                     "gb2312", "41FFA142", true
                     "gb2312", "41FFDF42", true
                     "gb2312", "41FFE042", true
                     "gb2312", "41FFFE42", true
                     "gb2312", "41FFFF42", true
                     "euckr", "41804042", true
                     "euckr", "41807F42", true
                     "euckr", "41808042", true
                     "euckr", "41808142", true
                     "euckr", "41809F42", true
                     "euckr", "4180A042", true
                     "euckr", "4180FE42", true
                     "euckr", "4180FF42", true
                     "euckr", "41814042", true
                     "euckr", "41817F42", true
                     "euckr", "41818042", true
                     "euckr", "41818142", false
                     "euckr", "41819F42", false
                     "euckr", "4181A042", false
                     "euckr", "4181FE42", false
                     "euckr", "4181FF42", true
                     "euckr", "41824042", true
                     "euckr", "41827F42", true
                     "euckr", "41828042", true
                     "euckr", "41828142", false
                     "euckr", "41829F42", false
                     "euckr", "4182A042", false
                     "euckr", "4182FE42", false
                     "euckr", "4182FF42", true
                     "euckr", "419F4042", true
                     "euckr", "419F7F42", true
                     "euckr", "419F8042", true
                     "euckr", "419F8142", false
                     "euckr", "419F9F42", false
                     "euckr", "419FA042", false
                     "euckr", "419FFE42", false
                     "euckr", "419FFF42", true
                     "euckr", "41FF4042", true
                     "euckr", "41FF7F42", true
                     "euckr", "41FF8042", true
                     "euckr", "41FF8142", true
                     "euckr", "41FF9F42", true
                     "euckr", "41FFA042", true
                     "euckr", "41FFFE42", true
                     "euckr", "41FFFF42", true
                     "gb2312", "41A0A042", true
                     "gb2312", "41A0A142", true
                     "gb2312", "41A0FE42", true
                     "gb2312", "41A0FF42", true
                     "gb2312", "41A1A042", true
                     "gb2312", "41A1A142", false
                     "gb2312", "41A1FE42", false
                     "gb2312", "41A1FF42", true
                     "gb2312", "41F7A042", true
                     "gb2312", "41F7A142", false
                     "gb2312", "41F7FE42", false
                     "gb2312", "41F7FF42", true
                     "gb2312", "41F8A042", true
                     "gb2312", "41F8A142", true
                     "gb2312", "41F8FE42", true
                     "gb2312", "41F8FF42", true
                     "gb2312", "41FDA042", true
                     "gb2312", "41FDA142", true
                     "gb2312", "41FDFE42", true
                     "gb2312", "41FDFF42", true
                     "gb2312", "41FEA042", true
                     "gb2312", "41FEA142", true
                     "gb2312", "41FEFE42", true
                     "gb2312", "41FEFF42", true
                     "ujis", "418FA0A042", true
                     "ujis", "418FA0A142", true
                     "ujis", "418FA0FE42", true
                     "ujis", "418FA0FF42", true
                     "ujis", "418FA1A042", true
                     "ujis", "418FA1A142", false
                     "ujis", "418FA1FE42", false
                     "ujis", "418FA1FF42", true
                     "ujis", "418FFEA042", true
                     "ujis", "418FFEA142", false
                     "ujis", "418FFEFE42", false
                     "ujis", "418FFEFF42", true
                     "ujis", "418FFFA042", true
                     "ujis", "418FFFA142", true
                     "ujis", "418FFFFE42", true
                     "ujis", "418FFFFF42", true
                     "ujis", "418E9F42", true
                     "ujis", "418EA042", false
                     "ujis", "418EA142", false
                     "ujis", "418EDF42", false
                     "ujis", "418EE042", true ] |> List.indexed do
                   let literal = "_" + charset + " X'" + bytes + "'"
                   let sql = "SELECT HEX(REVERSE(" + literal + ")) AS h,CHAR_LENGTH(" + literal + ") AS n"
                   let step = Contract.query (sprintf "euc-boundaries-%d" index) sql
                   yield if rejected then step |> Contract.fails 1300 "HY000" else step
               for index, (sql, rejected) in
                   [ "SELECT HEX(REVERSE(_gb18030 X'41814042')) AS h,CHAR_LENGTH(_gb18030 X'41814042') AS n,HEX(LEFT(_gb18030 X'41814042',2)) AS l,HEX(SUBSTRING(_gb18030 X'41814042',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'41817F42')) AS h,CHAR_LENGTH(_gb18030 X'41817F42') AS n,HEX(LEFT(_gb18030 X'41817F42',2)) AS l,HEX(SUBSTRING(_gb18030 X'41817F42',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'41818042')) AS h,CHAR_LENGTH(_gb18030 X'41818042') AS n,HEX(LEFT(_gb18030 X'41818042',2)) AS l,HEX(SUBSTRING(_gb18030 X'41818042',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'4181FE42')) AS h,CHAR_LENGTH(_gb18030 X'4181FE42') AS n,HEX(LEFT(_gb18030 X'4181FE42',2)) AS l,HEX(SUBSTRING(_gb18030 X'4181FE42',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'4181FF42')) AS h,CHAR_LENGTH(_gb18030 X'4181FF42') AS n,HEX(LEFT(_gb18030 X'4181FF42',2)) AS l,HEX(SUBSTRING(_gb18030 X'4181FF42',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'418130813042')) AS h,CHAR_LENGTH(_gb18030 X'418130813042') AS n,HEX(LEFT(_gb18030 X'418130813042',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130813042',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'418130813942')) AS h,CHAR_LENGTH(_gb18030 X'418130813942') AS n,HEX(LEFT(_gb18030 X'418130813942',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130813942',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'418130813A42')) AS h,CHAR_LENGTH(_gb18030 X'418130813A42') AS n,HEX(LEFT(_gb18030 X'418130813A42',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130813A42',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'418130803042')) AS h,CHAR_LENGTH(_gb18030 X'418130803042') AS n,HEX(LEFT(_gb18030 X'418130803042',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130803042',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'418130FE3942')) AS h,CHAR_LENGTH(_gb18030 X'418130FE3942') AS n,HEX(LEFT(_gb18030 X'418130FE3942',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130FE3942',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'418130FF3042')) AS h,CHAR_LENGTH(_gb18030 X'418130FF3042') AS n,HEX(LEFT(_gb18030 X'418130FF3042',2)) AS l,HEX(SUBSTRING(_gb18030 X'418130FF3042',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'419030813042')) AS h,CHAR_LENGTH(_gb18030 X'419030813042') AS n,HEX(LEFT(_gb18030 X'419030813042',2)) AS l,HEX(SUBSTRING(_gb18030 X'419030813042',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'41FE39FE3942')) AS h,CHAR_LENGTH(_gb18030 X'41FE39FE3942') AS n,HEX(LEFT(_gb18030 X'41FE39FE3942',2)) AS l,HEX(SUBSTRING(_gb18030 X'41FE39FE3942',2,1)) AS s", false
                     "SELECT HEX(REVERSE(_gb18030 X'41FF30813042')) AS h,CHAR_LENGTH(_gb18030 X'41FF30813042') AS n,HEX(LEFT(_gb18030 X'41FF30813042',2)) AS l,HEX(SUBSTRING(_gb18030 X'41FF30813042',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'418042')) AS h,CHAR_LENGTH(_gb18030 X'418042') AS n,HEX(LEFT(_gb18030 X'418042',2)) AS l,HEX(SUBSTRING(_gb18030 X'418042',2,1)) AS s", true
                     "SELECT HEX(REVERSE(_gb18030 X'4181308142')) AS h,CHAR_LENGTH(_gb18030 X'4181308142') AS n,HEX(LEFT(_gb18030 X'4181308142',2)) AS l,HEX(SUBSTRING(_gb18030 X'4181308142',2,1)) AS s", true
                     "SELECT HEX(LEFT(_gb18030 X'419030813042',2)) AS h", false
                     "SELECT HEX(RIGHT(_gb18030 X'419030813042',2)) AS h", false
                     "SELECT HEX(SUBSTRING(_gb18030 X'419030813042',2,1)) AS h", false
                     "SELECT HEX(SUBSTRING(_gb18030 X'419030813042',-2,1)) AS h", false
                     "SELECT HEX(SUBSTRING(_gb18030 X'419030813042',2)) AS h", false
                     "SELECT HEX(SUBSTRING(_gb18030 X'419030813042',0,2)) AS h", false
                     "SELECT HEX(SUBSTRING(_gb18030 X'419030813042',-4,1)) AS h", false
                     "SELECT HEX(LEFT(_gb18030 X'419030813042',0)) AS h", false
                     "SELECT HEX(RIGHT(_gb18030 X'419030813042',-1)) AS h", false
                     "SELECT HEX(LEFT(_utf8mb4 X'41F090808042',2)) AS h", false
                     "SELECT HEX(RIGHT(_utf8mb4 X'41F090808042',2)) AS h", false
                     "SELECT HEX(SUBSTRING(_utf8mb4 X'41F090808042',2,1)) AS h", false
                     "SELECT HEX(SUBSTRING(_utf8mb4 X'41F090808042',-2,1)) AS h", false
                     "SELECT HEX(SUBSTRING(_utf8mb4 X'41F090808042',2)) AS h", false
                     "SELECT HEX(SUBSTRING(_utf8mb4 X'41F090808042',0,2)) AS h", false
                     "SELECT HEX(SUBSTRING(_utf8mb4 X'41F090808042',-4,1)) AS h", false
                     "SELECT HEX(LEFT(_utf8mb4 X'41F090808042',0)) AS h", false
                     "SELECT HEX(RIGHT(_utf8mb4 X'41F090808042',-1)) AS h", false ] |> List.indexed do
                   let step = Contract.query (sprintf "gb18030-slicing-%d" index) sql
                   yield if rejected then step |> Contract.fails 1300 "HY000" else step
               for index, sql in
                   [ "SELECT HEX(_sjis'é') AS h,LENGTH(_sjis'é') AS b,CHAR_LENGTH(_sjis'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_sjis'😀') AS h,LENGTH(_sjis'😀') AS b,CHAR_LENGTH(_sjis'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_cp932'é') AS h,LENGTH(_cp932'é') AS b,CHAR_LENGTH(_cp932'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_cp932'😀') AS h,LENGTH(_cp932'😀') AS b,CHAR_LENGTH(_cp932'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_big5'é') AS h,LENGTH(_big5'é') AS b,CHAR_LENGTH(_big5'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_big5'😀') AS h,LENGTH(_big5'😀') AS b,CHAR_LENGTH(_big5'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gbk'é') AS h,LENGTH(_gbk'é') AS b,CHAR_LENGTH(_gbk'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gbk'😀') AS h,LENGTH(_gbk'😀') AS b,CHAR_LENGTH(_gbk'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_ujis'é') AS h,LENGTH(_ujis'é') AS b,CHAR_LENGTH(_ujis'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_ujis'😀') AS h,LENGTH(_ujis'😀') AS b,CHAR_LENGTH(_ujis'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_euckr'é') AS h,LENGTH(_euckr'é') AS b,CHAR_LENGTH(_euckr'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_euckr'😀') AS h,LENGTH(_euckr'😀') AS b,CHAR_LENGTH(_euckr'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gb2312'é') AS h,LENGTH(_gb2312'é') AS b,CHAR_LENGTH(_gb2312'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gb2312'😀') AS h,LENGTH(_gb2312'😀') AS b,CHAR_LENGTH(_gb2312'😀') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gb18030'é') AS h,LENGTH(_gb18030'é') AS b,CHAR_LENGTH(_gb18030'é') AS n;SHOW WARNINGS"
                     "SELECT HEX(_gb18030'😀') AS h,LENGTH(_gb18030'😀') AS b,CHAR_LENGTH(_gb18030'😀') AS n;SHOW WARNINGS" ] |> List.indexed do
                   yield Contract.execute (sprintf "quoted-legacy-charset-%d" index) "SET NAMES utf8mb4"
                   for step, fragment in sql.Split(';') |> Array.indexed do
                       yield Contract.query (sprintf "quoted-legacy-%d-%d" index step) fragment
               yield Contract.execute "encoded-storage-mode" "SET sql_mode=DEFAULT" |]
          Cleanup = [||]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential"; "error-contract" |] |] }

    let private exactErrors =
        { Name = "syntax-error-contracts"
          Setup = [||]
          Steps =
            [| Contract.query "unterminated-expression" "SELECT (1" |> Contract.fails 1064 "42000"
               Contract.query "missing-table-reference" "SELECT 1 FROM" |> Contract.fails 1064 "42000"
               Contract.query "dash-is-an-operator" "SELECT 1--missing" |> Contract.fails 1054 "42S22" |]
          Cleanup = [||]
          Coverage = [| "statement:select", [| "error-contract" |]; "syntax:comments", [| "error-contract" |] |] }

    let private noDirInCreate =
        { Name = "no-dir-in-create"
          Setup = [| "DROP TABLE IF EXISTS contract_no_dir" |]
          Steps =
            [| Contract.execute "enable-portable-create" "SET SESSION sql_mode = 'NO_DIR_IN_CREATE'"
               Contract.execute
                   "create-with-ignored-directories"
                   "CREATE TABLE contract_no_dir (id INT) INDEX DIRECTORY='/first' DATA DIRECTORY='/data' INDEX DIRECTORY='/index' PARTITION BY HASH(id) PARTITIONS 2"
               Contract.query "directory-warning-contract" "SHOW WARNINGS"
               Contract.query "ignored-directory-table-exists" "SELECT COUNT(*) FROM contract_no_dir" |]
          Cleanup = [| "SET SESSION sql_mode = DEFAULT"; "DROP TABLE IF EXISTS contract_no_dir" |]
          Coverage =
            [| "statement:create_table", [| "parser"; "text-differential" |]
               "sql-mode:no_dir_in_create", [| "execution"; "diagnostics"; "text-differential" |] |] }

    let private prepared =
        { Name = "prepared-binary-protocol"
          Setup = [| "DROP TABLE IF EXISTS contract_prepared"; "CREATE TABLE contract_prepared (id INT PRIMARY KEY, label VARCHAR(20))"; "INSERT INTO contract_prepared VALUES (1, 'one'), (2, 'two')" |]
          Steps =
            [| Contract.preparedQuery
                   "typed-parameters"
                   "SELECT id, label FROM contract_prepared WHERE id >= ? AND label <> ? ORDER BY id"
                   [| box 1; box "one" |]
               Contract.preparedQuery
                   "prepared-cte"
                   "WITH selected AS (SELECT id FROM contract_prepared WHERE id = ?) SELECT id FROM selected"
                   [| box 2 |]
               Contract.preparedQuery
                   "prepared-comments"
                   "SELECT /* before */ id FROM contract_prepared WHERE id /* operator */ = /* parameter */ ?"
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-limit-integer"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT ?"
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-limit-integral-text"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT ?"
                   [| box "1" |]
               Contract.preparedQuery
                   "prepared-limit-fractional-text"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT ?"
                   [| box "1.9" |]
               Contract.preparedQuery
                   "prepared-limit-nonnumeric-text"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT ?"
                   [| box "abc" |]
               Contract.preparedQuery
                   "prepared-offset-rejects-double"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT 1 OFFSET ?"
                   [| box 1.0 |]
               |> Contract.fails 1210 "HY000"
               Contract.preparedExecute
                   "prepared-update-limit-rejects-decimal"
                   "UPDATE contract_prepared SET label = 'changed' LIMIT ?"
                   [| box 1M |]
               |> Contract.fails 1210 "HY000"
               Contract.preparedExecute
                   "prepared-delete-limit-null"
                   "DELETE FROM contract_prepared LIMIT ?"
                   [| box DBNull.Value |]
               Contract.preparedQuery
                   "prepared-limit-negative"
                   "SELECT id FROM contract_prepared ORDER BY id LIMIT ?"
                   [| box -1 |]
               |> Contract.fails 1690 "22003" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_prepared" |]
          Coverage =
            [| "statement:select", [| "prepared-protocol" |]
               "column-type:t_int", [| "prepared-protocol" |]
               "column-type:t_varchar", [| "prepared-protocol" |] |] }

    let private columnTypes =
        { Name = "column-type-roundtrip"
          Setup = [| "DROP TABLE IF EXISTS contract_types"; TypeMatrix.createTable "contract_types"; TypeMatrix.insert "contract_types" |]
          Steps =
            [| Contract.query "text-type-row" (TypeMatrix.select "contract_types" "1")
               Contract.preparedQuery
                   "prepared-numeric-row"
                   (TypeMatrix.selectColumns
                       "contract_types"
                       "c_tiny, c_bool, c_small, c_medium, c_int, c_big, c_bit, c_decimal, c_double, c_float, c_year"
                       "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-text-binary-row"
                   (TypeMatrix.selectColumns
                       "contract_types"
                       "c_char, c_varchar, c_tinytext, c_text, c_mediumtext, c_longtext, c_binary, c_varbinary, c_tinyblob, c_blob, c_mediumblob, c_longblob, c_enum, c_set, c_json"
                       "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-temporal-row"
                   (TypeMatrix.selectColumns "contract_types" "c_date, c_datetime, c_timestamp, c_time" "?")
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-geometry-row"
                   (TypeMatrix.selectColumns "contract_types" "c_geometry" "?")
                   [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_types" |]
          Coverage =
            TypeMatrix.capabilities
            |> Array.map (fun name -> "column-type:" + name, [| "parser"; "text-differential"; "prepared-protocol" |]) }

    let private generatedFunctionFamilies =
        let spec name functions text prepared parameters nullPrepared wrongArityExpressions =
            { Name = name
              Functions = functions
              TextExpression = text
              PreparedExpression = prepared
              Parameters = parameters
              ResultTypes = functions
              NullPrepared = nullPrepared
              WrongArityExpressions = wrongArityExpressions }

        let shapeSpec name functions text prepared parameters nullPrepared wrongArityExpressions =
            { spec name functions text prepared parameters nullPrepared wrongArityExpressions with ResultTypes = [||] }

        let nullValue = box DBNull.Value

        let establishedSpecs =
            [| spec
                   "aes-roundtrip"
                   [| "AES_ENCRYPT"; "AES_DECRYPT" |]
                   "AES_ENCRYPT('hello', 'secret'), AES_DECRYPT(AES_ENCRYPT('hello', 'secret'), 'secret')"
                   "AES_ENCRYPT(?, ?), AES_DECRYPT(AES_ENCRYPT(?, ?), ?)"
                   [| box "hello"; box "secret"; box "hello"; box "secret"; box "secret" |]
                   (Some([| "AES_ENCRYPT" |], "AES_ENCRYPT(?, ?)", [| nullValue; box "secret" |]))
                   [| "AES_ENCRYPT", "AES_ENCRYPT('hello')"; "AES_DECRYPT", "AES_DECRYPT('hello')" |]
               spec
                   "uuid-binary-roundtrip"
                   [| "UUID_TO_BIN"; "BIN_TO_UUID" |]
                   "UUID_TO_BIN('6ccd780c-baba-1026-9564-5b8c656024db', 1), BIN_TO_UUID(UUID_TO_BIN('6ccd780c-baba-1026-9564-5b8c656024db', 1), 1)"
                   "UUID_TO_BIN(?, ?), BIN_TO_UUID(UUID_TO_BIN(?, ?), ?)"
                   [| box "6ccd780c-baba-1026-9564-5b8c656024db"
                      box 1
                      box "6ccd780c-baba-1026-9564-5b8c656024db"
                      box 1
                      box 1 |]
                   (Some([| "UUID_TO_BIN" |], "UUID_TO_BIN(?)", [| nullValue |]))
                   [| "UUID_TO_BIN", "UUID_TO_BIN()"; "BIN_TO_UUID", "BIN_TO_UUID()" |]
               spec
                   "uuid-validation"
                   [| "IS_UUID" |]
                   "IS_UUID('6ccd780c-baba-1026-9564-5b8c656024db')"
                   "IS_UUID(?)"
                   [| box "6ccd780c-baba-1026-9564-5b8c656024db" |]
                   (Some([| "IS_UUID" |], "IS_UUID(?)", [| nullValue |]))
                   [| "IS_UUID", "IS_UUID()" |]
               spec
                   "ipv4-roundtrip"
                   [| "INET_ATON"; "INET_NTOA" |]
                   "INET_ATON('192.0.2.7'), INET_NTOA(3221225991)"
                   "INET_ATON(?), INET_NTOA(?)"
                   [| box "192.0.2.7"; box 3221225991L |]
                   (Some([| "INET_ATON" |], "INET_ATON(?)", [| nullValue |]))
                   [| "INET_ATON", "INET_ATON()"; "INET_NTOA", "INET_NTOA()" |]
               spec
                   "ipv6-roundtrip"
                   [| "INET6_ATON"; "INET6_NTOA" |]
                   "INET6_NTOA(INET6_ATON('2001:db8::7'))"
                   "INET6_NTOA(INET6_ATON(?))"
                   [| box "2001:db8::7" |]
                   (Some([| "INET6_ATON" |], "INET6_ATON(?)", [| nullValue |]))
                   [| "INET6_ATON", "INET6_ATON()"; "INET6_NTOA", "INET6_NTOA()" |]
               spec
                   "ip-predicates"
                   [| "IS_IPV4"; "IS_IPV6"; "IS_IPV4_COMPAT"; "IS_IPV4_MAPPED" |]
                   "IS_IPV4('192.0.2.7'), IS_IPV6('2001:db8::7'), IS_IPV4_COMPAT(INET6_ATON('::192.0.2.7')), IS_IPV4_MAPPED(INET6_ATON('::ffff:192.0.2.7'))"
                   "IS_IPV4(?), IS_IPV6(?), IS_IPV4_COMPAT(INET6_ATON(?)), IS_IPV4_MAPPED(INET6_ATON(?))"
                   [| box "192.0.2.7"; box "2001:db8::7"; box "::192.0.2.7"; box "::ffff:192.0.2.7" |]
                   (Some([| "IS_IPV4" |], "IS_IPV4(?)", [| nullValue |]))
                   [| "IS_IPV4", "IS_IPV4()"
                      "IS_IPV6", "IS_IPV6()"
                      "IS_IPV4_COMPAT", "IS_IPV4_COMPAT()"
                      "IS_IPV4_MAPPED", "IS_IPV4_MAPPED()" |]
               spec
                   "json-value"
                   [| "JSON_VALUE" |]
                   "JSON_VALUE('{\"a\":7}', '$.a')"
                   "JSON_VALUE(JSON_EXTRACT(?, '$'), '$.a')"
                   [| box "{\"a\":7}" |]
                   (Some([| "JSON_VALUE" |], "JSON_VALUE(JSON_EXTRACT(?, '$'), '$.a')", [| nullValue |]))
                   [||]
               spec
                   "json-member-of"
                   [| "JSON_MEMBER_OF" |]
                   "2 MEMBER OF('[1,2]')"
                   "? MEMBER OF(?)"
                   [| box 2; box "[1,2]" |]
                   (Some([| "JSON_MEMBER_OF" |], "? MEMBER OF(?)", [| nullValue; box "[1,2]" |]))
                   [||]
               spec
                   "json-search"
                   [| "JSON_SEARCH" |]
                   "JSON_SEARCH('{\"a\":\"needle\"}', 'one', 'needle')"
                   "JSON_SEARCH(?, 'one', ?)"
                   [| box "{\"a\":\"needle\"}"; box "needle" |]
                   (Some([| "JSON_SEARCH" |], "JSON_SEARCH(?, 'one', ?)", [| nullValue; box "needle" |]))
                   [| "JSON_SEARCH", "JSON_SEARCH('{}', 'one')" |]
               spec
                   "calendar-names"
                   [| "DAYNAME"; "MONTHNAME" |]
                   "DAYNAME('2024-02-29'), MONTHNAME('2024-02-29')"
                   "DAYNAME(?), MONTHNAME(?)"
                   [| box "2024-02-29"; box "2024-02-29" |]
                   (Some([| "DAYNAME"; "MONTHNAME" |], "DAYNAME(?), MONTHNAME(?)", [| nullValue; nullValue |]))
                   [| "DAYNAME", "DAYNAME()"; "MONTHNAME", "MONTHNAME()" |]
               spec
                   "temporal-formats"
                   [| "GET_FORMAT" |]
                   "GET_FORMAT(DATE, 'EUR')"
                   "GET_FORMAT(DATE, ?)"
                   [| box "EUR" |]
                   (Some([| "GET_FORMAT" |], "GET_FORMAT(DATE, ?)", [| nullValue |]))
                   [||]
               spec
                   "period-arithmetic"
                   [| "PERIOD_ADD"; "PERIOD_DIFF" |]
                   "PERIOD_ADD(202312, 2), PERIOD_DIFF(202402, 202312)"
                   "PERIOD_ADD(?, ?), PERIOD_DIFF(?, ?)"
                   [| box 202312; box 2; box 202402; box 202312 |]
                   (Some([| "PERIOD_ADD" |], "PERIOD_ADD(?, ?)", [| nullValue; box 2 |]))
                   [| "PERIOD_ADD", "PERIOD_ADD(202312)"; "PERIOD_DIFF", "PERIOD_DIFF(202402)" |]
               shapeSpec
                   "cotangent"
                   [| "COT" |]
                   "ABS(COT(1) - 0.6420926159343306) < 0.000000000001"
                   "ABS(COT(?) - 0.6420926159343306) < 0.000000000001"
                   [| box 1 |]
                   (Some([| "COT" |], "COT(?)", [| nullValue |]))
                   [| "COT", "COT()" |]
               spec
                   "named-constant"
                   [| "NAME_CONST" |]
                   "NAME_CONST('answer', 42)"
                   "NAME_CONST('answer', 42) + ?"
                   [| box 0 |]
                   (Some([| "NAME_CONST" |], "NAME_CONST('answer', NULL)", [||]))
                   [| "NAME_CONST", "NAME_CONST('answer')" |]
               shapeSpec
                   "random-bytes-shape"
                   [| "RANDOM_BYTES" |]
                   "LENGTH(RANDOM_BYTES(8))"
                   "LENGTH(RANDOM_BYTES(?))"
                   [| box 8 |]
                   None
                   [| "RANDOM_BYTES", "RANDOM_BYTES()" |]
               shapeSpec
                   "random-shape"
                   [| "RAND" |]
                   "RAND(7) BETWEEN 0 AND 1"
                   "RAND(?) BETWEEN 0 AND 1"
                   [| box 7 |]
                   None
                   [| "RAND", "RAND(1, 2)" |]
               shapeSpec
                   "uuid-shape"
                   [| "UUID"; "UUID_SHORT" |]
                   "IS_UUID(UUID()), UUID_SHORT() > 0"
                   "IS_UUID(UUID()), UUID_SHORT() > ?"
                   [| box 0 |]
                   None
                   [| "UUID", "UUID(1)"; "UUID_SHORT", "UUID_SHORT(1)" |]
               shapeSpec
                   "current-temporal-aliases"
                   [| "CURDATE"; "CURRENT_DATE"; "CURTIME"; "CURRENT_TIME"; "NOW"; "CURRENT_TIMESTAMP"; "LOCALTIME"
                      "LOCALTIMESTAMP"; "UTC_DATE"; "UTC_TIME"; "UTC_TIMESTAMP"; "SYSDATE" |]
                   "CURDATE() = CURRENT_DATE(), CURTIME() = CURRENT_TIME(), NOW() = CURRENT_TIMESTAMP(), LOCALTIME() = LOCALTIMESTAMP(), UTC_DATE() = DATE(UTC_TIMESTAMP()), UTC_TIME() = TIME(UTC_TIMESTAMP()), SYSDATE() IS NOT NULL"
                   "CURDATE() = CURRENT_DATE(), CURTIME() = CURRENT_TIME(), NOW() = CURRENT_TIMESTAMP(), LOCALTIME() = LOCALTIMESTAMP(), UTC_DATE() = DATE(UTC_TIMESTAMP()), UTC_TIME() = TIME(UTC_TIMESTAMP()), SYSDATE() IS NOT NULL"
                   [||]
                   None
                   [||] |]

        let numericSpecs =
            [| spec
                   "numeric-unary"
                   [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "SQRT"; "SIGN" |]
                   "ABS(-2), CEIL(1.2), CEILING(1.2), FLOOR(1.8), SQRT(4), SIGN(-2)"
                   "ABS(?), CEIL(?), CEILING(?), FLOOR(?), SQRT(?), SIGN(?)"
                   [| box -2; box 1.2M; box 1.2M; box 1.8M; box 4; box -2 |]
                   (Some(
                       [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "SQRT"; "SIGN" |],
                       "ABS(?), CEIL(?), CEILING(?), FLOOR(?), SQRT(?), SIGN(?)",
                       Array.create 6 nullValue
                   ))
                   [| "ABS", "ABS()"
                      "CEIL", "CEIL()"
                      "CEILING", "CEILING()"
                      "FLOOR", "FLOOR()"
                      "SQRT", "SQRT()"
                      "SIGN", "SIGN()" |]
               spec
                   "numeric-transcendental"
                   [| "LOG"; "LN"; "LOG2"; "LOG10"; "EXP"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"
                      "ATAN2"; "DEGREES"; "RADIANS" |]
                   "LOG(1), LN(1), LOG2(8), LOG10(100), EXP(0), SIN(0), COS(0), TAN(0), ASIN(0), ACOS(1), ATAN(0), ATAN2(0, 1), DEGREES(0), RADIANS(0)"
                   "LOG(?), LN(?), LOG2(?), LOG10(?), EXP(?), SIN(?), COS(?), TAN(?), ASIN(?), ACOS(?), ATAN(?), ATAN2(?, ?), DEGREES(?), RADIANS(?)"
                   [| box 1
                      box 1
                      box 8
                      box 100
                      box 0
                      box 0
                      box 0
                      box 0
                      box 0
                      box 1
                      box 0
                      box 0
                      box 1
                      box 0
                      box 0 |]
                   (Some(
                       [| "LOG"; "LN"; "LOG2"; "LOG10"; "EXP"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"
                          "ATAN2"; "DEGREES"; "RADIANS" |],
                       "LOG(?), LN(?), LOG2(?), LOG10(?), EXP(?), SIN(?), COS(?), TAN(?), ASIN(?), ACOS(?), ATAN(?), ATAN2(?, ?), DEGREES(?), RADIANS(?)",
                       Array.create 15 nullValue
                   ))
                   [| "LN", "LN()"
                      "LOG2", "LOG2()"
                      "LOG10", "LOG10()"
                      "EXP", "EXP()"
                      "SIN", "SIN()"
                      "COS", "COS()"
                      "TAN", "TAN()"
                      "ASIN", "ASIN()"
                      "ACOS", "ACOS()"
                      "ATAN", "ATAN()"
                      "ATAN2", "ATAN2()"
                      "DEGREES", "DEGREES()"
                      "RADIANS", "RADIANS()" |]
               spec
                   "numeric-rounding-and-power"
                   [| "POW"; "POWER"; "ROUND"; "TRUNCATE"; "MOD" |]
                   "POW(2, 3), POWER(2, 3), ROUND(1.25, 1), TRUNCATE(1.29, 1), MOD(7, 3)"
                   "POW(2, ?), POWER(2, ?), ROUND(1.25, ?), TRUNCATE(1.29, ?), MOD(7, ?)"
                   [| box 3; box 3; box 1; box 1; box 3 |]
                   (Some(
                       [| "POW"; "POWER"; "ROUND"; "TRUNCATE"; "MOD" |],
                       "POW(?, ?), POWER(?, ?), ROUND(?, ?), TRUNCATE(?, ?), MOD(?, ?)",
                       Array.create 10 nullValue
                   ))
                   [| "POW", "POW(2)"
                      "POWER", "POWER(2)"
                      "ROUND", "ROUND()" |]
               spec
                   "numeric-selection"
                   [| "GREATEST"; "LEAST"; "NULLIF" |]
                   "GREATEST(1, 3, 2), LEAST(1, 3, 2), NULLIF(1, 1)"
                   "GREATEST(?, ?, ?), LEAST(?, ?, ?), NULLIF(?, ?)"
                   [| box 1; box 3; box 2; box 1; box 3; box 2; box 1; box 1 |]
                   (Some(
                       [| "GREATEST"; "LEAST"; "NULLIF" |],
                       "GREATEST(?, 2), LEAST(?, 2), NULLIF(?, 1)",
                       Array.create 3 nullValue
                   ))
                   [| "GREATEST", "GREATEST()"; "LEAST", "LEAST()" |]
               spec
                   "numeric-null-predicate"
                   [| "ISNULL" |]
                   "ISNULL(NULL)"
                   "ISNULL(?)"
                   [| box 1 |]
                   (Some([| "ISNULL" |], "ISNULL(?)", [| nullValue |]))
                   [| "ISNULL", "ISNULL()" |]
               spec
                   "numeric-base-and-bits"
                   [| "CONV"; "BIN"; "BIT_COUNT"; "OCT"; "CRC32" |]
                   "CONV('ff', 16, 10), BIN(5), BIT_COUNT(7), OCT(8), CRC32('abc')"
                   "CONV(?, ?, ?), BIN(?), BIT_COUNT(?), OCT(?), CRC32(?)"
                   [| box "ff"; box 16; box 10; box 5; box 7; box 8; box "abc" |]
                   (Some(
                       [| "CONV"; "BIN"; "BIT_COUNT"; "OCT"; "CRC32" |],
                       "CONV(?, 16, 10), BIN(?), BIT_COUNT(?), OCT(?), CRC32(?)",
                       Array.create 5 nullValue
                   ))
                   [| "CONV", "CONV('ff', 16)"
                      "BIN", "BIN()"
                      "BIT_COUNT", "BIT_COUNT()"
                      "OCT", "OCT()"
                      "CRC32", "CRC32()" |]
               spec
                   "cotangent-null-result"
                   [| "COT" |]
                   "COT(NULL)"
                   "COT(?)"
                   [| nullValue |]
                   (Some([| "COT" |], "COT(?)", [| nullValue |]))
                   [||] |]

        let stringSpecs =
            [| spec
                   "string-case-and-length"
                   [| "UPPER"; "LOWER"; "LENGTH"; "CHAR_LENGTH" |]
                   "UPPER('Abc'), LOWER('AbC'), LENGTH('blå'), CHAR_LENGTH('blå')"
                   "UPPER(?), LOWER(?), LENGTH(?), CHAR_LENGTH(?)"
                   [| box "Abc"; box "AbC"; box "blå"; box "blå" |]
                   (Some(
                       [| "UPPER"; "LOWER"; "LENGTH"; "CHAR_LENGTH" |],
                       "UPPER(?), LOWER(?), LENGTH(?), CHAR_LENGTH(?)",
                       [| nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "UPPER", "UPPER()"
                      "LOWER", "LOWER()"
                      "LENGTH", "LENGTH()"
                      "CHAR_LENGTH", "CHAR_LENGTH()" |]
               spec
                   "string-byte-functions"
                   [| "ASCII"; "ORD"; "HEX"; "UNHEX"; "MD5"; "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64" |]
                   "ASCII('A'), ORD('Å'), HEX('A'), UNHEX('41'), MD5('abc'), SHA1('abc'), SHA2('abc', 256), TO_BASE64('abc'), FROM_BASE64('YWJj')"
                   "ASCII(?), ORD(?), HEX(?), UNHEX(?), MD5(?), SHA1(?), SHA2(?, ?), TO_BASE64(?), FROM_BASE64(?)"
                   [| box "A"
                      box "Å"
                      box "A"
                      box "41"
                      box "abc"
                      box "abc"
                      box "abc"
                      box 256
                      box "abc"
                      box "YWJj" |]
                   (Some(
                       [| "ASCII"; "ORD"; "HEX"; "UNHEX"; "MD5"; "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64" |],
                       "ASCII(?), ORD(?), HEX(?), UNHEX(?), MD5(?), SHA1(?), SHA2(?, 256), TO_BASE64(?), FROM_BASE64(?)",
                       [| nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue
                          nullValue |]
                   ))
                   [| "ORD", "ORD()"
                      "HEX", "HEX()"
                      "UNHEX", "UNHEX()"
                      "MD5", "MD5()"
                      "SHA1", "SHA1()"
                      "SHA2", "SHA2('abc')"
                      "TO_BASE64", "TO_BASE64()"
                      "FROM_BASE64", "FROM_BASE64()" |]
               spec
                   "string-slicing-and-search"
                   [| "LEFT"; "RIGHT"; "SUBSTRING"; "INSTR"; "LOCATE"; "REPLACE"; "SUBSTRING_INDEX" |]
                   "LEFT('abcd', 2), RIGHT('abcd', 2), SUBSTRING('abcd', 2, 2), INSTR('abcd', 'bc'), LOCATE('bc', 'abcd'), REPLACE('abcd', 'bc', 'X'), SUBSTRING_INDEX('a,b,c', ',', 2)"
                   "LEFT(?, ?), RIGHT(?, ?), SUBSTRING(?, ?, ?), INSTR(?, ?), LOCATE(?, ?), REPLACE(?, ?, ?), SUBSTRING_INDEX(?, ?, ?)"
                   [| box "abcd"
                      box 2
                      box "abcd"
                      box 2
                      box "abcd"
                      box 2
                      box 2
                      box "abcd"
                      box "bc"
                      box "bc"
                      box "abcd"
                      box "abcd"
                      box "bc"
                      box "X"
                      box "a,b,c"
                      box ","
                      box 2 |]
                   (Some(
                       [| "LEFT"; "RIGHT"; "SUBSTRING"; "INSTR"; "LOCATE"; "REPLACE"; "SUBSTRING_INDEX" |],
                       "LEFT(?, 2), RIGHT(?, 2), SUBSTRING(?, 2, 2), INSTR(?, 'b'), LOCATE('b', ?), REPLACE(?, 'b', 'x'), SUBSTRING_INDEX(?, ',', 2)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "INSTR", "INSTR('abc')"
                      "LOCATE", "LOCATE('a')"
                      "SUBSTRING_INDEX", "SUBSTRING_INDEX('a,b', ',')" |]
               spec
                   "string-padding-and-repeat"
                   [| "REVERSE"; "REPEAT"; "SPACE"; "LPAD"; "RPAD"; "LTRIM"; "RTRIM" |]
                   "REVERSE('abc'), REPEAT('ab', 2), SPACE(3), LPAD('a', 3, '0'), RPAD('a', 3, '0'), LTRIM('  a'), RTRIM('a  ')"
                   "REVERSE(?), REPEAT(?, ?), SPACE(?), LPAD(?, ?, ?), RPAD(?, ?, ?), LTRIM(?), RTRIM(?)"
                   [| box "abc"
                      box "ab"
                      box 2
                      box 3
                      box "a"
                      box 3
                      box "0"
                      box "a"
                      box 3
                      box "0"
                      box "  a"
                      box "a  " |]
                   (Some(
                       [| "REVERSE"; "REPEAT"; "SPACE"; "LPAD"; "RPAD"; "LTRIM"; "RTRIM" |],
                       "REVERSE(?), REPEAT(?, 2), SPACE(?), LPAD(?, 3, '0'), RPAD(?, 3, '0'), LTRIM(?), RTRIM(?)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "SPACE", "SPACE()"
                      "LPAD", "LPAD('a', 3)"
                      "RPAD", "RPAD('a', 3)"
                      "LTRIM", "LTRIM()"
                      "RTRIM", "RTRIM()" |]
               spec
                   "concat-null-contracts"
                   [| "CONCAT" |]
                   "CONCAT('a', 'b')"
                   "CONCAT(?, ?)"
                   [| box "a"; box "b" |]
                   (Some([| "CONCAT" |], "CONCAT(?, 'b')", [| nullValue |]))
                   [| "CONCAT", "CONCAT()" |]
               spec
                   "concat-ws-null-contracts"
                   [| "CONCAT_WS" |]
                   "CONCAT_WS('-', 'a', 'b')"
                   "CONCAT_WS(?, ?, ?)"
                   [| box "-"; box "a"; box "b" |]
                   (Some([| "CONCAT_WS" |], "CONCAT_WS('-', ?, 'b')", [| nullValue |]))
                   [| "CONCAT_WS", "CONCAT_WS('-')" |] |]

        let jsonSpecs =
            [| spec
                   "json-inspection"
                   [| "JSON_TYPE"; "JSON_VALID"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE" |]
                   "JSON_TYPE('{\"a\":[1,2]}'), JSON_VALID('{\"a\":1}'), JSON_DEPTH('{\"a\":[1,2]}'), JSON_LENGTH('{\"a\":[1,2]}'), JSON_STORAGE_SIZE('{\"a\":1}'), JSON_STORAGE_FREE('{\"a\":1}')"
                   "JSON_TYPE(?), JSON_VALID(?), JSON_DEPTH(?), JSON_LENGTH(?), JSON_STORAGE_SIZE(?), JSON_STORAGE_FREE(?)"
                   [| box "{\"a\":[1,2]}"
                      box "{\"a\":1}"
                      box "{\"a\":[1,2]}"
                      box "{\"a\":[1,2]}"
                      box "{\"a\":1}"
                      box "{\"a\":1}" |]
                   (Some(
                       [| "JSON_TYPE"; "JSON_VALID"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE" |],
                       "JSON_TYPE(?), JSON_VALID(?), JSON_DEPTH(?), JSON_LENGTH(?), JSON_STORAGE_SIZE(?), JSON_STORAGE_FREE(?)",
                       [| nullValue; nullValue; nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "JSON_TYPE", "JSON_TYPE()"
                      "JSON_VALID", "JSON_VALID()"
                      "JSON_DEPTH", "JSON_DEPTH()"
                      "JSON_LENGTH", "JSON_LENGTH()"
                      "JSON_STORAGE_SIZE", "JSON_STORAGE_SIZE()"
                      "JSON_STORAGE_FREE", "JSON_STORAGE_FREE()" |]
               spec
                   "json-text-results"
                   [| "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_PRETTY"; "JSON_KEYS" |]
                   "JSON_UNQUOTE('\"hello\"'), JSON_QUOTE('hello'), JSON_PRETTY('{\"a\":1}'), JSON_KEYS('{\"a\":1}')"
                   "JSON_UNQUOTE(?), JSON_QUOTE(?), JSON_PRETTY(?), JSON_KEYS(?)"
                   [| box "\"hello\""; box "hello"; box "{\"a\":1}"; box "{\"a\":1}" |]
                   (Some(
                       [| "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_PRETTY"; "JSON_KEYS" |],
                       "JSON_UNQUOTE(?), JSON_QUOTE(?), JSON_PRETTY(?), JSON_KEYS(?)",
                       [| nullValue; nullValue; nullValue; nullValue |]
                   ))
                   [| "JSON_UNQUOTE", "JSON_UNQUOTE()"
                      "JSON_QUOTE", "JSON_QUOTE()"
                      "JSON_PRETTY", "JSON_PRETTY()"
                      "JSON_KEYS", "JSON_KEYS()" |]
               spec
                   "json-predicates"
                   [| "JSON_CONTAINS"; "JSON_OVERLAPS" |]
                   "JSON_CONTAINS('[1,2]', '2'), JSON_OVERLAPS('[1,2]', '[2,3]')"
                   "JSON_CONTAINS(?, ?), JSON_OVERLAPS(?, ?)"
                   [| box "[1,2]"; box "2"; box "[1,2]"; box "[2,3]" |]
                   (Some(
                       [| "JSON_CONTAINS"; "JSON_OVERLAPS" |],
                       "JSON_CONTAINS(?, '2'), JSON_OVERLAPS(?, '[2,3]')",
                       [| nullValue; nullValue |]
                   ))
                   [| "JSON_CONTAINS", "JSON_CONTAINS('[1,2]')"; "JSON_OVERLAPS", "JSON_OVERLAPS('[1,2]')" |]
               spec
                   "json-schema-validation"
                   [| "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |]
                   "JSON_SCHEMA_VALID('{\"type\":\"integer\"}', '7'), JSON_SCHEMA_VALIDATION_REPORT('{\"type\":\"integer\"}', '7')"
                   "JSON_SCHEMA_VALID(JSON_EXTRACT(?, '$'), JSON_EXTRACT(?, '$')), JSON_SCHEMA_VALIDATION_REPORT(JSON_EXTRACT(?, '$'), JSON_EXTRACT(?, '$'))"
                   [| box "{\"type\":\"integer\"}"
                      box "7"
                      box "{\"type\":\"integer\"}"
                      box "7" |]
                   (Some(
                       [| "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |],
                       "JSON_SCHEMA_VALID(JSON_EXTRACT(?, '$'), JSON_EXTRACT('7', '$')), JSON_SCHEMA_VALIDATION_REPORT(JSON_EXTRACT(?, '$'), JSON_EXTRACT('7', '$'))",
                       [| nullValue; nullValue |]
                   ))
                   [| "JSON_SCHEMA_VALID", "JSON_SCHEMA_VALID('{}')"
                      "JSON_SCHEMA_VALIDATION_REPORT", "JSON_SCHEMA_VALIDATION_REPORT('{}')" |] |]

        let temporalSpecs =
            [| spec
                   "temporal-components"
                   [| "DATE"
                      "TIME"
                      "YEAR"
                      "MONTH"
                      "DAY"
                      "DAYOFMONTH"
                      "HOUR"
                      "MINUTE"
                      "SECOND"
                      "MICROSECOND" |]
                   "DATE('2024-02-29 12:34:56.123456'), TIME('2024-02-29 12:34:56.123456'), YEAR('2024-02-29'), MONTH('2024-02-29'), DAY('2024-02-29'), DAYOFMONTH('2024-02-29'), HOUR('12:34:56.123456'), MINUTE('12:34:56.123456'), SECOND('12:34:56.123456'), MICROSECOND('12:34:56.123456')"
                   "DATE(?), TIME(?), YEAR(?), MONTH(?), DAY(?), DAYOFMONTH(?), HOUR(?), MINUTE(?), SECOND(?), MICROSECOND(?)"
                   [| box "2024-02-29 12:34:56.123456"
                      box "2024-02-29 12:34:56.123456"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "12:34:56.123456"
                      box "12:34:56.123456"
                      box "12:34:56.123456"
                      box "12:34:56.123456" |]
                   (Some(
                       [| "DATE"
                          "TIME"
                          "YEAR"
                          "MONTH"
                          "DAY"
                          "DAYOFMONTH"
                          "HOUR"
                          "MINUTE"
                          "SECOND"
                          "MICROSECOND" |],
                       "DATE(?), TIME(?), YEAR(?), MONTH(?), DAY(?), DAYOFMONTH(?), HOUR(?), MINUTE(?), SECOND(?), MICROSECOND(?)",
                       Array.create 10 nullValue
                   ))
                   [| "DAYOFMONTH", "DAYOFMONTH()" |]
               spec
                   "temporal-calendar"
                   [| "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"; "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "LAST_DAY" |]
                   "DAYOFWEEK('2024-02-29'), DAYOFYEAR('2024-02-29'), WEEKDAY('2024-02-29'), WEEK('2024-02-29', 3), WEEKOFYEAR('2024-02-29'), YEARWEEK('2024-02-29', 3), QUARTER('2024-02-29'), LAST_DAY('2024-02-10')"
                   "DAYOFWEEK(?), DAYOFYEAR(?), WEEKDAY(?), WEEK(?, ?), WEEKOFYEAR(?), YEARWEEK(?, ?), QUARTER(?), LAST_DAY(?)"
                   [| box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box "2024-02-29"
                      box 3
                      box "2024-02-29"
                      box "2024-02-29"
                      box 3
                      box "2024-02-29"
                      box "2024-02-10" |]
                   (Some(
                       [| "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"; "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "LAST_DAY" |],
                       "DAYOFWEEK(?), DAYOFYEAR(?), WEEKDAY(?), WEEK(?, 3), WEEKOFYEAR(?), YEARWEEK(?, 3), QUARTER(?), LAST_DAY(?)",
                       Array.create 8 nullValue
                   ))
                   [| "DAYOFWEEK", "DAYOFWEEK()"
                      "DAYOFYEAR", "DAYOFYEAR()"
                      "WEEKDAY", "WEEKDAY()"
                      "WEEKOFYEAR", "WEEKOFYEAR()"
                      "YEARWEEK", "YEARWEEK()"
                      "LAST_DAY", "LAST_DAY()" |]
               spec
                   "temporal-arithmetic"
                   [| "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME" |]
                   "DATEDIFF('2024-03-02', '2024-02-29'), TIMEDIFF('12:00:01.5', '10:00:00'), ADDTIME('10:00:00', '01:02:03'), SUBTIME('10:00:00', '01:02:03'), MAKEDATE(2024, 60), MAKETIME(12, 34, 56.5), SEC_TO_TIME(3661.5)"
                   "DATEDIFF(?, ?), TIMEDIFF(?, ?), ADDTIME(CAST(? AS TIME(6)), ?), SUBTIME(CAST(? AS TIME(6)), ?), MAKEDATE(?, ?), MAKETIME(?, ?, ?), SEC_TO_TIME(?)"
                   [| box "2024-03-02"
                      box "2024-02-29"
                      box "12:00:01.5"
                      box "10:00:00"
                      box (TimeSpan(10, 0, 0))
                      box (TimeSpan(1, 2, 3))
                      box (TimeSpan(10, 0, 0))
                      box (TimeSpan(1, 2, 3))
                      box 2024
                      box 60
                      box 12
                      box 34
                      box 56.5
                      box 3661.5 |]
                   (Some(
                       [| "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME" |],
                       "DATEDIFF(?, '2024-02-29'), TIMEDIFF(?, '10:00:00'), ADDTIME(CAST(? AS TIME), '01:02:03'), SUBTIME(CAST(? AS TIME), '01:02:03'), MAKEDATE(?, 60), MAKETIME(?, 34, 56), SEC_TO_TIME(?)",
                       Array.create 7 nullValue
                   ))
                   [| "DATEDIFF", "DATEDIFF('2024-03-02')"
                      "TIMEDIFF", "TIMEDIFF('12:00:01')"
                      "ADDTIME", "ADDTIME('10:00:00')"
                      "SUBTIME", "SUBTIME('10:00:00')"
                      "MAKEDATE", "MAKEDATE(2024)"
                      "MAKETIME", "MAKETIME(12, 34)"
                      "SEC_TO_TIME", "SEC_TO_TIME()" |]
               spec
                   "temporal-conversion"
                   [| "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"; "FROM_UNIXTIME"; "TIME_FORMAT"; "STR_TO_DATE"; "CONVERT_TZ" |]
                   "TO_DAYS('2024-02-29'), FROM_DAYS(739310), UNIX_TIMESTAMP('2024-02-29 12:00:00'), FROM_UNIXTIME(1709208000), TIME_FORMAT('12:34:56.123456', '%H:%i:%s.%f'), STR_TO_DATE('2024-02-29', '%Y-%m-%d'), CONVERT_TZ('2024-02-29 12:00:00', '+00:00', '+02:00')"
                   "TO_DAYS(?), FROM_DAYS(?), UNIX_TIMESTAMP(CAST(? AS DATETIME)), FROM_UNIXTIME(?), TIME_FORMAT(?, ?), STR_TO_DATE(?, '%Y-%m-%d'), CONVERT_TZ(?, ?, ?)"
                   [| box "2024-02-29"
                      box 739310
                      box "2024-02-29 12:00:00"
                      box 1709208000
                      box "12:34:56.123456"
                      box "%H:%i:%s.%f"
                      box "2024-02-29"
                      box "2024-02-29 12:00:00"
                      box "+00:00"
                      box "+02:00" |]
                   (Some(
                       [| "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"; "FROM_UNIXTIME"; "TIME_FORMAT"; "STR_TO_DATE"; "CONVERT_TZ" |],
                       "TO_DAYS(?), FROM_DAYS(?), UNIX_TIMESTAMP(CAST(? AS DATETIME)), FROM_UNIXTIME(?), TIME_FORMAT(?, '%H:%i:%s'), STR_TO_DATE(?, '%Y-%m-%d'), CONVERT_TZ(?, '+00:00', '+02:00')",
                       Array.create 7 nullValue
                   ))
                   [| "TO_DAYS", "TO_DAYS()"
                      "FROM_DAYS", "FROM_DAYS()"
                      "UNIX_TIMESTAMP", "UNIX_TIMESTAMP(1, 2)"
                      "FROM_UNIXTIME", "FROM_UNIXTIME()"
                      "TIME_FORMAT", "TIME_FORMAT('12:34:56')"
                      "STR_TO_DATE", "STR_TO_DATE('2024-02-29')"
                      "CONVERT_TZ", "CONVERT_TZ('2024-02-29', '+00:00')" |] |]

        let spatialSpecs =
            [| spec
                   "spatial-construction-and-serialization"
                   [| "ST_GEOMFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKB" |]
                   "ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_POINTFROMTEXT('POINT(1 2)'), ST_ASTEXT(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_ASWKB(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?), ST_ASTEXT(ST_GEOMFROMTEXT(?)), ST_ASWKB(ST_POINTFROMTEXT(?))"
                   [| box "LINESTRING(0 0, 2 2)"; box "POINT(1 2)"; box "LINESTRING(0 0, 2 2)"; box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKB" |],
                       "ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?), ST_ASTEXT(?), ST_ASWKB(?)",
                       Array.create 4 nullValue
                   ))
                   [| "ST_GEOMFROMTEXT", "ST_GEOMFROMTEXT()"
                      "ST_POINTFROMTEXT", "ST_POINTFROMTEXT()"
                      "ST_ASTEXT", "ST_ASTEXT()"
                      "ST_ASWKB", "ST_ASWKB()" |]
               spec
                   "spatial-inspection"
                   [| "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_SRID"; "ST_X"; "ST_Y" |]
                   "ST_GEOMETRYTYPE(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_DIMENSION(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)')), ST_ISEMPTY(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION EMPTY')), ST_ISVALID(ST_GEOMFROMTEXT('POLYGON((0 0, 2 0, 2 2, 0 0))')), ST_SRID(ST_GEOMFROMTEXT('POINT(1 2)')), ST_X(ST_POINTFROMTEXT('POINT(1 2)')), ST_Y(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMETRYTYPE(ST_GEOMFROMTEXT(?)), ST_DIMENSION(ST_GEOMFROMTEXT(?)), ST_ISEMPTY(ST_GEOMFROMTEXT(?)), ST_ISVALID(ST_GEOMFROMTEXT(?)), ST_SRID(ST_GEOMFROMTEXT(?)), ST_X(ST_POINTFROMTEXT(?)), ST_Y(ST_POINTFROMTEXT(?))"
                   [| box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 0, 2 2)"
                      box "GEOMETRYCOLLECTION EMPTY"
                      box "POLYGON((0 0, 2 0, 2 2, 0 0))"
                      box "POINT(1 2)"
                      box "POINT(1 2)"
                      box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_SRID"; "ST_X"; "ST_Y" |],
                       "ST_GEOMETRYTYPE(?), ST_DIMENSION(?), ST_ISEMPTY(?), ST_ISVALID(?), ST_SRID(?), ST_X(?), ST_Y(?)",
                       Array.create 7 nullValue
                   ))
                   [| "ST_GEOMETRYTYPE", "ST_GEOMETRYTYPE()"
                      "ST_DIMENSION", "ST_DIMENSION()"
                      "ST_ISEMPTY", "ST_ISEMPTY()"
                      "ST_ISVALID", "ST_ISVALID()"
                      "ST_SRID", "ST_SRID()"
                      "ST_X", "ST_X()"
                      "ST_Y", "ST_Y()" |]
               spec
                   "spatial-property-accessors"
                   [| "ST_ISCLOSED"
                      "ST_NUMPOINTS"
                      "ST_STARTPOINT"
                      "ST_ENDPOINT"
                      "ST_POINTN"
                      "ST_NUMINTERIORRING"
                      "ST_NUMINTERIORRINGS"
                      "ST_EXTERIORRING"
                      "ST_INTERIORRINGN"
                      "ST_NUMGEOMETRIES"
                      "ST_GEOMETRYN" |]
                   "ST_ISCLOSED(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,0 0)')), ST_NUMPOINTS(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)')), ST_ASTEXT(ST_STARTPOINT(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'))), ST_ASTEXT(ST_ENDPOINT(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'))), ST_ASTEXT(ST_POINTN(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1,2 3)'), 2)), ST_NUMINTERIORRING(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))')), ST_NUMINTERIORRINGS(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))')), ST_ASTEXT(ST_EXTERIORRING(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))'))), ST_ASTEXT(ST_INTERIORRINGN(ST_GEOMFROMTEXT('POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))'), 1)), ST_NUMGEOMETRIES(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))')), ST_ASTEXT(ST_GEOMETRYN(ST_GEOMFROMTEXT('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(0 0,1 1))'), 2))"
                   "ST_ISCLOSED(ST_GEOMFROMTEXT(?)), ST_NUMPOINTS(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_STARTPOINT(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_ENDPOINT(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_POINTN(ST_GEOMFROMTEXT(?), ?)), ST_NUMINTERIORRINGS(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_EXTERIORRING(ST_GEOMFROMTEXT(?))), ST_ASTEXT(ST_INTERIORRINGN(ST_GEOMFROMTEXT(?), ?)), ST_NUMGEOMETRIES(ST_GEOMFROMTEXT(?)), ST_ASTEXT(ST_GEOMETRYN(ST_GEOMFROMTEXT(?), ?))"
                   [| box "LINESTRING(0 0,1 1,0 0)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box "LINESTRING(0 0,1 1,2 3)"
                      box 2
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box "POLYGON((0 0,4 0,4 4,0 0),(1 1,2 1,1 2,1 1))"
                      box 1
                      box "MULTIPOINT((1 2),(3 4))"
                      box "MULTIPOINT((1 2),(3 4))"
                      box 2 |]
                   (Some(
                       [| "ST_ISCLOSED"
                          "ST_NUMPOINTS"
                          "ST_STARTPOINT"
                          "ST_ENDPOINT"
                          "ST_POINTN"
                          "ST_NUMINTERIORRINGS"
                          "ST_EXTERIORRING"
                          "ST_INTERIORRINGN"
                          "ST_NUMGEOMETRIES"
                          "ST_GEOMETRYN" |],
                       "ST_ISCLOSED(?), ST_NUMPOINTS(?), ST_STARTPOINT(?), ST_ENDPOINT(?), ST_POINTN(?, 1), ST_NUMINTERIORRINGS(?), ST_EXTERIORRING(?), ST_INTERIORRINGN(?, 1), ST_NUMGEOMETRIES(?), ST_GEOMETRYN(?, 1)",
                       Array.create 10 nullValue
                   ))
                   [| "ST_ISCLOSED", "ST_ISCLOSED()"
                      "ST_NUMPOINTS", "ST_NUMPOINTS()"
                      "ST_STARTPOINT", "ST_STARTPOINT()"
                      "ST_ENDPOINT", "ST_ENDPOINT()"
                      "ST_POINTN", "ST_POINTN(ST_GEOMFROMTEXT('LINESTRING(0 0,1 1)'))"
                      "ST_NUMINTERIORRING", "ST_NUMINTERIORRING()"
                      "ST_NUMINTERIORRINGS", "ST_NUMINTERIORRINGS()"
                      "ST_EXTERIORRING", "ST_EXTERIORRING()"
                      "ST_INTERIORRINGN", "ST_INTERIORRINGN(ST_GEOMFROMTEXT('POLYGON((0 0,1 0,1 1,0 0))'))"
                      "ST_NUMGEOMETRIES", "ST_NUMGEOMETRIES()"
                      "ST_GEOMETRYN", "ST_GEOMETRYN(ST_GEOMFROMTEXT('MULTIPOINT((1 2))'))" |]
               spec
                   "spatial-relations"
                   [| "ST_DISTANCE"
                      "ST_EQUALS"
                      "ST_CONTAINS"
                      "ST_WITHIN"
                      "ST_INTERSECTS"
                      "ST_DISJOINT"
                      "ST_TOUCHES"
                      "MBRCONTAINS"
                      "MBRWITHIN"
                      "MBRINTERSECTS" |]
                   "ST_DISTANCE(ST_POINTFROMTEXT('POINT(0 0)'), ST_POINTFROMTEXT('POINT(3 4)')), ST_EQUALS(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_CONTAINS(ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))'), ST_POINTFROMTEXT('POINT(2 2)')), ST_WITHIN(ST_POINTFROMTEXT('POINT(2 2)'), ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), ST_INTERSECTS(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)')), ST_DISJOINT(ST_POINTFROMTEXT('POINT(0 0)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_TOUCHES(ST_GEOMFROMTEXT('LINESTRING(0 0, 1 0)'), ST_GEOMFROMTEXT('LINESTRING(1 0, 2 0)')), MBRCONTAINS(ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))'), ST_POINTFROMTEXT('POINT(2 2)')), MBRWITHIN(ST_POINTFROMTEXT('POINT(2 2)'), ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), MBRINTERSECTS(ST_GEOMFROMTEXT('LINESTRING(0 0, 2 2)'), ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)'))"
                   "ST_DISTANCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_EQUALS(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_CONTAINS(ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_WITHIN(ST_POINTFROMTEXT(?), ST_GEOMFROMTEXT(?)), ST_INTERSECTS(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?)), ST_DISJOINT(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_TOUCHES(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?)), MBRCONTAINS(ST_GEOMFROMTEXT(?), ST_POINTFROMTEXT(?)), MBRWITHIN(ST_POINTFROMTEXT(?), ST_GEOMFROMTEXT(?)), MBRINTERSECTS(ST_GEOMFROMTEXT(?), ST_GEOMFROMTEXT(?))"
                   [| box "POINT(0 0)"
                      box "POINT(3 4)"
                      box "POINT(1 1)"
                      box "POINT(1 1)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "POINT(2 2)"
                      box "POINT(2 2)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 2, 2 0)"
                      box "POINT(0 0)"
                      box "POINT(1 1)"
                      box "LINESTRING(0 0, 1 0)"
                      box "LINESTRING(1 0, 2 0)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "POINT(2 2)"
                      box "POINT(2 2)"
                      box "POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))"
                      box "LINESTRING(0 0, 2 2)"
                      box "LINESTRING(0 2, 2 0)" |]
                   (Some(
                       [| "ST_DISTANCE"
                          "ST_EQUALS"
                          "ST_CONTAINS"
                          "ST_WITHIN"
                          "ST_INTERSECTS"
                          "ST_DISJOINT"
                          "ST_TOUCHES"
                          "MBRCONTAINS"
                          "MBRWITHIN"
                          "MBRINTERSECTS" |],
                       "ST_DISTANCE(?, ST_POINTFROMTEXT('POINT(0 0)')), ST_EQUALS(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_CONTAINS(?, ST_POINTFROMTEXT('POINT(2 2)')), ST_WITHIN(?, ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), ST_INTERSECTS(?, ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)')), ST_DISJOINT(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_TOUCHES(?, ST_GEOMFROMTEXT('LINESTRING(1 0, 2 0)')), MBRCONTAINS(?, ST_POINTFROMTEXT('POINT(2 2)')), MBRWITHIN(?, ST_GEOMFROMTEXT('POLYGON((0 0, 4 0, 4 4, 0 4, 0 0))')), MBRINTERSECTS(?, ST_GEOMFROMTEXT('LINESTRING(0 2, 2 0)'))",
                       Array.create 10 nullValue
                   ))
                   [| "ST_DISTANCE", "ST_DISTANCE(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_EQUALS", "ST_EQUALS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_CONTAINS", "ST_CONTAINS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_WITHIN", "ST_WITHIN(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_INTERSECTS", "ST_INTERSECTS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_DISJOINT", "ST_DISJOINT(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_TOUCHES", "ST_TOUCHES(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRCONTAINS", "MBRCONTAINS(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRWITHIN", "MBRWITHIN(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "MBRINTERSECTS", "MBRINTERSECTS(ST_POINTFROMTEXT('POINT(0 0)'))" |] |]

        let spatialResultSpecs =
            [| spec
                   "spatial-overlay-results"
                   [| "ST_INTERSECTION"; "ST_UNION"; "ST_DIFFERENCE"; "ST_SYMDIFFERENCE"; "ST_CONVEXHULL"; "ST_ENVELOPE" |]
                   "ST_INTERSECTION(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_UNION(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_DIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_SYMDIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'), ST_POINTFROMTEXT('POINT(1 1)')), ST_CONVEXHULL(ST_POINTFROMTEXT('POINT(1 1)')), ST_ENVELOPE(ST_POINTFROMTEXT('POINT(1 1)'))"
                   "ST_INTERSECTION(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_UNION(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_DIFFERENCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_SYMDIFFERENCE(ST_POINTFROMTEXT(?), ST_POINTFROMTEXT(?)), ST_CONVEXHULL(ST_POINTFROMTEXT(?)), ST_ENVELOPE(ST_POINTFROMTEXT(?))"
                   (Array.create 10 (box "POINT(1 1)"))
                   (Some(
                       [| "ST_INTERSECTION"; "ST_UNION"; "ST_DIFFERENCE"; "ST_SYMDIFFERENCE"; "ST_CONVEXHULL"; "ST_ENVELOPE" |],
                       "ST_INTERSECTION(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_UNION(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_DIFFERENCE(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_SYMDIFFERENCE(?, ST_POINTFROMTEXT('POINT(1 1)')), ST_CONVEXHULL(?), ST_ENVELOPE(?)",
                       Array.create 6 nullValue
                   ))
                   [| "ST_INTERSECTION", "ST_INTERSECTION(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_UNION", "ST_UNION(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_DIFFERENCE", "ST_DIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_SYMDIFFERENCE", "ST_SYMDIFFERENCE(ST_POINTFROMTEXT('POINT(1 1)'))"
                      "ST_CONVEXHULL", "ST_CONVEXHULL()"
                      "ST_ENVELOPE", "ST_ENVELOPE()" |]
               shapeSpec
                   "spatial-buffer-shapes"
                   [| "ST_BUFFER"; "ST_BUFFER_STRATEGY" |]
                   "ST_CONTAINS(ST_BUFFER(ST_POINTFROMTEXT('POINT(0 0)'), 2), ST_POINTFROMTEXT('POINT(0 0)')), LENGTH(ST_BUFFER_STRATEGY('point_square')) > 0"
                   "ST_CONTAINS(ST_BUFFER(ST_POINTFROMTEXT(?), ?), ST_POINTFROMTEXT(?)), LENGTH(ST_BUFFER_STRATEGY(?)) > 0"
                   [| box "POINT(0 0)"; box 2; box "POINT(0 0)"; box "point_square" |]
                   (Some(
                       [| "ST_BUFFER"; "ST_BUFFER_STRATEGY" |],
                       "ST_BUFFER(?, 2), ST_BUFFER_STRATEGY(?)",
                       [| nullValue; nullValue |]
                   ))
                   [| "ST_BUFFER", "ST_BUFFER(ST_POINTFROMTEXT('POINT(0 0)'))"
                      "ST_BUFFER_STRATEGY", "ST_BUFFER_STRATEGY()" |] |]

        let temporalSyntaxSpecs =
            [| spec
                   "temporal-syntax-functions"
                   [| "DATE_ADD"; "DATE_SUB"; "TIMESTAMP"; "TIMESTAMPADD"; "TIMESTAMPDIFF"; "DATE_FORMAT" |]
                   "DATE_ADD('2024-02-28', INTERVAL 1 DAY), DATE_SUB('2024-03-01', INTERVAL 1 DAY), TIMESTAMP('2024-02-29 12:34:56'), TIMESTAMPADD(DAY, 2, '2024-02-28'), TIMESTAMPDIFF(DAY, '2024-02-28', '2024-03-01'), DATE_FORMAT('2024-02-29 12:34:56', '%Y-%m-%d')"
                   "DATE_ADD(CAST(? AS DATE), INTERVAL ? DAY), DATE_SUB(CAST(? AS DATE), INTERVAL ? DAY), TIMESTAMP(?), TIMESTAMPADD(DAY, ?, CAST(? AS DATE)), TIMESTAMPDIFF(DAY, ?, ?), DATE_FORMAT(?, ?)"
                   [| box "2024-02-28"
                      box 1
                      box "2024-03-01"
                      box 1
                      box "2024-02-29 12:34:56"
                      box 2
                      box "2024-02-28"
                      box "2024-02-28"
                      box "2024-03-01"
                      box "2024-02-29 12:34:56"
                      box "%Y-%m-%d" |]
                   (Some(
                       [| "DATE_ADD"; "DATE_SUB"; "TIMESTAMP"; "TIMESTAMPADD"; "TIMESTAMPDIFF"; "DATE_FORMAT" |],
                       "DATE_ADD(CAST(? AS DATE), INTERVAL 1 DAY), DATE_SUB(CAST(? AS DATE), INTERVAL 1 DAY), TIMESTAMP(?), TIMESTAMPADD(DAY, 2, CAST(? AS DATE)), TIMESTAMPDIFF(DAY, ?, '2024-03-01'), DATE_FORMAT(?, '%Y-%m-%d')",
                       Array.create 6 nullValue
                   ))
                   [||] |]

        let spatialAliasSpecs =
            [| spec
                   "spatial-text-aliases"
                   [| "ST_GEOMETRYFROMTEXT"; "ST_LINESTRINGFROMTEXT"; "ST_POLYGONFROMTEXT"; "ST_ASWKT"; "ST_ASBINARY" |]
                   "ST_GEOMETRYFROMTEXT('POINT(1 2)'), ST_LINESTRINGFROMTEXT('LINESTRING(0 0, 2 2)'), ST_POLYGONFROMTEXT('POLYGON((0 0, 2 0, 0 2, 0 0))'), ST_ASWKT(ST_POINTFROMTEXT('POINT(1 2)')), ST_ASBINARY(ST_POINTFROMTEXT('POINT(1 2)'))"
                   "ST_GEOMETRYFROMTEXT(?), ST_LINESTRINGFROMTEXT(?), ST_POLYGONFROMTEXT(?), ST_ASWKT(ST_POINTFROMTEXT(?)), ST_ASBINARY(ST_POINTFROMTEXT(?))"
                   [| box "POINT(1 2)"
                      box "LINESTRING(0 0, 2 2)"
                      box "POLYGON((0 0, 2 0, 0 2, 0 0))"
                      box "POINT(1 2)"
                      box "POINT(1 2)" |]
                   (Some(
                       [| "ST_GEOMETRYFROMTEXT"; "ST_LINESTRINGFROMTEXT"; "ST_POLYGONFROMTEXT"; "ST_ASWKT"; "ST_ASBINARY" |],
                       "ST_GEOMETRYFROMTEXT(?), ST_LINESTRINGFROMTEXT(?), ST_POLYGONFROMTEXT(?), ST_ASWKT(?), ST_ASBINARY(?)",
                       Array.create 5 nullValue
                   ))
                   [| "ST_GEOMETRYFROMTEXT", "ST_GEOMETRYFROMTEXT()"
                      "ST_LINESTRINGFROMTEXT", "ST_LINESTRINGFROMTEXT()"
                      "ST_POLYGONFROMTEXT", "ST_POLYGONFROMTEXT()"
                      "ST_ASWKT", "ST_ASWKT()"
                      "ST_ASBINARY", "ST_ASBINARY()" |]
               spec
                   "spatial-wkb-construction"
                   [| "ST_GEOMFROMWKB"; "ST_GEOMETRYFROMWKB"; "ST_POINTFROMWKB" |]
                   "ST_GEOMFROMWKB(X'0101000000000000000000F03F0000000000000040'), ST_GEOMETRYFROMWKB(X'0101000000000000000000F03F0000000000000040'), ST_POINTFROMWKB(X'0101000000000000000000F03F0000000000000040')"
                   "ST_GEOMFROMWKB(?), ST_GEOMETRYFROMWKB(?), ST_POINTFROMWKB(?)"
                   (Array.create 3 (box (Convert.FromHexString "0101000000000000000000F03F0000000000000040")))
                   (Some(
                       [| "ST_GEOMFROMWKB"; "ST_GEOMETRYFROMWKB"; "ST_POINTFROMWKB" |],
                       "ST_GEOMFROMWKB(?), ST_GEOMETRYFROMWKB(?), ST_POINTFROMWKB(?)",
                       Array.create 3 nullValue
                   ))
                   [| "ST_GEOMFROMWKB", "ST_GEOMFROMWKB()"
                      "ST_GEOMETRYFROMWKB", "ST_GEOMETRYFROMWKB()"
                      "ST_POINTFROMWKB", "ST_POINTFROMWKB()" |] |]

        let specs =
            Array.concat
                [ establishedSpecs
                  numericSpecs
                  stringSpecs
                  jsonSpecs
                  temporalSpecs
                  temporalSyntaxSpecs
                  spatialSpecs
                  spatialResultSpecs
                  spatialAliasSpecs ]

        let steps, coverage = generatedFunctionContracts specs

        { Name = "generated-function-contracts"
          Setup = [| "SET SESSION time_zone = '+00:00'" |]
          Steps = steps
          Cleanup = [||]
          Coverage = coverage }

    let private functionFamilies =
        let numericSteps, numericCoverage =
            functionProbe
                "numeric-functions"
                [| "ABS"; "CEIL"; "CEILING"; "FLOOR"; "POW"; "POWER"; "SQRT"; "LOG"; "LN"; "LOG2"; "LOG10"
                   "EXP"; "PI"; "SIN"; "COS"; "TAN"; "ASIN"; "ACOS"; "ATAN"; "ATAN2"; "DEGREES"; "RADIANS"
                   "SIGN"; "TRUNCATE"; "ROUND"; "MOD"; "GREATEST"; "LEAST"; "NULLIF"; "ISNULL"; "CONV"; "BIN"
                   "BIT_COUNT"; "OCT"; "CRC32" |]
                """SELECT ABS(-2), CEIL(1.2), CEILING(1.2), FLOOR(1.8), POW(2, 3), POWER(2, 3), SQRT(4),
                          LOG(EXP(1)), LN(EXP(1)), LOG2(8), LOG10(100), EXP(0), PI() > 3, SIN(0), COS(0), TAN(0),
                          ASIN(0), ACOS(1), ATAN(0), ATAN2(0, 1), DEGREES(PI()), RADIANS(180) > 3,
                          SIGN(-2), TRUNCATE(1.29, 1), ROUND(1.25, 1), MOD(7, 3), GREATEST(1, 3, 2),
                          LEAST(1, 3, 2), NULLIF(1, 1), ISNULL(NULL), CONV('ff', 16, 10), BIN(5), BIT_COUNT(7),
                          OCT(8), CRC32('abc')"""

        let stringSteps, stringCoverage =
            functionProbe
                "string-functions"
                [| "CONCAT"; "CONCAT_WS"; "UPPER"; "UCASE"; "LOWER"; "LCASE"; "LENGTH"; "OCTET_LENGTH"
                   "BIT_LENGTH"; "CHAR_LENGTH"; "CHARACTER_LENGTH"; "ASCII"; "ORD"; "HEX"; "UNHEX"; "LEFT"
                   "RIGHT"; "SUBSTRING"; "SUBSTR"; "MID"; "REPLACE"; "REVERSE"; "REPEAT"; "SPACE"; "LPAD"
                   "RPAD"; "LTRIM"; "RTRIM"; "TRIM"; "INSTR"; "LOCATE"; "POSITION"; "SUBSTRING_INDEX"; "ELT"
                   "FIELD"; "FIND_IN_SET"; "MAKE_SET"; "EXPORT_SET"; "FORMAT"; "QUOTE"; "STRCMP"; "MD5"; "SHA"
                   "SHA1"; "SHA2"; "TO_BASE64"; "FROM_BASE64"; "COMPRESS"; "UNCOMPRESS"; "UNCOMPRESSED_LENGTH"
                   "SOUNDEX"; "CHAR" |]
                """SELECT CONCAT('a', 'b'), CONCAT_WS('-', 'a', 'b'), UPPER('ab'), UCASE('ab'), LOWER('AB'), LCASE('AB'),
                          LENGTH('blå'), OCTET_LENGTH('blå'), BIT_LENGTH('A'), CHAR_LENGTH('blå'), CHARACTER_LENGTH('blå'),
                          ASCII('A'), ORD('A'), HEX('A'), HEX(UNHEX('41')), LEFT('abcd', 2), RIGHT('abcd', 2),
                          SUBSTRING('abcd', 2, 2), SUBSTR('abcd', 2, 2), MID('abcd', 2, 2), REPLACE('abc', 'b', 'x'),
                          REVERSE('abc'), REPEAT('ab', 2), LENGTH(SPACE(3)), LPAD('a', 3, '0'), RPAD('a', 3, '0'),
                          LTRIM('  a'), RTRIM('a  '), TRIM('  a  '), INSTR('abc', 'b'), LOCATE('b', 'abc'),
                          POSITION('b' IN 'abc'), SUBSTRING_INDEX('a,b,c', ',', 2), ELT(2, 'a', 'b'),
                          FIELD('b', 'a', 'b'), FIND_IN_SET('b', 'a,b'), MAKE_SET(3, 'a', 'b'),
                          EXPORT_SET(5, 'Y', 'N', ',', 4), FORMAT(1234.5, 2, 'en_US'), QUOTE('a''b'), STRCMP('a', 'b'),
                          MD5('abc'), SHA('abc'), SHA1('abc'), SHA2('abc', 256), TO_BASE64('abc'),
                          HEX(FROM_BASE64('YWJj')), UNCOMPRESSED_LENGTH(COMPRESS('abc')), HEX(UNCOMPRESS(COMPRESS('abc'))),
                          SOUNDEX('Robert'), CHAR(65, 66)"""

        let temporalSteps, temporalCoverage =
            functionProbe
                "temporal-functions"
                [| "DATE"; "TIME"; "YEAR"; "MONTH"; "DAY"; "DAYOFMONTH"; "DAYOFWEEK"; "DAYOFYEAR"; "WEEKDAY"
                   "WEEK"; "WEEKOFYEAR"; "YEARWEEK"; "QUARTER"; "HOUR"; "MINUTE"; "SECOND"; "MICROSECOND"
                   "DATEDIFF"; "TIMEDIFF"; "ADDTIME"; "SUBTIME"; "DATE_ADD"; "ADDDATE"; "DATE_SUB"; "SUBDATE"
                   "LAST_DAY"; "MAKEDATE"; "MAKETIME"; "SEC_TO_TIME"; "TO_DAYS"; "FROM_DAYS"; "UNIX_TIMESTAMP"
                   "FROM_UNIXTIME"; "DATE_FORMAT"; "TIME_FORMAT"; "STR_TO_DATE"; "TIMESTAMPADD"; "TIMESTAMPDIFF" |]
                """SELECT DATE('2024-02-29 12:34:56'), TIME('12:34:56.123456'), YEAR('2024-02-29'), MONTH('2024-02-29'),
                          DAY('2024-02-29'), DAYOFMONTH('2024-02-29'), DAYOFWEEK('2024-02-29'), DAYOFYEAR('2024-02-29'),
                          WEEKDAY('2024-02-29'), WEEK('2024-02-29', 3), WEEKOFYEAR('2024-02-29'), YEARWEEK('2024-02-29', 3),
                          QUARTER('2024-05-01'), HOUR('-34:20:30.123456'), MINUTE('-34:20:30.123456'),
                          SECOND('-34:20:30.123456'), MICROSECOND('-34:20:30.123456'), DATEDIFF('2024-03-02', '2024-02-29'),
                          TIMEDIFF('12:00:01', '12:00:00'), ADDTIME('12:00:00', '01:02:03'), SUBTIME('12:00:00', '01:02:03'),
                          DATE_ADD('2024-02-29', INTERVAL 1 DAY), ADDDATE('2024-02-29', INTERVAL 1 DAY),
                          DATE_SUB('2024-03-01', INTERVAL 1 DAY), SUBDATE('2024-03-01', INTERVAL 1 DAY),
                          LAST_DAY('2024-02-10'), MAKEDATE(2024, 60), MAKETIME(12, 34, 56), SEC_TO_TIME(3661),
                          TO_DAYS('2024-02-29'), FROM_DAYS(739310), UNIX_TIMESTAMP('2024-01-01 00:00:00'),
                          FROM_UNIXTIME(1704067200), DATE_FORMAT('2024-02-29', '%Y-%m-%d'), TIME_FORMAT('12:34:56', '%H:%i:%s'),
                          STR_TO_DATE('2024-02-29', '%Y-%m-%d'), TIMESTAMPADD(DAY, 1, '2024-02-29'),
                          TIMESTAMPDIFF(DAY, '2024-02-29', '2024-03-02')"""

        let temporalEdgeSteps =
            [| Contract.query
                   "time-components-round-seven-digits"
                   "SELECT HOUR('-34:20:30.1234567'),MINUTE('-34:20:30.1234567'),SECOND('-34:20:30.1234567'),MICROSECOND('-34:20:30.1234567'),HOUR('34:59:59.9999995'),MINUTE('34:59:59.9999995'),SECOND('34:59:59.9999995'),MICROSECOND('34:59:59.9999995'),HOUR('838:59:59.9999995'),MINUTE('838:59:59.9999995'),SECOND('838:59:59.9999995'),MICROSECOND('838:59:59.9999995')"
               |> Contract.comparingValues
               Contract.query
                   "time-components-clamp-warning-source"
                   "SELECT HOUR('838:59:59.9999995'),MINUTE('838:59:59.9999995'),TIME('839:00:00')"
                   |> Contract.comparingValues
               Contract.query "time-components-clamp-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.execute "time-precision-truncate-mode" "SET SESSION sql_mode='TIME_TRUNCATE_FRACTIONAL'"
               Contract.query
                   "time-precision-truncate-scalars"
                   "SELECT TIME('12:34:56.1234567'),MICROSECOND('12:34:56.1234567'),TIME('34:59:59.9999995'),HOUR('34:59:59.9999995'),TIME_FORMAT('12:34:56.1234567','%f'),EXTRACT(MICROSECOND FROM '12:34:56.1234567'),MICROSECOND('2024-01-01 12:34:56.1234567'),EXTRACT(MICROSECOND FROM '2024-01-01 12:34:56.1234567'),DATE('2024-01-01 23:59:59.9999995')"
                   |> Contract.comparingValues
               Contract.execute "time-precision-default-mode" "SET SESSION sql_mode=DEFAULT"
               Contract.execute "time-precision-source-table" "CREATE TABLE temporal_precision_source(n INT)"
               Contract.execute "time-precision-log-table" "CREATE TABLE temporal_precision_log(n INT)"
               Contract.execute "time-precision-captured-mode" "SET SESSION sql_mode='TIME_TRUNCATE_FRACTIONAL'"
               Contract.execute
                   "time-precision-create-trigger"
                   "CREATE TRIGGER temporal_precision_trigger AFTER INSERT ON temporal_precision_source FOR EACH ROW INSERT INTO temporal_precision_log VALUES (MICROSECOND('12:34:56.1234567'))"
               Contract.execute
                   "time-precision-create-function"
                   "CREATE FUNCTION temporal_precision_fn() RETURNS INT DETERMINISTIC NO SQL RETURN MICROSECOND('12:34:56.1234567')"
               Contract.execute "time-precision-restore-mode" "SET SESSION sql_mode=DEFAULT"
               Contract.execute "time-precision-fire-trigger" "INSERT INTO temporal_precision_source VALUES(1)"
               Contract.query "time-precision-captured-results" "SELECT (SELECT n FROM temporal_precision_log),temporal_precision_fn()"
                   |> Contract.comparingValues
               Contract.execute "time-precision-drop-function" "DROP FUNCTION temporal_precision_fn"
               Contract.execute "time-precision-drop-trigger" "DROP TRIGGER temporal_precision_trigger"
               Contract.execute "time-precision-drop-log" "DROP TABLE temporal_precision_log"
               Contract.execute "time-precision-drop-source" "DROP TABLE temporal_precision_source"
               Contract.query
                   "time-constructor-clamp-source"
                   "SELECT SEC_TO_TIME(3020400),SEC_TO_TIME(-3020400),MAKETIME(839,0,0),MAKETIME(-839,0,0),MAKETIME(839,1,2.5),MAKETIME(839,1,'2.500'),MAKETIME(839,1,2e0)"
                   |> Contract.comparingValues
               Contract.query "time-constructor-clamp-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.query
                   "time-arithmetic-clamp-source"
                   "SELECT ADDTIME('838:59:59','00:00:01'),SUBTIME('-838:59:59','00:00:01'),TIMEDIFF('838:59:59','-00:00:01')"
                   |> Contract.comparingValues
               Contract.query "time-arithmetic-clamp-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.query
                   "time-arithmetic-subsecond-source"
                   "SELECT ADDTIME('838:59:59','00:00:00.000001'),TIMEDIFF('838:59:59','-00:00:00.000001')"
                   |> Contract.comparingValues
               Contract.query "time-arithmetic-subsecond-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.query
                   "time-arithmetic-invalid-left-source"
                   "SELECT SUBTIME('839:00:00','00:00:01'),TIMEDIFF('839:00:00','00:00:01')"
                   |> Contract.comparingValues
               Contract.query "time-arithmetic-invalid-left-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.query "time-arithmetic-invalid-add-source" "SELECT ADDTIME('839:00:00','00:00:01')"
                   |> Contract.comparingValues
               Contract.query "time-arithmetic-invalid-add-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               Contract.query "time-arithmetic-invalid-right-source" "SELECT TIMEDIFF('00:00:01','-839:00:00')"
                   |> Contract.comparingValues
               Contract.query "time-arithmetic-invalid-right-warnings" "SHOW WARNINGS" |> Contract.comparingValues |]

        let jsonSteps, jsonCoverage =
            functionProbe
                "json-functions"
                [| "JSON_ARRAY"; "JSON_OBJECT"; "JSON_VALID"; "JSON_TYPE"; "JSON_DEPTH"; "JSON_LENGTH"; "JSON_EXTRACT"
                   "JSON_UNQUOTE"; "JSON_QUOTE"; "JSON_KEYS"; "JSON_CONTAINS"; "JSON_CONTAINS_PATH"; "JSON_OVERLAPS"
                   "JSON_INSERT"; "JSON_REPLACE"; "JSON_SET"; "JSON_REMOVE"; "JSON_ARRAY_APPEND"; "JSON_ARRAY_INSERT"
                   "JSON_MERGE_PATCH"; "JSON_MERGE_PRESERVE"; "JSON_PRETTY"; "JSON_STORAGE_SIZE"; "JSON_STORAGE_FREE"
                   "JSON_SCHEMA_VALID"; "JSON_SCHEMA_VALIDATION_REPORT" |]
                """SELECT JSON_ARRAY(1, 'a'), JSON_OBJECT('a', 1), JSON_VALID('{\"a\":1}'), JSON_TYPE('{\"a\":1}'),
                          JSON_DEPTH('{\"a\":[1]}'), JSON_LENGTH('{\"a\":1}'), JSON_EXTRACT('{\"a\":1}', '$.a'),
                          JSON_UNQUOTE('\"a\"'), JSON_QUOTE('a'), JSON_KEYS('{\"a\":1}'),
                          JSON_CONTAINS('[1,2]', '2'), JSON_CONTAINS_PATH('{\"a\":1}', 'one', '$.a'),
                          JSON_OVERLAPS('[1,2]', '[2,3]'), JSON_INSERT('{\"a\":1}', '$.b', 2),
                          JSON_REPLACE('{\"a\":1}', '$.a', 2), JSON_SET('{\"a\":1}', '$.b', 2),
                          JSON_REMOVE('{\"a\":1,\"b\":2}', '$.b'), JSON_ARRAY_APPEND('[1]', '$', 2),
                          JSON_ARRAY_INSERT('[1,3]', '$[1]', 2), JSON_MERGE_PATCH('{\"a\":1}', '{\"b\":2}'),
                          JSON_MERGE_PRESERVE('[1]', '[2]'), JSON_PRETTY('{\"a\":1}'),
                          JSON_STORAGE_SIZE('{\"a\":1}'), JSON_STORAGE_FREE('{\"a\":1}'),
                          JSON_SCHEMA_VALID('{\"type\":\"integer\"}', '1'),
                          JSON_SCHEMA_VALIDATION_REPORT('{\"type\":\"integer\"}', '\"x\"')"""

        let spatialSteps, spatialCoverage =
            functionProbe
                "spatial-functions"
                [| "ST_GEOMFROMTEXT"; "ST_GEOMETRYFROMTEXT"; "ST_POINTFROMTEXT"; "ST_ASTEXT"; "ST_ASWKT"; "ST_ASBINARY"
                   "ST_ASWKB"; "ST_GEOMETRYTYPE"; "ST_DIMENSION"; "ST_ISEMPTY"; "ST_ISVALID"; "ST_X"; "ST_Y"
                   "ST_DISTANCE"; "ST_CONTAINS"; "ST_WITHIN"; "ST_INTERSECTS"; "ST_DISJOINT"
                   "ST_TOUCHES"; "ST_EQUALS"; "ST_ENVELOPE"; "ST_CONVEXHULL"; "MBRCONTAINS"; "MBRWITHIN"; "MBRINTERSECTS" |]
                """SELECT ST_AsText(ST_GeomFromText('POINT(1 2)')), ST_AsText(ST_GeometryFromText('POINT(1 2)')),
                          ST_AsText(ST_PointFromText('POINT(1 2)')), ST_AsText(ST_GeomFromText('POINT(1 2)')),
                          ST_AsWKT(ST_GeomFromText('POINT(1 2)')), HEX(ST_AsBinary(ST_GeomFromText('POINT(1 2)'))),
                          HEX(ST_AsWKB(ST_GeomFromText('POINT(1 2)'))), ST_GeometryType(ST_GeomFromText('POINT(1 2)')),
                          ST_Dimension(ST_GeomFromText('POINT(1 2)')), ST_IsEmpty(ST_GeomFromText('POINT(1 2)')),
                          ST_IsValid(ST_GeomFromText('POINT(1 2)')), ST_X(ST_GeomFromText('POINT(1 2)')),
                          ST_Y(ST_GeomFromText('POINT(1 2)')),
                          ST_Distance(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(3 4)')),
                          ST_Contains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')),
                          ST_Within(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')),
                          ST_Intersects(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)')),
                          ST_Disjoint(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(2 2)')),
                          ST_Touches(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('LINESTRING(0 0,1 0)')),
                          ST_Equals(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)')),
                          ST_AsText(ST_Envelope(ST_GeomFromText('LINESTRING(0 0,2 2)'))),
                          ST_AsText(ST_ConvexHull(ST_GeomFromText('MULTIPOINT((0 0),(2 0),(0 2))'))),
                          MBRContains(ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))'), ST_GeomFromText('POINT(2 2)')),
                          MBRWithin(ST_GeomFromText('POINT(2 2)'), ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))')),
                          MBRIntersects(ST_GeomFromText('POINT(1 1)'), ST_GeomFromText('POINT(1 1)'))"""

        let typedSpatialConstructorSteps, typedSpatialConstructorCoverage =
            functionProbe
                "typed-spatial-constructors"
                [| "ST_LINEFROMTEXT"
                   "ST_LINESTRINGFROMTEXT"
                   "ST_POLYFROMTEXT"
                   "ST_POLYGONFROMTEXT"
                   "ST_MPOINTFROMTEXT"
                   "ST_MULTIPOINTFROMTEXT"
                   "ST_MLINEFROMTEXT"
                   "ST_MULTILINESTRINGFROMTEXT"
                   "ST_MPOLYFROMTEXT"
                   "ST_MULTIPOLYGONFROMTEXT"
                   "ST_GEOMCOLLFROMTEXT"
                   "ST_GEOMCOLLFROMTXT"
                   "ST_GEOMETRYCOLLECTIONFROMTEXT"
                   "ST_LINEFROMWKB"
                   "ST_LINESTRINGFROMWKB"
                   "ST_POLYFROMWKB"
                   "ST_POLYGONFROMWKB"
                   "ST_MPOINTFROMWKB"
                   "ST_MULTIPOINTFROMWKB"
                   "ST_MLINEFROMWKB"
                   "ST_MULTILINESTRINGFROMWKB"
                   "ST_MPOLYFROMWKB"
                   "ST_MULTIPOLYGONFROMWKB"
                   "ST_GEOMCOLLFROMWKB"
                   "ST_GEOMETRYCOLLECTIONFROMWKB" |]
                """SELECT ST_LineFromText('LINESTRING(0 0,1 1)'), ST_LineStringFromText('LINESTRING(0 0,1 1)'),
                          ST_PolyFromText('POLYGON((0 0,1 0,1 1,0 0))'), ST_PolygonFromText('POLYGON((0 0,1 0,1 1,0 0))'),
                          ST_MPointFromText('MULTIPOINT((0 0),(1 1))'), ST_MultiPointFromText('MULTIPOINT((0 0),(1 1))'),
                          ST_MLineFromText('MULTILINESTRING((0 0,1 1))'), ST_MultiLineStringFromText('MULTILINESTRING((0 0,1 1))'),
                          ST_MPolyFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'), ST_MultiPolygonFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'),
                          ST_GeomCollFromText('GEOMETRYCOLLECTION(POINT(0 0))'), ST_GeomCollFromTxt('GEOMETRYCOLLECTION(POINT(0 0))'),
                          ST_GeometryCollectionFromText('GEOMETRYCOLLECTION(POINT(0 0))'),
                          ST_LineFromWKB(ST_AsWKB(ST_GeomFromText('LINESTRING(0 0,1 1)'))),
                          ST_LineStringFromWKB(ST_AsWKB(ST_GeomFromText('LINESTRING(0 0,1 1)'))),
                          ST_PolyFromWKB(ST_AsWKB(ST_GeomFromText('POLYGON((0 0,1 0,1 1,0 0))'))),
                          ST_PolygonFromWKB(ST_AsWKB(ST_GeomFromText('POLYGON((0 0,1 0,1 1,0 0))'))),
                          ST_MPointFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOINT((0 0),(1 1))'))),
                          ST_MultiPointFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOINT((0 0),(1 1))'))),
                          ST_MLineFromWKB(ST_AsWKB(ST_GeomFromText('MULTILINESTRING((0 0,1 1))'))),
                          ST_MultiLineStringFromWKB(ST_AsWKB(ST_GeomFromText('MULTILINESTRING((0 0,1 1))'))),
                          ST_MPolyFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'))),
                          ST_MultiPolygonFromWKB(ST_AsWKB(ST_GeomFromText('MULTIPOLYGON(((0 0,1 0,1 1,0 0)))'))),
                          ST_GeomCollFromWKB(ST_AsWKB(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))'))),
                          ST_GeometryCollectionFromWKB(ST_AsWKB(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0))')))"""

        let spatialPropertyEdgeSteps =
            [| Contract.query
                   "spatial-property-family-and-index-rules"
                   "SELECT ST_IsClosed(ST_GeomFromText('POINT(1 2)')), ST_NumPoints(ST_GeomFromText('MULTILINESTRING((0 0,1 1))')), ST_NumGeometries(ST_GeomFromText('POINT(1 2)')), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), -1)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), 1.5)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), '1.9')), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(0 0,1 1,2 2)'), CAST(3.9 AS DOUBLE)))"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spatial-property-contextual-index-coercion"
                   "SELECT ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 0,20 0,30 0)'), ?))"
                   [| box 1.5; box 1.5M; box "1.9" |]
               |> Contract.comparingValues
               Contract.query
                   "spatial-property-srid-retention"
                   "SELECT ST_Srid(ST_PointN(ST_GeomFromText('LINESTRING(10 20,11 21)', 4326, 'axis-order=long-lat'), 1)), ST_AsText(ST_PointN(ST_GeomFromText('LINESTRING(10 20,11 21)', 4326, 'axis-order=long-lat'), 1))"
               |> Contract.comparingValues |]

        let aggregateSteps, aggregateCoverage =
            functionProbe
                "aggregate-functions"
                [| "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "STD"; "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"; "VARIANCE"
                   "VAR_POP"; "VAR_SAMP"; "BIT_AND"; "BIT_OR"; "BIT_XOR" |]
                """SELECT COUNT(*), SUM(n), AVG(n), MIN(n), MAX(n), STD(n), STDDEV(n), STDDEV_POP(n), STDDEV_SAMP(n),
                          VARIANCE(n), VAR_POP(n), VAR_SAMP(n), BIT_AND(n), BIT_OR(n), BIT_XOR(n)
                   FROM (SELECT 1 AS n UNION ALL SELECT 2 UNION ALL SELECT 3) AS valueset"""

        let errorSpecs =
            [| "abs-arity", "ABS", 1582, "42000", "SELECT ABS()"
               "pow-arity", "POW", 1582, "42000", "SELECT POW(2)"
               "char-length-arity", "CHAR_LENGTH", 1582, "42000", "SELECT CHAR_LENGTH()"
               "json-extract-arity", "JSON_EXTRACT", 1582, "42000", "SELECT JSON_EXTRACT('{}')"
               "regexp-like-arity", "REGEXP_LIKE", 1582, "42000", "SELECT REGEXP_LIKE('a')"
               "date-format-arity", "DATE_FORMAT", 1582, "42000", "SELECT DATE_FORMAT('2024-01-01')"
               "sec-to-time-arity", "SEC_TO_TIME", 1582, "42000", "SELECT SEC_TO_TIME()"
               "spatial-distance-arity", "ST_DISTANCE", 1582, "42000", "SELECT ST_DISTANCE(ST_GeomFromText('POINT(0 0)'))"
               "aggregate-sum-arity", "SUM", 1064, "42000", "SELECT SUM(1, 2)" |]

        let errorSteps, errorCoverage =
            errorSpecs
            |> Array.map (fun (name, functionName, code, sqlState, sql) -> functionError name functionName code sqlState sql)
            |> Array.unzip

        { Name = "function-family-contracts"
          Setup = [| "SET SESSION time_zone = '+00:00'" |]
          Steps =
            Array.concat
                [ numericSteps
                  stringSteps
                  temporalSteps
                  temporalEdgeSteps
                  jsonSteps
                  spatialSteps
                  typedSpatialConstructorSteps
                  spatialPropertyEdgeSteps
                  aggregateSteps
                  errorSteps ]
          Cleanup = [||]
          Coverage =
            Array.concat
                [ numericCoverage
                  stringCoverage
                  temporalCoverage
                  jsonCoverage
                  spatialCoverage
                  typedSpatialConstructorCoverage
                  aggregateCoverage
                  errorCoverage ] }

    let private aggregateFunctions =
        let foldFunctions =
            [| "COUNT"; "SUM"; "AVG"; "MIN"; "MAX"; "STD"; "STDDEV"; "STDDEV_POP"; "STDDEV_SAMP"; "VARIANCE"
               "VAR_POP"; "VAR_SAMP"; "BIT_AND"; "BIT_OR"; "BIT_XOR" |]

        let directFunctions = [| "GROUP_CONCAT"; "JSON_ARRAYAGG"; "JSON_OBJECTAGG" |]

        let projection =
            "COUNT(*), COUNT(n), SUM(n), AVG(n), MIN(n), MAX(n), STD(n), STDDEV(n), STDDEV_POP(n), "
            + "STDDEV_SAMP(n), VARIANCE(n), VAR_POP(n), VAR_SAMP(n), BIT_AND(bits), BIT_OR(bits), BIT_XOR(bits)"

        let errors =
            foldFunctions
            |> Array.map (fun name ->
                Contract.query
                    ("aggregate-" + name.ToLowerInvariant() + "-error")
                    (sprintf "SELECT %s(n, n) FROM contract_aggregates" name)
                |> Contract.fails 1064 "42000")

        let directErrors =
            [| "GROUP_CONCAT", "GROUP_CONCAT()"
               "JSON_ARRAYAGG", "JSON_ARRAYAGG(n, n)"
               "JSON_OBJECTAGG", "JSON_OBJECTAGG(n)" |]
            |> Array.map (fun (name, expression) ->
                Contract.query
                    ("aggregate-" + name.ToLowerInvariant() + "-error")
                    ("SELECT " + expression + " FROM contract_aggregates")
                |> Contract.fails 1064 "42000")

        { Name = "aggregate-function-contracts"
          Setup =
            [| "DROP TABLE IF EXISTS contract_aggregates"
               "CREATE TABLE contract_aggregates (grp INT NOT NULL, n INT, bits INT, label VARCHAR(10))"
               "INSERT INTO contract_aggregates VALUES (1, 1, 1, 'a'), (1, 2, 2, 'b'), (1, 3, 4, 'c'), (1, NULL, NULL, 'd'), (2, NULL, NULL, 'e')" |]
          Steps =
            Array.concat
                [ [| Contract.query
                         "aggregate-populated-text"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 1")
                     |> Contract.comparingValues
                     Contract.preparedQuery
                         "aggregate-populated-prepared"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = ?")
                         [| box 1 |]
                     |> Contract.comparingValues
                     Contract.query
                         "aggregate-all-null"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 2")
                     |> Contract.comparingValues
                     Contract.query
                         "aggregate-empty"
                         ("SELECT " + projection + " FROM contract_aggregates WHERE grp = 99")
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-populated-text"
                         "SELECT GROUP_CONCAT(label ORDER BY label SEPARATOR ','), JSON_ARRAYAGG(n), JSON_OBJECTAGG(label, n) FROM contract_aggregates WHERE grp = 1 AND n = 2"
                     |> Contract.comparingValues
                     Contract.preparedQuery
                         "direct-aggregate-populated-prepared"
                         "SELECT GROUP_CONCAT(label ORDER BY label SEPARATOR ','), JSON_ARRAYAGG(n), JSON_OBJECTAGG(label, n) FROM contract_aggregates WHERE grp = ? AND n = 2"
                         [| box 1 |]
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-all-null"
                         "SELECT GROUP_CONCAT(n), JSON_ARRAYAGG(n), JSON_OBJECTAGG('key', n) FROM contract_aggregates WHERE grp = 2"
                     |> Contract.comparingValues
                     Contract.query
                         "direct-aggregate-empty"
                         "SELECT GROUP_CONCAT(n), JSON_ARRAYAGG(n), JSON_OBJECTAGG('key', n) FROM contract_aggregates WHERE grp = 99"
                     |> Contract.comparingValues |]
                  errors
                  directErrors ]
          Cleanup = [| "DROP TABLE IF EXISTS contract_aggregates" |]
          Coverage =
            Array.append foldFunctions directFunctions
            |> Array.map (fun name ->
                "function:" + name.ToLowerInvariant(),
                [| "parser"; "text-differential"; "null-semantics"; "error-contract"; "prepared-protocol"; "result-type" |]) }

    let private geographicSpatial =
        let oslo = "POINT(59.9139 10.7522)"
        let london = "POINT(51.5074 -0.1278)"

        { Name = "geographic-spatial-contracts"
          Setup = [||]
          Steps =
            [| Contract.query
                   "geographic-distance-metres"
                   (sprintf
                       "SELECT ROUND(ST_Distance(ST_GeomFromText('%s', 4326), ST_GeomFromText('%s', 4326)), 3)"
                       oslo
                       london)
               |> Contract.comparingValues
               Contract.preparedQuery
                   "geographic-distance-unit-prepared"
                   "SELECT ROUND(ST_Distance(ST_GeomFromText(?, 4326), ST_GeomFromText(?, 4326), ?), 6)"
                   [| box oslo; box london; box "kilometre" |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-longitude-latitude-input"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(10.7522 59.9139)', 4326, 'axis-order=long-lat'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-wkb-longitude-latitude-input"
                   "SELECT ST_AsText(ST_GeomFromWKB(ST_AsWKB(ST_GeomFromText('POINT(10.7522 59.9139)')), 4326, 'axis-order=long-lat'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-text-output-axis-order"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), 'axis-order=long-lat'), ST_AsWKT(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ' axis-order = lat-long '), ST_AsText(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), '')"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "geographic-binary-output-axis-order-prepared"
                   "SELECT ST_AsText(ST_GeomFromWKB(ST_AsWKB(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ?))), ST_AsText(ST_GeomFromWKB(ST_AsBinary(ST_GeomFromText('POINT(59.9139 10.7522)', 4326), ?)))"
                   [| box "axis-order=long-lat"; box "axis-order=lat-long" |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-output-invalid-axis-order"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(0 0)', 4326), 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "geographic-output-axis-order-null"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(0 0)', 4326), NULL), ST_AsWKB(NULL, 'axis-order=long-lat')"
               |> Contract.comparingValues
               Contract.query
                   "geographic-output-axis-order-arity"
                   "SELECT ST_AsWKB(ST_GeomFromText('POINT(0 0)', 4326), 'axis-order=long-lat', 'extra')"
               |> Contract.fails 1582 "42000"
               Contract.preparedQuery
                   "geographic-null-distance"
                   "SELECT ST_Distance(ST_GeomFromText(?, 4326), ST_GeomFromText(?, 4326))"
                   [| box DBNull.Value; box london |]
               |> Contract.comparingValues
               Contract.query
                   "geographic-mismatched-srid"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 0), ST_GeomFromText('POINT(0 0)', 4326))"
               |> Contract.fails 3033 "HY000"
               Contract.query
                   "geographic-latitude-domain"
                   "SELECT ST_GeomFromText('POINT(91 0)', 4326)"
               |> Contract.fails 3617 "22S03"
               Contract.query
                   "geographic-longitude-domain"
                   "SELECT ST_GeomFromText('POINT(0 181)', 4326)"
               |> Contract.fails 3616 "22S02"
               Contract.query
                   "geographic-unknown-srid"
                   "SELECT ST_GeomFromText('POINT(0 0)', 9999)"
               |> Contract.fails 3548 "SR001"
               Contract.query
                   "geographic-invalid-axis-order"
                   "SELECT ST_GeomFromText('POINT(0 0)', 4326, 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "planar-invalid-axis-order"
                   "SELECT ST_GeomFromText('POINT(0 0)', 0, 'axis-order=bogus')"
               |> Contract.fails 3559 "22023"
               Contract.query
                   "geographic-unknown-unit"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 4326), ST_GeomFromText('POINT(1 1)', 4326), 'bogus')"
               |> Contract.fails 3902 "SU001"
               Contract.query
                   "planar-distance-has-no-length-unit"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(0 0)', 0), ST_GeomFromText('POINT(1 1)', 0), 'metre')"
               |> Contract.fails 3882 "SU001"
               Contract.query
                   "spherical-distance-srid-zero"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 1)')), 6)"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spherical-distance-custom-radius-prepared"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText(?), ST_GeomFromText(?), ?), 9)"
                   [| box "POINT(0 0)"; box "POINT(0 1)"; box 1000.0 |]
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-wgs84"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)', 4326), ST_GeomFromText('POINT(0 1)', 4326)), 6)"
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-multipoint"
                   "SELECT ROUND(ST_Distance_Sphere(ST_GeomFromText('MULTIPOINT(0 0,10 10)'), ST_GeomFromText('POINT(1 0)')), 6)"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spherical-distance-null"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText(?), ST_GeomFromText(?))"
                   [| box DBNull.Value; box "POINT(0 1)" |]
               |> Contract.comparingValues
               Contract.query
                   "spherical-distance-nonpositive-radius"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('POINT(0 0)'), ST_GeomFromText('POINT(0 1)'), 0)"
               |> Contract.fails 3706 "22003"
               Contract.query
                   "planar-linestring-length"
                   "SELECT ST_Length(ST_GeomFromText('LINESTRING(0 0,3 4)'))"
               |> Contract.comparingValues
               Contract.query
                   "geographic-linestring-length"
                   "SELECT ROUND(ST_Length(ST_GeomFromText('LINESTRING(59.9139 10.7522,51.5074 -0.1278)', 4326)), 3)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-planar-point"
                   "SELECT ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),0.5)),ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),0.75))"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-planar-points"
                   "SELECT ST_AsText(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),0.25)),ST_AsText(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),0.3))"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-points-beyond-geometry-setting"
                   "SELECT ST_NumGeometries(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)'),0.00001))"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-planar-distance"
                   "SELECT ST_AsText(ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),7.5)),ST_AsText(ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)'),10))"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-boundaries"
                   "SELECT ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,1 1)'),0)),ST_AsText(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)'),0)),ST_AsText(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)'),1)),ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,0 0)'),0.5))"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-geographic-point"
                   "SELECT ROUND(ST_X(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),0.5)),8),ROUND(ST_Y(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),0.5)),8)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-geographic-northwest"
                   "SELECT ROUND(ST_X(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(59.9139 10.7522,51.5074 -0.1278)',4326),0.5)),7),ROUND(ST_Y(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(59.9139 10.7522,51.5074 -0.1278)',4326),0.5)),7)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-geographic-antimeridian"
                   "SELECT ROUND(ST_X(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(10 179,10 -179)',4326),0.5)),6),ROUND(ST_Y(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(10 179,10 -179)',4326),0.5)),6)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-geographic-points"
                   "SELECT ST_NumGeometries(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),0.25)),ROUND(ST_X(ST_GeometryN(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),0.25),2)),8),ROUND(ST_Y(ST_GeometryN(ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),0.25),2)),8)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-geographic-distance"
                   "SELECT ROUND(ST_X(ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),100000)),8),ROUND(ST_Y(ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),100000)),8)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-projected"
                   "SELECT ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)',3395),0.75)),ST_SRID(ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,0 5,5 5)',3857),7.5))"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "line-interpolate-prepared"
                   "SELECT ST_AsText(ST_LineInterpolatePoint(ST_GeomFromText(?),?))"
                   [| box "LINESTRING(0 0,0 5,5 5)"; box 0.75 |]
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-null"
                   "SELECT ST_LineInterpolatePoint(NULL,0.5),ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)'),NULL),ST_PointAtDistance(NULL,1)"
               |> Contract.comparingValues
               Contract.query
                   "line-interpolate-type-error"
                   "SELECT ST_LineInterpolatePoint(ST_GeomFromText('POINT(0 0)'),0.5)"
               |> Contract.fails 3516 "22S01"
               Contract.query
                   "line-interpolate-range-error"
                   "SELECT ST_LineInterpolatePoints(ST_GeomFromText('LINESTRING(0 0,1 1)'),1.1)"
               |> Contract.fails 1690 "22003"
               Contract.query
                   "point-at-distance-range-error"
                   "SELECT ST_PointAtDistance(ST_GeomFromText('LINESTRING(0 0,1 1)'),-1)"
               |> Contract.fails 1690 "22003"
               Contract.preparedQuery
                   "geographic-multilinestring-length-unit-prepared"
                   "SELECT ROUND(ST_Length(ST_GeomFromText(?, 4326), ?), 6)"
                   [| box "MULTILINESTRING((0 0,0 1),(0 0,1 0))"; box "kilometre" |]
               |> Contract.comparingValues
               Contract.query
                   "point-length-is-null"
                   "SELECT ST_Length(ST_GeomFromText('POINT(0 0)'))"
               |> Contract.comparingValues
               Contract.query
                   "point-length-with-unit-is-null"
                   "SELECT ST_Length(ST_GeomFromText('POINT(0 0)'), 'metre')"
               |> Contract.comparingValues
               Contract.query
                   "planar-length-has-no-unit"
                   "SELECT ST_Length(ST_GeomFromText('LINESTRING(0 0,1 1)'), 'metre')"
               |> Contract.fails 3882 "SU001"
               Contract.query
                   "spatial-length-arity"
                   "SELECT ST_Length()"
               |> Contract.fails 1582 "42000"
               Contract.query
                   "geographic-srs-catalog"
                   "SELECT SRS_NAME, SRS_ID, ORGANIZATION, ORGANIZATION_COORDSYS_ID, DESCRIPTION FROM information_schema.ST_SPATIAL_REFERENCE_SYSTEMS WHERE SRS_ID = 4326"
               |> Contract.comparingValues
               Contract.query
                   "projected-srs-catalog"
                   "SELECT SRS_NAME,SRS_ID,ORGANIZATION,ORGANIZATION_COORDSYS_ID,DEFINITION,DESCRIPTION FROM information_schema.ST_SPATIAL_REFERENCE_SYSTEMS WHERE SRS_ID=3857"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-srs-catalog"
                   "SELECT SRS_NAME,SRS_ID,ORGANIZATION,ORGANIZATION_COORDSYS_ID,DEFINITION,DESCRIPTION FROM information_schema.ST_SPATIAL_REFERENCE_SYSTEMS WHERE SRS_ID=3395"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-forward"
                   "SELECT ST_SRID(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3395)),ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3395)),5),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3395)),5)"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-reverse"
                   "SELECT ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(1196929.428907435 8343586.513829184)',3395),4326)),6),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(1196929.428907435 8343586.513829184)',3395),4326)),6)"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-from-web-mercator"
                   "SELECT ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(1000000 8000000)',3857),3395)),5),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(1000000 8000000)',3857),3395)),5)"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-to-web-mercator"
                   "SELECT ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(1000000 8000000)',3395),3857)),5),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(1000000 8000000)',3395),3857)),5)"
               |> Contract.comparingValues
               Contract.query
                   "world-mercator-planar-distance"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(1 2)',3395),ST_GeomFromText('POINT(4 6)',3395))"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-forward"
                   "SELECT ST_SRID(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3857)),ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3857)),5),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(59.9139 10.7522)',4326),3857)),5)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-reverse"
                   "SELECT ST_SRID(ST_Transform(ST_GeomFromText('POINT(1196929.428907435 8380593.569947172)',3857),4326)),ROUND(ST_X(ST_Transform(ST_GeomFromText('POINT(1196929.428907435 8380593.569947172)',3857),4326)),6),ROUND(ST_Y(ST_Transform(ST_GeomFromText('POINT(1196929.428907435 8380593.569947172)',3857),4326)),6)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-linestring"
                   "SELECT ST_SRID(ST_Transform(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),3857)),ROUND(ST_X(ST_PointN(ST_Transform(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),3857),2)),5),ROUND(ST_Y(ST_PointN(ST_Transform(ST_GeomFromText('LINESTRING(0 0,1 1)',4326),3857),2)),5)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-collection-member"
                   "SELECT ST_SRID(ST_GeometryN(ST_Transform(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,1 1))',4326),3857),1)),ROUND(ST_X(ST_GeometryN(ST_Transform(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(0 0),LINESTRING(0 0,1 1))',4326),3857),1)),5)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-same-srid-and-null"
                   "SELECT ST_AsText(ST_Transform(ST_GeomFromText('POINT(1 2)',4326),4326)),ST_AsText(ST_Transform(ST_GeomFromText('POINT(1 2)'),0)),ST_Transform(NULL,3857),ST_Transform(ST_GeomFromText('POINT(1 2)',4326),NULL)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-mercator-pole"
                   "SELECT ST_Transform(ST_GeomFromText('POINT(90 0)',4326),3857) IS NULL,ST_AsText(ST_Transform(ST_GeomFromText('POINT(90 0)',4326),3857)) IS NULL,ST_Transform(ST_GeomFromText('POINT(-90 0)',4326),3395) IS NULL,ST_AsText(ST_Transform(ST_GeomFromText('POINT(-90 0)',4326),3395)) IS NULL"
               |> Contract.comparingValues
               Contract.query
                   "spatial-transform-mercator-pole-invalid-srid-read"
                   "SELECT ST_SRID(ST_Transform(ST_GeomFromText('POINT(90 0)',4326),3857))"
               |> Contract.fails 3037 "22023"
               Contract.query
                   "spatial-transform-from-srid-zero"
                   "SELECT ST_Transform(ST_GeomFromText('POINT(0 0)'),3857)"
               |> Contract.fails 3741 "22S00"
               Contract.query
                   "spatial-transform-to-srid-zero"
                   "SELECT ST_Transform(ST_GeomFromText('POINT(0 0)',3857),0)"
               |> Contract.fails 3742 "22S00"
               Contract.query
                   "spatial-transform-unknown-srid"
                   "SELECT ST_Transform(ST_GeomFromText('POINT(0 0)',4326),9999)"
               |> Contract.fails 3548 "SR001"
               Contract.query
                   "spatial-swapxy-point-and-line"
                   "SELECT ST_AsText(ST_SwapXY(ST_GeomFromText('POINT(1 2)'))),ST_AsText(ST_SwapXY(ST_GeomFromText('LINESTRING(0 1,2 3)'))),ST_SRID(ST_SwapXY(ST_GeomFromText('POINT(1 2)',3857)))"
               |> Contract.comparingValues
               Contract.query
                   "spatial-swapxy-collection"
                   "SELECT ST_AsText(ST_SwapXY(ST_GeomFromText('GEOMETRYCOLLECTION(POINT(1 2),LINESTRING(3 4,5 6))')))"
               |> Contract.comparingValues
               Contract.preparedQuery
                   "spatial-swapxy-prepared"
                   "SELECT ST_AsText(ST_SwapXY(ST_GeomFromText(?)))"
                   [| box "POINT(1 2)" |]
               |> Contract.comparingValues
               Contract.query
                   "spatial-swapxy-geographic-domain"
                   "SELECT ST_AsText(ST_SwapXY(ST_GeomFromText('POINT(20 100)',4326)))"
               |> Contract.comparingValues
               Contract.query
                   "spatial-swapxy-null"
                   "SELECT ST_SwapXY(NULL)"
               |> Contract.comparingValues
               Contract.query
                   "spatial-swapxy-arity"
                   "SELECT ST_SwapXY(ST_GeomFromText('POINT(1 2)'),1)"
               |> Contract.fails 1582 "42000"
               Contract.query
                   "projected-point-axis-order"
                   "SELECT ST_AsText(ST_GeomFromText('POINT(1 2)',3857)),ST_AsText(ST_GeomFromText('POINT(1 2)',3857,'axis-order=lat-long'))"
               |> Contract.comparingValues
               Contract.query
                   "projected-point-distance"
                   "SELECT ST_Distance(ST_GeomFromText('POINT(1 2)',3857),ST_GeomFromText('POINT(4 6)',3857))"
               |> Contract.comparingValues
               Contract.query
                   "projected-point-distance-in-feet"
                   "SELECT ROUND(ST_Distance(ST_GeomFromText('POINT(1 2)',3857),ST_GeomFromText('POINT(4 6)',3857),'foot'),6)"
               |> Contract.comparingValues
               Contract.query
                   "projected-nonpoint-distance"
                   "SELECT ST_Distance(ST_GeomFromText('LINESTRING(0 0,4 0)',3857),ST_GeomFromText('POINT(2 3)',3857))"
               |> Contract.comparingValues
               Contract.query
                   "projected-line-length-in-feet"
                   "SELECT ROUND(ST_Length(ST_GeomFromText('LINESTRING(0 0,3 4)',3857),'foot'),6)"
               |> Contract.comparingValues
               Contract.query
                   "projected-envelope"
                   "SELECT ST_AsText(ST_Envelope(ST_GeomFromText('LINESTRING(1 2,4 6)',3857)))"
               |> Contract.comparingValues
               Contract.query
                   "projected-spherical-distance-refusal"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('POINT(1 2)',3857),ST_GeomFromText('POINT(4 6)',3857))"
               |> Contract.fails 3705 "22S00"
               Contract.query
                   "projected-spherical-custom-radius-refusal"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('POINT(1 2)',3857),ST_GeomFromText('POINT(4 6)',3857),1000)"
               |> Contract.fails 3705 "22S00"
               Contract.query
                   "projected-spherical-multipoint-refusal"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('MULTIPOINT((1 2),(4 6))',3857),ST_GeomFromText('POINT(4 6)',3857))"
               |> Contract.fails 3705 "22S00"
               Contract.query
                   "projected-spherical-invalid-radius"
                   "SELECT ST_Distance_Sphere(ST_GeomFromText('POINT(1 2)',3857),ST_GeomFromText('POINT(4 6)',3857),0)"
               |> Contract.fails 3706 "22003" |]
          Cleanup = [||]
          Coverage =
            [| "function:st_distance",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract" |]
               "function:st_geomfromtext",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract" |]
               "function:st_geomfromwkb", [| "parser"; "text-differential"; "error-contract" |]
               "function:st_distance_sphere",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract"; "result-type" |]
               "function:st_length",
               [| "parser"; "text-differential"; "prepared-protocol"; "null-semantics"; "error-contract"; "result-type" |]
               "table:information_schema.st_spatial_reference_systems", [| "text-differential" |] |] }

    let private preparedInvalidation =
        { Name = "prepared-ddl-invalidation"
          Setup = [| "DROP TABLE IF EXISTS contract_reprepare"; "CREATE TABLE contract_reprepare (id INT PRIMARY KEY)"; "INSERT INTO contract_reprepare VALUES (1)" |]
          Steps =
            [| Contract.prepare "prepare-select" "select-handle" Query "SELECT id FROM contract_reprepare ORDER BY id" [||]
               Contract.invoke "execute-before-alter" "select-handle" OracleSuccess
               Contract.execute "alter-under-handle" "ALTER TABLE contract_reprepare ADD COLUMN label VARCHAR(10) DEFAULT 'x'"
               Contract.invoke "execute-after-alter" "select-handle" OracleSuccess
               Contract.execute "drop-under-handle" "DROP TABLE contract_reprepare"
               Contract.invoke "execute-after-drop" "select-handle" (OracleError(1146, "42S02"))
               Contract.close "close-handle" "select-handle" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_reprepare" |]
          Coverage =
            [| "statement:select", [| "prepared-protocol"; "error-contract" |]
               "statement:alter_table", [| "prepared-protocol" |]
               "statement:drop_table", [| "prepared-protocol" |] |] }

    let private preparedDml =
        { Name = "prepared-dml"
          Setup =
            [| "DROP TABLE IF EXISTS contract_dml_target"
               "DROP TABLE IF EXISTS contract_dml_source"
               "CREATE TABLE contract_dml_target (id INT PRIMARY KEY, label VARCHAR(20))"
               "CREATE TABLE contract_dml_source (id INT PRIMARY KEY, label VARCHAR(20))"
               "INSERT INTO contract_dml_source VALUES (2, 'two'), (3, 'three')" |]
          Steps =
            [| Contract.preparedExecute "prepared-insert" "INSERT INTO contract_dml_target VALUES (?, ?)" [| box 1; box "one" |]
               Contract.preparedExecute
                   "prepared-insert-select"
                   "INSERT INTO contract_dml_target SELECT id, label FROM contract_dml_source WHERE id = ?"
                   [| box 2 |]
               Contract.preparedExecute
                   "prepared-update"
                   "UPDATE contract_dml_target SET label = ? WHERE id = ?"
                   [| box "ONE"; box 1 |]
               Contract.preparedExecute "prepared-replace" "REPLACE INTO contract_dml_target VALUES (?, ?)" [| box 1; box "replaced" |]
               Contract.preparedExecute
                   "prepared-replace-select"
                   "REPLACE INTO contract_dml_target SELECT id, label FROM contract_dml_source WHERE id = ?"
                   [| box 3 |]
               Contract.query "prepared-dml-state" "SELECT id, label FROM contract_dml_target ORDER BY id"
               Contract.preparedExecute "prepared-delete" "DELETE FROM contract_dml_target WHERE id = ?" [| box 2 |]
               Contract.preparedQuery "prepared-dml-final" "SELECT id, label FROM contract_dml_target WHERE id >= ? ORDER BY id" [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_dml_target"; "DROP TABLE IF EXISTS contract_dml_source" |]
          Coverage =
            [| for statement in [ "insert"; "insert_select"; "update"; "replace"; "replace_select"; "delete" ] do
                   yield "statement:" + statement, [| "prepared-protocol" |] |] }

    let private implicitCommit =
        { Name = "implicit-ddl-commit"
          Setup = [| "DROP TABLE IF EXISTS contract_commit"; "DROP TABLE IF EXISTS contract_ddl"; "CREATE TABLE contract_commit (id INT PRIMARY KEY)" |]
          Steps =
            [| Contract.execute "begin" "START TRANSACTION"
               Contract.execute "write-before-ddl" "INSERT INTO contract_commit VALUES (1)"
               Contract.execute "ddl-commits-transaction" "CREATE TABLE contract_ddl (id INT PRIMARY KEY)"
               Contract.execute "rollback-after-ddl" "ROLLBACK"
               Contract.query "write-survived" "SELECT id FROM contract_commit"
               Contract.query
                   "ddl-survived"
                   "SELECT COUNT(*) AS found FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'contract_ddl'" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_ddl"; "DROP TABLE IF EXISTS contract_commit" |]
          Coverage =
            [| "statement:create_table", [| "text-differential"; "concurrency" |]
               "statement:insert", [| "text-differential"; "concurrency" |] |] }

    let private semanticErrors =
        { Name = "semantic-error-contracts"
          Setup =
            [| "DROP TABLE IF EXISTS contract_errors"
               "CREATE TABLE contract_errors (id INT PRIMARY KEY, n INT NOT NULL)"
               "INSERT INTO contract_errors VALUES (1, 10), (2, 20)" |]
          Steps =
            [| Contract.execute "duplicate-key" "INSERT INTO contract_errors VALUES (1, 99)" |> Contract.fails 1062 "23000"
               Contract.query "unknown-column" "SELECT missing FROM contract_errors" |> Contract.fails 1054 "42S22"
               Contract.execute "wrong-value-count" "INSERT INTO contract_errors VALUES (3)" |> Contract.fails 1136 "21S01"
               Contract.query "scalar-subquery-cardinality" "SELECT (SELECT id FROM contract_errors)" |> Contract.fails 1242 "21000"
               Contract.query "row-arity" "SELECT (1, 2) = (1, 2, 3)" |> Contract.fails 1241 "21000"
               Contract.query "empty-row-in-arity" "SELECT (1, 2) IN (SELECT id FROM contract_errors WHERE 0)"
               |> Contract.fails 1241 "21000"
               Contract.query "empty-row-in-valid" "SELECT (1, 2) IN (SELECT id, n FROM contract_errors WHERE 0)"
               |> Contract.comparingValues
               Contract.query "state-unchanged" "SELECT id, n FROM contract_errors ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_errors" |]
          Coverage =
            [| "statement:insert", [| "error-contract" |]
               "statement:select", [| "error-contract" |] |] }

    let private concurrentSessions =
        { Name = "named-connections-send-reap"
          Setup = [| "DROP TABLE IF EXISTS contract_parallel"; "CREATE TABLE contract_parallel (id INT PRIMARY KEY, n INT NOT NULL)" |]
          Steps =
            [| Contract.execute "first-write" "INSERT INTO contract_parallel VALUES (1, 10)"
               |> Contract.on "writer-one"
               |> Contract.send "first"
               Contract.execute "second-write" "INSERT INTO contract_parallel VALUES (2, 20)"
               |> Contract.on "writer-two"
               |> Contract.send "second"
               Contract.reap "first-complete" "writer-one" "first" OracleSuccess
               Contract.reap "second-complete" "writer-two" "second" OracleSuccess
               Contract.query "combined-state" "SELECT id, n FROM contract_parallel ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_parallel" |]
          Coverage = [| "statement:insert", [| "concurrency" |] |] }

    let private contendedSchedule =
        { Name = "contended-send-reap"
          Setup = [| "DROP TABLE IF EXISTS contract_lock"; "CREATE TABLE contract_lock (id INT PRIMARY KEY, n INT NOT NULL)"; "INSERT INTO contract_lock VALUES (1, 10)" |]
          Steps =
            [| Contract.execute "owner-begin" "START TRANSACTION" |> Contract.on "owner"
               Contract.execute "owner-locks-row" "UPDATE contract_lock SET n = n + 1 WHERE id = 1" |> Contract.on "owner"
               Contract.execute "waiter-begin" "START TRANSACTION" |> Contract.on "waiter"
               Contract.execute "waiter-update" "UPDATE contract_lock SET n = n + 1 WHERE id = 1"
               |> Contract.on "waiter"
               |> Contract.send "blocked-update"
               Contract.awaitPending "waiter-is-blocked" "blocked-update"
               Contract.execute "owner-commit" "COMMIT" |> Contract.on "owner"
               Contract.reap "waiter-completes" "waiter" "blocked-update" OracleSuccess
               Contract.execute "waiter-commit" "COMMIT" |> Contract.on "waiter"
               Contract.query "serialized-state" "SELECT id, n FROM contract_lock" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_lock" |]
          Coverage = [| "statement:update", [| "concurrency" |]; "statement:select", [| "concurrency" |] |] }

    let private contendedDeleteAndInsert =
        { Name = "contended-delete-and-insert"
          Setup =
            [| "DROP TABLE IF EXISTS contract_lock_mix"
               "CREATE TABLE contract_lock_mix (id INT PRIMARY KEY, n INT NOT NULL)"
               "INSERT INTO contract_lock_mix VALUES (1, 10)" |]
          Steps =
            [| Contract.execute "delete-owner-begin" "START TRANSACTION" |> Contract.on "delete-owner"
               Contract.execute "delete-owner-lock" "UPDATE contract_lock_mix SET n = n + 1 WHERE id = 1" |> Contract.on "delete-owner"
               Contract.execute "delete-waiter-begin" "START TRANSACTION" |> Contract.on "delete-waiter"
               Contract.execute "delete-waits" "DELETE FROM contract_lock_mix WHERE id = 1"
               |> Contract.on "delete-waiter"
               |> Contract.send "blocked-delete"
               Contract.awaitPending "delete-is-blocked" "blocked-delete"
               Contract.execute "delete-owner-commit" "COMMIT" |> Contract.on "delete-owner"
               Contract.reap "delete-completes" "delete-waiter" "blocked-delete" OracleSuccess
               Contract.execute "delete-waiter-commit" "COMMIT" |> Contract.on "delete-waiter"
               Contract.execute "insert-owner-begin" "START TRANSACTION" |> Contract.on "insert-owner"
               Contract.execute "insert-owner-reserves-key" "INSERT INTO contract_lock_mix VALUES (2, 20)" |> Contract.on "insert-owner"
               Contract.execute "insert-waiter-begin" "START TRANSACTION" |> Contract.on "insert-waiter"
               Contract.execute "insert-waits" "INSERT INTO contract_lock_mix VALUES (2, 21)"
               |> Contract.on "insert-waiter"
               |> Contract.send "blocked-insert"
               Contract.awaitPending "insert-is-blocked" "blocked-insert"
               Contract.execute "insert-owner-rolls-back" "ROLLBACK" |> Contract.on "insert-owner"
               Contract.reap "insert-completes" "insert-waiter" "blocked-insert" OracleSuccess
               Contract.execute "insert-waiter-commit" "COMMIT" |> Contract.on "insert-waiter"
               Contract.query "contended-mixed-state" "SELECT id, n FROM contract_lock_mix ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_lock_mix" |]
          Coverage =
            [| "statement:delete", [| "concurrency" |]
               "statement:insert", [| "concurrency" |] |] }

    let private namedTimeZones =
        { Name = "named-time-zones"
          Setup =
            [| "INSERT INTO mysql.time_zone (Time_zone_id,Use_leap_seconds) VALUES (900002,'N')"
               "INSERT INTO mysql.time_zone_name VALUES ('Fsdb/Contract_Eastern',900002)"
               "INSERT INTO mysql.time_zone_transition_type (Time_zone_id, Transition_type_id, Offset, Is_DST, Abbreviation) VALUES (900002,0,-18000,0,'EST'),(900002,1,-14400,1,'EDT')"
               "INSERT INTO mysql.time_zone_transition VALUES (900002,1710054000,1),(900002,1730613600,0)"
               "CREATE TABLE contract_zone (t TIMESTAMP(6), d DATETIME(6))" |]
          Steps =
            [| Contract.query "gap-and-fold"
                   "SELECT CONVERT_TZ('2024-03-10 02:30:12.123456','Fsdb/Contract_Eastern','+00:00') AS gap_value, CONVERT_TZ('2024-11-03 01:30:12.123456','Fsdb/Contract_Eastern','+00:00') AS fold_value"
               Contract.preparedQuery "bound-zone-conversion"
                   "SELECT CONVERT_TZ(?, ?, ?) AS local_value, CONVERT_TZ(?, ?, ?) AS utc_value"
                   [| box "2024-03-10 06:59:59.999999"; box "+00:00"; box "Fsdb/Contract_Eastern"
                      box "1969-12-31 23:59:59.123456"; box "Fsdb/Contract_Eastern"; box "+00:00" |]
               Contract.execute "session-zone" "SET time_zone='Fsdb/Contract_Eastern'"
               Contract.query "unix-functions"
                   "SELECT FROM_UNIXTIME(1710054000) AS local_value, UNIX_TIMESTAMP('2024-11-03 01:30:12.123456') AS epoch_value"
               Contract.execute "strict-gap"
                   "INSERT INTO contract_zone VALUES ('2024-03-10 02:30:12.123456','2024-03-10 02:30:12.123456')"
                   |> Contract.fails 1292 "22007"
               Contract.preparedExecute "fold-insert" "INSERT INTO contract_zone VALUES (?, ?)"
                   [| box "2024-11-03 01:30:12.123456"; box "2024-11-03 01:30:12.123456" |]
               Contract.execute "utc-session" "SET time_zone='+00:00'"
               Contract.preparedQuery "stored-instant" "SELECT t,d FROM contract_zone" [||]
               Contract.execute "non-strict" "SET sql_mode=''"
               Contract.execute "restore-zone" "SET time_zone='Fsdb/Contract_Eastern'"
               Contract.execute "normalize-gap"
                   "INSERT INTO contract_zone VALUES ('2024-03-10 02:30:12.123456','2024-03-10 02:30:12.123456')"
               Contract.query "gap-warning" "SHOW WARNINGS"
               Contract.query "local-results" "SELECT t,d FROM contract_zone ORDER BY t"
               Contract.execute "remove-types" "DELETE FROM mysql.time_zone_transition_type WHERE Time_zone_id=900002"
               Contract.execute "cached-zone" "SET time_zone='fsdb/contract_eastern'"
               Contract.query "cached-rules" "SELECT @@time_zone, FROM_UNIXTIME(1710054000)" |]
          Cleanup =
            [| "SET time_zone='SYSTEM'"
               "SET sql_mode=DEFAULT"
               "DROP TABLE IF EXISTS contract_zone"
               "DELETE FROM mysql.time_zone_transition WHERE Time_zone_id=900002"
               "DELETE FROM mysql.time_zone_transition_type WHERE Time_zone_id=900002"
               "DELETE FROM mysql.time_zone_name WHERE Time_zone_id=900002"
               "DELETE FROM mysql.time_zone WHERE Time_zone_id=900002" |]
          Coverage =
            [| "function:CONVERT_TZ", [| "text-differential"; "prepared-protocol" |]
               "function:UNIX_TIMESTAMP", [| "text-differential" |]
               "function:FROM_UNIXTIME", [| "text-differential" |] |] }

    let private preparedProjectionNames =
        { Name = "prepared-projection-names"
          Setup = [||]
          Steps =
            [| Contract.preparedQuery "parameter-label" "SELECT ?" [| box "value" |]
               Contract.preparedQuery "function-label" "SELECT ABS(?)" [| box -2 |]
               Contract.preparedQuery "arithmetic-label" "SELECT ? + 1" [| box -2 |]
               Contract.preparedQuery "derived-label" "SELECT d.`?` FROM (SELECT ?) d" [| box "value" |]
               Contract.preparedQuery "cte-label" "WITH c AS (SELECT ?) SELECT * FROM c" [| box "value" |]
               Contract.preparedQuery "union-label" "SELECT ? UNION ALL SELECT ?" [| box "first"; box "second" |]
               Contract.preparedQuery "explicit-alias" "SELECT ? AS chosen" [| box "value" |]
               Contract.execute "sql-prepare" "PREPARE stable_labels FROM 'SELECT ?'"
               Contract.execute "bind-first" "SET @label_value='first'"
               Contract.query "sql-execute-first" "EXECUTE stable_labels USING @label_value"
               Contract.execute "bind-second" "SET @label_value='second'"
               Contract.query "sql-execute-second" "EXECUTE stable_labels USING @label_value" |]
          Cleanup = [| "DEALLOCATE PREPARE stable_labels" |]
          Coverage = [| "statement:select", [| "prepared-protocol"; "text-differential" |] |] }

    let private preparedTypeHistory =
        let sql = "SELECT ?, ABS(?), ? + 1"
        let values =
            [| "integer", "-2", box -2L
               "negative-string", "'-3'", box "-3"
               "null", "NULL", box DBNull.Value
               "decimal", "1.25", box 1.25M
               "integer-after-decimal", "-4", box -4L
               "double", "1.5e0", box 1.5
               "invalid-string", "'oops'", box "oops"
               "integer-after-double", "-5", box -5L |]
        { Name = "prepared-parameter-type-history"
          Setup = [||]
          Steps =
            [| Contract.prepare "binary-prepare" "typed" Query sql [| box -2L; box -2L; box -2L |]
               for name, _, value in values do
                   Contract.invokeWith ("binary-" + name) "typed" (Array.replicate 3 value)
               Contract.close "binary-close" "typed"
               Contract.prepare "temporal-prepare" "temporal" Query "SELECT ?" [| box (DateOnly(2024, 1, 2)) |]
               Contract.invoke "temporal-date" "temporal" OracleSuccess
               Contract.invokeWith "temporal-datetime" "temporal" [| box (DateTime(2024, 1, 3, 12, 30, 0)) |]
               Contract.invokeWith "temporal-date-after-datetime" "temporal" [| box (DateOnly(2024, 1, 4)) |]
               Contract.invokeWith "temporal-numeric-date" "temporal" [| box 20240105L |]
               Contract.invokeWith "temporal-null" "temporal" [| box DBNull.Value |]
               Contract.close "temporal-close" "temporal"
               Contract.prepare "error-prepare" "error-types" Query "SELECT ?, ABS(?)"
                   [| box Int64.MinValue; box Int64.MinValue |]
               Contract.invoke "error-overflow" "error-types" (OracleError(1690, "22003"))
               Contract.invokeWith "error-retained-types" "error-types" [| box DBNull.Value; box DBNull.Value |]
               Contract.close "error-close" "error-types"
               Contract.execute "sql-prepare" ("PREPARE typed_history FROM '" + sql + "'")
               for name, literal, _ in values do
                   Contract.execute ("set-" + name) ("SET @typed_value=" + literal)
                   Contract.query ("sql-" + name) "EXECUTE typed_history USING @typed_value,@typed_value,@typed_value"
               Contract.execute "null-prepare" "PREPARE null_history FROM 'SELECT ?, ABS(?)'"
               Contract.execute "null-initial-values" "SET @typed_a=-2,@typed_b=-2"
               Contract.query "null-initial-execute" "EXECUTE null_history USING @typed_a,@typed_b"
               Contract.execute "null-reprepare-values" "SET @typed_a=NULL,@typed_b=1.25"
               Contract.query "null-reprepare-execute" "EXECUTE null_history USING @typed_a,@typed_b"
               Contract.execute "null-reset-values" "SET @typed_a=-4,@typed_b=-4"
               Contract.query "null-reset-execute" "EXECUTE null_history USING @typed_a,@typed_b" |]
          Cleanup = [| "DEALLOCATE PREPARE typed_history"; "DEALLOCATE PREPARE null_history" |]
          Coverage = [| "statement:select", [| "prepared-protocol"; "text-differential" |] |] }

    let private preparedUserVariables =
        let histories =
            [| "integer", "-2"
               "unsigned", "CAST(18446744073709551615 AS UNSIGNED)"
               "decimal", "1.25"
               "double", "1.5e0"
               "text", "'abc'"
               "binary", "NULL" |]
        let changes =
            [| "integer", "-2"
               "decimal", "1.75"
               "double", "2.75e0"
               "string", "'1.75'"
               "null", "NULL" |]
        let mixed =
            [| "initial", "-2", "NULL"
               "retained-integer", "1.25", "NULL"
               "reprepare-decimal", "1.25", "1"
               "retained-decimal", "1.5e0", "2"
               "reprepare-double", "1.5e0", "2.5"
               "retained-double", "'abc'", "NULL" |]
        { Name = "prepared-user-variable-types"
          Setup = [||]
          Steps =
            [| for family, initial in histories do
                   let variable = "@direct_" + family
                   Contract.execute (family + "-initial") ("SET " + variable + "=" + initial)
                   Contract.execute (family + "-prepare")
                       ("PREPARE direct_" + family + " FROM 'SELECT " + variable + " AS value,ABS(" + variable + ") AS magnitude'")
                   for name, value in changes do
                       Contract.execute (family + "-set-" + name) ("SET " + variable + "=" + value)
                       Contract.query (family + "-read-" + name) ("EXECUTE direct_" + family)
                   Contract.execute (family + "-close") ("DEALLOCATE PREPARE direct_" + family)
               Contract.execute "mixed-initial" "SET @direct_mixed=-2"
               Contract.execute "mixed-prepare"
                   "PREPARE direct_mixed FROM 'SELECT ? AS parameter,@direct_mixed AS value,ABS(@direct_mixed) AS magnitude'"
               for name, value, parameter in mixed do
                   Contract.execute ("mixed-set-" + name) ("SET @direct_mixed=" + value + ",@direct_parameter=" + parameter)
                   Contract.query ("mixed-read-" + name) "EXECUTE direct_mixed USING @direct_parameter"
               Contract.execute "mixed-close" "DEALLOCATE PREPARE direct_mixed"
               Contract.execute "assignment-initial" "SET @direct_assignment=-2"
               Contract.execute "assignment-prepare"
                   "PREPARE assigned_variable FROM 'SELECT @direct_assignment:=1.75 AS assigned,@direct_assignment AS value'"
               Contract.query "assignment-read" "EXECUTE assigned_variable"
               Contract.query "assignment-stored-value" "SELECT @direct_assignment AS value" |]
          Cleanup = [| "DEALLOCATE PREPARE assigned_variable" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private preparedUserAssignments =
        { Name = "prepared-user-variable-assignments"
          Setup = [||]
          Steps =
            [| Contract.execute "initial" "SET @set_v=-2"
               Contract.execute "prepare" "PREPARE user_assignment FROM 'SET @set_x=@set_v'"
               for name, value in [ "decimal", "1.75"; "string", "'hello'"; "null", "NULL" ] do
                   Contract.execute (name + "-set") ("SET @set_v=" + value)
                   Contract.execute (name + "-execute") "EXECUTE user_assignment"
                   Contract.query (name + "-read") "SELECT @set_x AS assigned,@set_v AS source"
               Contract.execute "delayed-initial" "SET @set_v=-2"
               Contract.execute "delayed-prepare" "PREPARE delayed_assignment FROM 'SET @set_v=1.75,@set_x=@set_v'"
               Contract.execute "delayed-execute" "EXECUTE delayed_assignment"
               Contract.query "delayed-read" "SELECT @set_x AS assigned,@set_v AS source"
               Contract.execute "mixed-initial" "SET @set_v=-2,@set_p=NULL"
               Contract.execute "mixed-prepare" "PREPARE mixed_assignment FROM 'SET @set_x=?,@set_y=@set_v'"
               for name, parameter, value in
                   [ "retained", "NULL", "1.75"
                     "decimal", "1", "1.75"
                     "double", "2.5", "1.5e0" ] do
                   Contract.execute ("mixed-" + name + "-set") ("SET @set_p=" + parameter + ",@set_v=" + value)
                   Contract.execute ("mixed-" + name + "-execute") "EXECUTE mixed_assignment USING @set_p"
                   Contract.query ("mixed-" + name + "-read") "SELECT @set_x AS parameter,@set_y AS assigned"
               Contract.execute "failure-initial" "SET @set_side=9,@set_x=8,@set_y=7"
               Contract.execute "failure-prepare" "PREPARE failing_assignment FROM 'SET @set_x=(@set_side:=1),@set_y=(SELECT 1 UNION ALL SELECT 2)'"
               Contract.execute "failure-execute" "EXECUTE failing_assignment" |> Contract.fails 1242 "21000"
               Contract.query "failure-read" "SELECT @set_side AS nested,@set_x AS first,@set_y AS second"
               Contract.execute "ordinary-failure-initial" "SET @set_side=9,@set_x=8,@set_y=7"
               Contract.execute "ordinary-failure-execute" "SET @set_x=(@set_side:=1),@set_y=(SELECT 1 UNION ALL SELECT 2)" |> Contract.fails 1242 "21000"
               Contract.query "ordinary-failure-read" "SELECT @set_side AS nested,@set_x AS first,@set_y AS second" |]
          Cleanup = [| "DEALLOCATE PREPARE user_assignment"; "DEALLOCATE PREPARE delayed_assignment"; "DEALLOCATE PREPARE mixed_assignment"; "DEALLOCATE PREPARE failing_assignment" |]
          Coverage = [| "statement:set", [| "text-differential" |] |] }

    let private preparedMixedAssignments =
        { Name = "prepared-mixed-variable-assignments"
          Setup = [||]
          Steps =
            [| for name, statement in
                   [ "mixed", "SET @mixed_x=@mixed_v, SESSION max_sp_recursion_depth=100"
                     "system", "SET SESSION max_sp_recursion_depth=@mixed_v"
                     "both", "SET SESSION max_sp_recursion_depth=@mixed_v,@mixed_x=@mixed_v"
                     "names-first", "SET NAMES utf8mb4 COLLATE utf8mb4_bin,@mixed_x=@mixed_v"
                     "names-last", "SET @mixed_x=@mixed_v,NAMES utf8mb4 COLLATE utf8mb4_bin" ] do
                   Contract.execute (name + "-initial") "SET @mixed_v=2,@mixed_x=9"
                   Contract.execute (name + "-prepare") ("PREPARE mixed_types FROM '" + statement + "'")
                   for valueName, value in [ "decimal", "1.75"; "text", "'abc'"; "null", "NULL"; "integer", "3" ] do
                       let step = name + "-" + valueName
                       Contract.execute (step + "-set") ("SET @mixed_v=" + value)
                       Contract.execute (step + "-execute") "EXECUTE mixed_types"
                       Contract.query (step + "-read") "SELECT @mixed_x AS assigned,@@session.max_sp_recursion_depth AS depth"
                   if name.StartsWith("names-", StringComparison.Ordinal) then
                       Contract.query (name + "-charset") "SELECT @@character_set_client AS charset,@@collation_connection AS collation"
                   Contract.execute (name + "-close") "DEALLOCATE PREPARE mixed_types"
               Contract.execute "parameter-prepare" "PREPARE numeric_target FROM 'SET SESSION max_sp_recursion_depth=?'"
               for name, value in [ "decimal", "1.75"; "null", "NULL"; "text", "'abc'" ] do
                   Contract.execute (name + "-parameter") ("SET @mixed_p=" + value)
                   Contract.execute (name + "-parameter-execute") "EXECUTE numeric_target USING @mixed_p" |> Contract.fails 1232 "42000"
               Contract.execute "parameter-close" "DEALLOCATE PREPARE numeric_target"
               Contract.execute "nullable-prepare" "PREPARE nullable_target FROM 'SET character_set_results=NULL'"
               Contract.execute "nullable-execute" "EXECUTE nullable_target"
               Contract.query "nullable-read" "SELECT @@session.character_set_results AS charset"
               Contract.execute "nullable-close" "DEALLOCATE PREPARE nullable_target" |]
          Cleanup = [| "SET SESSION max_sp_recursion_depth=DEFAULT"; "SET character_set_results=DEFAULT" |]
          Coverage = [| "statement:set", [| "text-differential" |] |] }

    let private preparedDecimalDivision =
        { Name = "prepared-decimal-division"
          Setup = [||]
          Steps =
            [| Contract.execute "initial" "SET @division_value=1.25"
               Contract.execute "prepare" "PREPARE division_types FROM 'SELECT @division_value/3 AS integral_divisor,@division_value/3.00 AS decimal_divisor,@division_value/0.03 AS fractional_divisor,@division_value AS direct,@division_value+1 AS added,@division_value-1 AS subtracted,@division_value*2 AS multiplied,@division_value%1 AS modulo,ABS(@division_value) AS absolute_value,-@division_value AS negated,ROUND(@division_value,2) AS rounded,TRUNCATE(@division_value,2) AS truncated,COALESCE(@division_value,0) AS coalesced,CASE WHEN 1 THEN @division_value ELSE 0 END AS conditional'"
               for name, value in
                   [ "initial", "1.25"; "integer", "2"; "negative", "-1.25"
                     "nine-digits", "1.234567891"; "null", "NULL" ] do
                   Contract.execute (name + "-set") ("SET @division_value=" + value)
                   Contract.query (name + "-execute") "EXECUTE division_types"
               Contract.execute "close" "DEALLOCATE PREPARE division_types"
               Contract.query "literal-scales" "SELECT 1/3 AS integer_division,10.00/3 AS decimal_division"
               Contract.query "nested-scales" "SELECT CONCAT(1/3) AS text_value,(1/3)*3 AS numeric_value,CAST(10.00/3 AS CHAR) AS cast_value" |]
          Cleanup = [||]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private binaryLiteralContexts =
        { Name = "binary-literal-contexts"
          Setup = [||]
          Steps =
            [| for name, sql in
                   [ "arithmetic", "SELECT b'01'+0 AS bit_value,X'01'+0 AS hex_value,_binary X'01'+0 AS bytes_value,-b'01' AS negative,ABS(b'01') AS absolute"
                     "introduced", "SELECT _binary b'01' AS quoted,_binary 0b000000001 AS unquoted,_binary 0xabc AS hexadecimal,_BINARY B'' AS empty_value,_binary/*separator*/b'1' AS separated"
                     "introduced-numeric", "SELECT _binary b'01'+0 AS value,SUM(_binary b'01') AS total,_binary 0x01+0 AS hexadecimal"
                     "aggregates", "SELECT SUM(b'01') AS s,AVG(b'01') AS a,SUM(DISTINCT b'01') AS d"
                     "raw", "SELECT b'01' AS bit_value,X'01' AS hex_value,b'000000001' AS leading_zero,CONCAT(b'01') AS concatenated"
                     "negative-boundary", "SELECT -b'1000000000000000000000000000000000000000000000000000000000000000' AS negative"
                     "widths", "SELECT SUM(b'') AS empty_value,SUM(b'100000001') AS sum_value,AVG(b'100000001') AS average_value,SUM(X'010001') AS hex_value"
                     "casts", "SELECT CAST(b'01' AS UNSIGNED) AS u,CAST(b'01' AS DECIMAL) AS d,b'01'=1 AS numeric_equal,b'01'='1' AS string_equal"
                     "conditional", "SELECT IF(1,b'01',b'10')+0 AS branch_value,CASE WHEN 1 THEN b'01' ELSE b'10' END+0 AS case_value,COALESCE(b'01',b'10')+0 AS coalesced,IFNULL(b'01',b'10')+0 AS nonnull,CONCAT(b'01')+0 AS concatenated"
                     "scalar-reduced", "SELECT (SELECT b'01')+0 AS n,SUM((SELECT b'01')) AS s,AVG((SELECT b'01')) AS a,(SELECT 1.25)+0 AS d,(SELECT (SELECT b'01'))+0 AS nested"
                     "scalar-true-conditions", "SELECT (SELECT b'01' WHERE 1)+0 AS literal_value,(SELECT b'01' WHERE 1=1)+0 AS compared,(SELECT b'01' WHERE ABS(-1)=1)+0 AS computed,(SELECT b'01' WHERE 'a'='A')+0 AS collated,(SELECT b'01' WHERE COALESCE(NULL,1))+0 AS coalesced"
                     "scalar-runtime-conditions", "SELECT (SELECT b'01' WHERE 0)+0 AS false_value,(SELECT b'01' WHERE NULL)+0 AS null_value,(SELECT b'01' WHERE RAND()>=0)+0 AS random_value,(SELECT b'01' WHERE EXISTS(SELECT 1))+0 AS existence"
                     "scalar-condition-limits", "SELECT (SELECT b'01' WHERE 1 LIMIT 0)+0 AS removed_limit,(SELECT b'01' WHERE RAND()>=0 LIMIT 0)+0 AS retained_limit,(SELECT b'01' WHERE 0 LIMIT 0)+0 AS empty_value"
                     "scalar-condition-collations", "SELECT (SELECT b'01' WHERE 'a' COLLATE utf8mb4_bin='A')+0 AS binary_value,(SELECT b'01' WHERE NULL OR 1)+0 AS disjunction,(SELECT b'01' WHERE NULL AND 1)+0 AS conjunction"
                     "scalar-limits", "SELECT (SELECT b'01' LIMIT 0)+0 AS zero_limit,(SELECT b'01' LIMIT 1 OFFSET 10)+0 AS offset_value,(SELECT b'01' GROUP BY 1 LIMIT 0)+0 AS grouped,SUM((SELECT b'01' GROUP BY 1 LIMIT 0)) AS total"
                     "scalar-rollup", "SELECT (SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 1)+0 AS first_row,(SELECT b'01' GROUP BY 1 WITH ROLLUP LIMIT 0)+0 AS no_rows"
                     "scalar-preserved-limits", "SELECT (SELECT MIN(b'01') LIMIT 0)+0 AS aggregate_value,(SELECT FIRST_VALUE(b'01') OVER () LIMIT 0)+0 AS window_value,(SELECT b'01' HAVING 1 LIMIT 0)+0 AS having_value,(SELECT b'01' FROM (SELECT 1)t LIMIT 0)+0 AS source_value"
                     "row-materialized", "SELECT ROW(1,1)=(SELECT b'01',b'01') AS source_free,ROW(1,1)=(SELECT b'01',b'01' FROM (SELECT 1)t) AS sourced,ROW(1,1)=(SELECT b'01',b'01' LIMIT 0) AS limited"
                     "scalar-materialized", "SELECT (SELECT b'01' FROM (SELECT 1) t)+0 AS sourced,(SELECT b'01' HAVING 1)+0 AS filtered"
                     "scalar-aggregate-window", "SELECT (SELECT MIN(b'01'))+0 AS minimum,(SELECT FIRST_VALUE(b'01') OVER ())+0 AS first_result"
                     "scalar-comparison", "SELECT (SELECT b'01' FROM (SELECT 1)t)=1 AS scalar_value,1 IN (SELECT b'01' FROM (SELECT 1)t) AS membership,1=ANY(SELECT b'01' FROM (SELECT 1)t) AS quantified"
                     "derived", "SELECT SUM(v) AS s,AVG(v) AS a FROM (SELECT b'01' AS v) t"
                     "bytes", "SELECT HEX(b'000000001') AS leading_zero,HEX(b'') AS empty_value,HEX(CONCAT(b'01')) AS concatenated"
                     "wide", "SELECT SUM(b'1111111111111111111111111111111111111111111111111111111111111111') AS s,b'1111111111111111111111111111111111111111111111111111111111111111'+0 AS arithmetic" ] do
                   Contract.query (name + "-text") sql
                   Contract.preparedQuery (name + "-binary") sql [||]
               for index, predicate in
                   [ "CEIL(0.1)=1"; "CEILING(0.1)=1"; "FLOOR(1.9)=1"; "SQRT(4)=2"; "POWER(2,3)=8"; "POW(2,3)=8"
                     "SIGN(-2)=-1"; "GREATEST(1,2)=2"; "LEAST(1,2)=1"; "NULLIF(1,2)=1"
                     "SIN(0)=0"; "COS(0)=1"; "TAN(0)=0"; "COT(1)>0"; "ASIN(0)=0"; "ACOS(1)=0"; "ATAN(0)=0"; "ATAN2(0,1)=0"
                     "PI()>3"; "EXP(0)=1"; "LN(1)=0"; "LOG(1)=0"; "LOG2(8)=3"; "LOG10(100)=2"; "DEGREES(0)=0"; "RADIANS(0)=0"
                     "BIT_COUNT(3)=2"; "CRC32('a')>0"; "HEX('a')='61'"; "REVERSE('ab')='ba'"; "TRIM(' a ')='a'" ] |> List.indexed do
                   let sql = "SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value"
                   Contract.query (sprintf "scalar-function-text-%d" index) sql
                   Contract.preparedQuery (sprintf "scalar-function-binary-%d" index) sql [||]
               for limit in [ 0; 3 ] do
                   Contract.preparedQuery (sprintf "scalar-limit-parameter-%d" limit) "SELECT (SELECT b'01' LIMIT ? OFFSET ?)+0 AS value" [| box limit; box 10 |]
               Contract.execute "scalar-condition-binding" "SET @scalar_condition=1"
               Contract.execute "prepare-scalar-condition" "PREPARE scalar_condition FROM 'SELECT (SELECT b''01'' WHERE @scalar_condition)+0 AS value'"
               Contract.execute "prepare-scalar-disjunction" "PREPARE scalar_disjunction FROM 'SELECT (SELECT b''01'' WHERE 1 OR @scalar_condition)+0 AS value'"
               for index, value in [ 1; 0; 1 ] |> List.indexed do
                   Contract.execute (sprintf "set-scalar-condition-%d" index) (sprintf "SET @scalar_condition=%d" value)
                   Contract.query (sprintf "scalar-condition-text-%d" index) "SELECT (SELECT b'01' WHERE @scalar_condition)+0 AS value"
                   Contract.query (sprintf "scalar-condition-reuse-%d" index) "EXECUTE scalar_condition"
                   Contract.query (sprintf "scalar-disjunction-reuse-%d" index) "EXECUTE scalar_disjunction"
                   Contract.preparedQuery (sprintf "scalar-condition-binary-%d" index) "SELECT (SELECT b'01' WHERE ?)+0 AS value" [| box value |]
               for index, predicate in
                   [ "NOT (0 AND @scalar_condition)"; "NOT NOT (1 OR @scalar_condition)"
                     "IF(1 OR @scalar_condition,1,0)"; "IF(0 AND @scalar_condition,0,1)"; "IF(NULL AND @scalar_condition,0,1)"
                     "IF(1,1,@scalar_condition)"; "(1 OR @scalar_condition)=1"; "(1 OR @scalar_condition) IS TRUE"
                     "CAST(1 OR @scalar_condition AS SIGNED)"; "COALESCE(1 OR @scalar_condition,0)"
                     "CASE WHEN 1 OR @scalar_condition THEN 1 ELSE 0 END"; "(1 OR @scalar_condition)+0"
                     "NOT (NULL AND @scalar_condition)"; "NOT (0 AND @scalar_condition) LIMIT 0"; "(1 OR @scalar_condition)=1 LIMIT 0" ] |> List.indexed do
                   for value in [ 0; 1 ] do
                       let name = sprintf "scalar-nesting-%d-%d" index value
                       let sql = "SELECT (SELECT b'01' WHERE " + predicate + ")+0 AS value"
                       Contract.execute (name + "-set") (sprintf "SET @scalar_condition=%d" value)
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") (sql.Replace("@scalar_condition", "?")) [| box value |]
               Contract.execute "close-scalar-condition" "DEALLOCATE PREPARE scalar_condition"
               Contract.execute "close-scalar-disjunction" "DEALLOCATE PREPARE scalar_disjunction"
               Contract.query "adjacent-introducer-identifier" "SELECT _binaryX'00ff'" |> Contract.fails 1054 "42S22"
               Contract.execute "storage-table" "CREATE TABLE literal_storage(b BIT(64),u BIGINT UNSIGNED,d DECIMAL(30),f DOUBLE,n TINYINT)"
               for name, literal in [ "wide", "X'010000000000000000'"; "padded", "X'000000000000000001'" ] do
                   for column in [ "b"; "u"; "d"; "f"; "n" ] do
                       Contract.execute (name + "-strict-" + column) (sprintf "INSERT INTO literal_storage(%s) VALUES(%s)" column literal)
                       |> Contract.fails 1264 "22003"
               Contract.execute "permissive-mode" "SET sql_mode=''"
               Contract.execute "permissive-storage" "INSERT INTO literal_storage VALUES(X'010000000000000000',X'010000000000000000',X'010000000000000000',X'010000000000000000',X'010000000000000000')"
               Contract.query "storage-warnings" "SHOW WARNINGS"
               Contract.query "stored-values" "SELECT HEX(b) AS b,u,d,f,n FROM literal_storage"
               Contract.execute "restore-mode" "SET sql_mode=DEFAULT"
               Contract.execute "bind-variable" "SET @literal_bytes=b'01'"
               Contract.query "variable-materialized" "SELECT @literal_bytes+0 AS value,SUM(@literal_bytes) AS total"
               Contract.execute "prepare-variable" "PREPARE literal_context FROM 'SELECT ?+0 AS value,SUM(?) AS total'"
               Contract.query "execute-variable" "EXECUTE literal_context USING @literal_bytes,@literal_bytes" |]
          Cleanup = [| "DEALLOCATE PREPARE literal_context"; "DROP TABLE IF EXISTS literal_storage"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private roundingPrecisionDescriptors =
        { Name = "rounding-precision-descriptors"
          Setup = [| "CREATE TABLE rounding_precision(d DECIMAL(10,2),n INT)"
                     "INSERT INTO rounding_precision VALUES(1.25,1)" |]
          Steps =
            [| for index, expression in
                   [ "ROUND(1.25,1+0)"; "TRUNCATE(1.25,1+0)"
                     "ROUND(CAST(1 AS DECIMAL(10,0)),0)"; "TRUNCATE(CAST(0.1 AS DECIMAL(4,4)),0)"
                     "ROUND(1.25,NULL)"; "ROUND(1.25,1.5)"; "ROUND(1.25,1.5e0)"; "ROUND(1.25,'1.5')"
                     "TRUNCATE(1.29,1.5)"; "ROUND(1.25,30)"; "TRUNCATE(1.25,30)"
                     "TRUNCATE(9223372036854775807,0)"; "TRUNCATE(-9223372036854775808,-1)"
                     "ROUND(1.25,18446744073709551615)"; "TRUNCATE(1.25,-9223372036854775808)"
                     "ROUND(1.25,-9223372036854775808)"; "ROUND('2.5')"
                     "TRUNCATE(1.25e0,18446744073709551615)"; "TRUNCATE(1.25e0,-9223372036854775808)"
                     "ROUND(d,n)"; "TRUNCATE(d,n)"; "ROUND(d,ABS(-1))"; "TRUNCATE(d,CAST(1 AS UNSIGNED))" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value FROM rounding_precision"
                   Contract.query (sprintf "precision-%d-text" index) sql
                   Contract.preparedQuery (sprintf "precision-%d-binary" index) sql [||]
               for index, value in [ "9223372036854775807"; "-9223372036854775808" ] |> List.indexed do
                   let sql = "SELECT ROUND(" + value + ",-1)"
                   Contract.query (sprintf "overflow-%d-text" index) sql |> Contract.fails 1690 "22003"
                   Contract.preparedQuery (sprintf "overflow-%d-binary" index) sql [||] |> Contract.fails 1690 "22003"
               Contract.preparedQuery "runtime-precision" "SELECT ROUND(d,?) AS value FROM rounding_precision" [| box 1 |]
               Contract.preparedQuery "runtime-truncation" "SELECT TRUNCATE(d,?) AS value FROM rounding_precision" [| box 1 |]
               Contract.preparedQuery "runtime-expression" "SELECT ROUND(d,?+0) AS value FROM rounding_precision" [| box 1 |]
               Contract.preparedQuery "runtime-function" "SELECT TRUNCATE(d,ABS(?)) AS value FROM rounding_precision" [| box -1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS rounding_precision" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private integralRoundingDescriptors =
        { Name = "integral-rounding-descriptors"
          Setup = [| "CREATE TABLE rounding_numbers(d DECIMAL(10,2),wide DECIMAL(20,2),u DECIMAL(20,2) UNSIGNED,f DOUBLE)"
                     "INSERT INTO rounding_numbers VALUES(-1.25,1.25,1.25,1e20)" |]
          Steps =
            [| for index, expression in
                   [ "FLOOR(1.25)"; "CEIL(-1.25)"; "FLOOR(1)"; "CEILING(CAST(1 AS UNSIGNED))"
                     "FLOOR(CAST(1.25 AS DECIMAL(19,2)))"; "CEILING(CAST(1.25 AS DECIMAL(20,2)))"
                     "FLOOR(CAST(1.25 AS DECIMAL(30,2)))"; "CEIL(999999999999999999.99)"
                     "FLOOR(NULL)"; "FLOOR('1.25')"; "FLOOR(1e20)"; "CEIL(-1e20)"
                     "FLOOR(d)"; "CEIL(wide)"; "FLOOR(u)"; "CEIL(f)"
                     "FLOOR(d)+1"; "SUM(FLOOR(d))"; "COALESCE(FLOOR(d),0)"
                     "CAST(FLOOR(d) AS CHAR)"; "FLOOR(d)/2" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value FROM rounding_numbers"
                   Contract.query (sprintf "rounding-%d-text" index) sql
                   Contract.preparedQuery (sprintf "rounding-%d-binary" index) sql [||]
               Contract.preparedQuery "parameter" "SELECT FLOOR(?) AS value" [| box 1.25M |]
               Contract.query "union" "SELECT FLOOR(d) AS value FROM rounding_numbers UNION ALL SELECT CEIL(wide) FROM rounding_numbers" |]
          Cleanup = [| "DROP TABLE IF EXISTS rounding_numbers" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private scientificLiteralDescriptors =
        { Name = "scientific-literal-descriptors"
          Setup = [| "CREATE TABLE scientific_source(n INT PRIMARY KEY,f FLOAT,d DOUBLE(10,2))"
                     "INSERT INTO scientific_source VALUES(1e0,1.25e0,1.25e0)" |]
          Steps =
            [| for index, expression in
                   [ "1e0"; "1E+00"; "0001e000"; "1.00e0"; ".1e1"; "1.e0"
                     "+1e0"; "-1e0"; "-(-1e0)"; "(1e0)"; "(SELECT 1e0)"
                     "(SELECT 1e0 FROM scientific_source LIMIT 1)"
                     "(SELECT x FROM (SELECT 1e0 AS x)t)"
                     "1 DIV 2e0"; "2e0 DIV 1"
                     "(SELECT 2e0 FROM scientific_source LIMIT 1) DIV 1"
                     "1e0/2"; "ABS(1e0)"; "COALESCE(1e0,NULL)"; "1e0+0" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value"
                   Contract.query (sprintf "literal-%d-text" index) sql
                   Contract.preparedQuery (sprintf "literal-%d-binary" index) sql [||]
               let division = "SELECT f DIV 1 AS a,d DIV 1 AS b,d DIV 0.1 AS c,2 DIV d AS e FROM scientific_source"
               Contract.query "division-columns-text" division
               Contract.preparedQuery "division-columns-binary" division [||]
               Contract.query "projection-names" "SELECT 1E+00,0001e000,.1e1"
               Contract.execute "materialize" "CREATE TABLE scientific_materialized AS SELECT 1e0 AS value"
               Contract.query "materialized-text" "SELECT value FROM scientific_materialized"
               Contract.preparedQuery "materialized-binary" "SELECT value FROM scientific_materialized" [||]
               Contract.execute "create-view" "CREATE VIEW scientific_projection AS SELECT 1e0 AS value"
               Contract.query "view-text" "SELECT value FROM scientific_projection"
               Contract.preparedQuery "view-binary" "SELECT value FROM scientific_projection" [||]
               Contract.execute "assign-variable" "SET @scientific=1e0"
               Contract.query "variable" "SELECT @scientific AS value"
               Contract.query "indexed-predicate" "SELECT n FROM scientific_source WHERE n=1e0"
               Contract.query "literal-membership" "SELECT n FROM scientific_source WHERE n IN(1e0,2e0)"
               Contract.preparedQuery "inherited-parameter" "SELECT ?+1e0 AS value" [| box 2L |] |]
          Cleanup = [| "DROP VIEW IF EXISTS scientific_projection"
                       "DROP TABLE IF EXISTS scientific_materialized"; "DROP TABLE IF EXISTS scientific_source" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private approximateExpressionDescriptors =
        { Name = "approximate-expression-descriptors"
          Setup = [| "CREATE TABLE approximate_numbers(d DOUBLE,f FLOAT,df DOUBLE(10,2))"
                     "INSERT INTO approximate_numbers VALUES(1.25,1.25,1.25)" |]
          Steps =
            [| for index, expression in
                   [ "-1e0"
                     "1e0+1"
                     "'1'+1"
                     "ABS(1e0)"
                     "SQRT(4)"
                     "ROUND(1e0,2)"
                     "COALESCE(1e0,0)"
                     "COALESCE(1e0,NULL)"
                     "CAST(1 AS DOUBLE)"
                     "d"
                     "f"
                     "df"
                     "d+0"
                     "f+0"
                     "df+0"
                     "-df"
                     "ABS(df)"
                     "ROUND(df,1)"
                     "TRUNCATE(df,1)"
                     "FLOOR(df)"
                     "CEIL(df)"
                     "ABS(f)"
                     "-f"
                     "df+df"
                     "df*df"
                     "df+NULL"
                     "COALESCE(NULLIF(df,df),2)"
                     "COALESCE(NULLIF(df,df),2)+0.5"
                     "COALESCE(df,0)"
                     "IF(1,df,0)"
                     "COALESCE(f,0)"
                     "COALESCE(f,1.25)"
                     "IFNULL(f,1.25)"
                     "IF(0,df,2)"
                     "CASE WHEN 0 THEN df ELSE 2 END"
                     "df*1.2345"
                     "df+1.2345"
                     "CAST(1 AS FLOAT)"
                     "-b'01'"
                     "ABS(b'01')" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value FROM approximate_numbers"
                   Contract.query (sprintf "expression-%d-text" index) sql
                   Contract.preparedQuery (sprintf "expression-%d-binary" index) sql [||]
               let unionSql = "SELECT df*df AS value FROM approximate_numbers UNION ALL SELECT df*df FROM approximate_numbers"
               Contract.query "union-text" unionSql
               Contract.preparedQuery "union-binary" unionSql [||]
               for index, expression in [ "df*0.1"; "-df*0.1" ] |> List.indexed do
                   let branch = "SELECT " + expression + " AS value FROM approximate_numbers"
                   let sql = branch + " UNION ALL " + branch
                   Contract.query (sprintf "union-round-%d-text" index) sql
                   Contract.preparedQuery (sprintf "union-round-%d-binary" index) sql [||]
               Contract.execute "prepare-fixed-scale" "PREPARE fixed_scale FROM 'SELECT df*df AS value FROM approximate_numbers'"
               Contract.query "execute-fixed-scale-text" "EXECUTE fixed_scale"
               Contract.execute "deallocate-fixed-scale" "DEALLOCATE PREPARE fixed_scale"
               Contract.execute "approximate-variable" "SET @value=1e0"
               Contract.query "variable-descriptors" "SELECT @value AS a,@value+1 AS b,-@value AS c"
               Contract.query "fixed-scale-text" "SELECT CAST(df*df AS CHAR) AS a,CONCAT(df+df) AS b,CAST(COALESCE(NULLIF(df,df),2) AS CHAR) AS c FROM approximate_numbers"
               Contract.query "temporal-numeric-text" "SELECT CAST(-TIMESTAMP '2020-01-01 00:00:00.123' AS CHAR) AS a,CAST(ABS(CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))) AS CHAR) AS b"
               Contract.query "binary-literal-numeric-text" "SELECT CAST(-b'1000000000000000000000000000000000000000000000000000000000000000' AS CHAR) AS a,CAST((SELECT b'01' FROM (SELECT 1)t)/2 AS CHAR) AS b" |]
          Cleanup = [| "DROP TABLE IF EXISTS approximate_numbers" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private integerExpressionDescriptors =
        { Name = "integer-expression-descriptors"
          Setup = [| "CREATE TABLE integer_expressions(n INT,u BIGINT UNSIGNED)"
                     "INSERT INTO integer_expressions VALUES(12,34),(-12,56),(NULL,NULL)" |]
          Steps =
            [| for index, expression in
                   [ "1"
                     "-1"
                     "127"
                     "128"
                     "9223372036854775807"
                     "18446744073709551615"
                     "1+1"
                     "12+34"
                     "1-1"
                     "1*1"
                     "12*34"
                     "CAST(1 AS SIGNED)"
                     "CAST(1 AS UNSIGNED)"
                     "CAST(18446744073709551615 AS UNSIGNED)"
                     "-CAST(18446744073709551615 AS UNSIGNED)"
                     "CAST(1 AS UNSIGNED)/2"
                     "CAST(1 AS UNSIGNED)/CAST(2 AS UNSIGNED)"
                     "COALESCE(CAST(18446744073709551615 AS UNSIGNED),0)"
                     "1 DIV 2"
                     "ROUND(1,0)"
                     "TRUNCATE(1,0)"
                     "CAST(NULL AS UNSIGNED)"
                     "CAST('123' AS SIGNED)"
                     "b'01'+1"
                     "1+b'01'"
                     "b'01'-1"
                     "b'01'*2"
                     "CAST(1 AS UNSIGNED)+2"
                     "CAST(3 AS UNSIGNED)-2"
                     "-(b'01'+1)"
                     "-(1+1)"
                     "MOD(b'01',2)"
                     "MOD(2,b'01')"
                     "123 DIV 2"
                     "1.25 DIV 0.1"
                     "'12' DIV 2"
                     "12 DIV '2'"
                     "b'01' DIV 2"
                     "2 DIV b'01'"
                     "NULL DIV 2"
                     "1 DIV NULL"
                     "CAST(1 AS UNSIGNED) DIV 2"
                     "1 DIV 2e0"
                     "9223372036854775807+0"
                     "MOD(CAST(1 AS UNSIGNED),2)"
                     "MOD(2,CAST(1 AS UNSIGNED))"
                     "b'01'/b'01'"
                     "b'01'/CAST(2 AS UNSIGNED)"
                     "1=1"
                     "1<2"
                     "NOT 0"
                     "1 IS NULL"
                     "EXISTS(SELECT 1)"
                     "-(1=1)"
                     "-(NOT 0)"
                     "(1=1)+1" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value"
                   Contract.query (sprintf "literal-%d-text" index) sql
                   Contract.preparedQuery (sprintf "literal-%d-binary" index) sql [||]
               Contract.execute "integer-variable" "SET @value=7"
               Contract.query "integer-variable-descriptors" "SELECT @value AS a,@value+1 AS b,-@value AS c,@value DIV 2 AS d"
               let sql = "SELECT n+1 AS a,n-1 AS b,n*2 AS c,n DIV 2 AS d,MOD(n,2) AS e,u+1 AS f,u*2 AS g,-u AS h,u DIV 2 AS i,MOD(u,2) AS j,COALESCE(u+1,0) AS k FROM integer_expressions ORDER BY n"
               Contract.query "columns-text" sql
               Contract.preparedQuery "columns-binary" sql [||]
               Contract.execute "signed-subtraction-mode" "SET sql_mode='NO_UNSIGNED_SUBTRACTION'"
               Contract.query "signed-subtraction-text" "SELECT CAST(1 AS UNSIGNED)-2 AS value"
               Contract.preparedQuery "signed-subtraction-binary" "SELECT CAST(1 AS UNSIGNED)-2 AS value" [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS integer_expressions"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private divisionOperandDescriptors =
        { Name = "division-operand-descriptors"
          Setup = [| "SET sql_mode=''"
                     "CREATE TABLE division_operands(n INT,s VARCHAR(10),b VARBINARY(10),j JSON)"
                     "INSERT INTO division_operands VALUES(1,'1','1','1'),(NULL,NULL,NULL,NULL)" |]
          Steps =
            [| for increment in [ 0; 4; 10; 30 ] do
                   Contract.execute (sprintf "increment-%d" increment) (sprintf "SET div_precision_increment=%d" increment)
                   for index, expression in
                       [ "b'01'/2"
                         "2/b'01'"
                         "b''/2"
                         "b'100000001'/2"
                         "X'010001'/2"
                         "b'01'/2.00"
                         "b'01'/2e0"
                         "b'01'/'2'"
                         "'1'/2"
                         "1/'2'"
                         "_binary X'31'/2"
                         "_binary b'01'/2"
                         "NULL/2"
                         "1/NULL"
                         "NULL/NULL"
                         "b'01'/NULL"
                         "NULL/b'01'"
                         "(SELECT b'01')/2"
                         "(SELECT b'01' WHERE 1)/2"
                         "(SELECT b'01' FROM (SELECT 1)t)/2"
                         "CAST('2020-01-01' AS DATE)/2"
                         "CAST('2020-01-02 03:04:05' AS DATETIME)/2"
                         "CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))/2"
                         "CAST('-12:34:56.123456' AS TIME(6))/2"
                         "CAST('2020-00-01' AS DATE)/2"
                         "CAST('2020-00-01 03:04:05.123456' AS DATETIME(6))/2"
                         "CAST('1' AS JSON)/2"
                         "CAST(1 AS UNSIGNED)/CAST(2 AS UNSIGNED)"
                         "CAST(1.00 AS DECIMAL(5,2))/NULL"
                         "CAST('2020-01-01' AS DATE)/NULL"
                         "CAST('2020-01-01' AS DATETIME(6))/NULL"
                         "NULL/CAST('2020-01-01' AS DATETIME(6))" ] |> List.indexed do
                       let name = sprintf "increment-%d-operand-%d" increment index
                       let sql = sprintf "SELECT %s AS value /* increment=%d */" expression increment
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||]
                   let sql = sprintf "SELECT n/2 AS n,s/2 AS s,b/2 AS b,j/2 AS j FROM division_operands ORDER BY n /* increment=%d */" increment
                   Contract.query (sprintf "columns-%d-text" increment) sql
                   Contract.preparedQuery (sprintf "columns-%d-binary" increment) sql [||]
                   for column in [ "n"; "s"; "b"; "j" ] do
                       let sql = sprintf "SELECT (SELECT %s FROM division_operands WHERE n=1)/2 AS value /* increment=%d */" column increment
                       let name = sprintf "scalar-%s-%d" column increment
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS division_operands"; "SET sql_mode=DEFAULT"; "SET div_precision_increment=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private temporalArithmeticDescriptors =
        { Name = "temporal-arithmetic-descriptors"
          Setup = [| "SET sql_mode=''"; "SET time_zone='+00:00'"
                     "CREATE TABLE temporal_arithmetic(id INT,d DATE,dt DATETIME(6),tm TIME(3),ts TIMESTAMP(6))"
                     "INSERT INTO temporal_arithmetic VALUES(1,'2020-01-01','2020-01-01','-12:34:56','2020-01-01'),(2,'2020-01-02','2020-01-02 03:04:05.123456','12:34:56.123','2020-01-02 03:04:05.123456')" |]
          Steps =
            [| for index, operand in
                   [ "CAST('2020-01-01' AS DATE)"; "CAST('2020-01-01' AS DATETIME)"
                     "CAST('2020-01-01' AS DATETIME(6))"; "CAST('2020-01-01 03:04:05.123456' AS DATETIME(6))"
                     "CAST('-12:34:56' AS TIME(3))"; "TIMESTAMP '2020-01-01 00:00:00.000'"; "TIMESTAMP '2020-01-01 00:00:00.123'" ] |> List.indexed do
                   for operation, expression in
                       [ "add", operand + "+0"; "subtract", operand + "-0"; "multiply", operand + "*1"
                         "negate", "-" + operand; "absolute", "ABS(" + operand + ")"; "modulo", "MOD(" + operand + ",2)" ] do
                       let name = sprintf "%d-%s" index operation
                       let sql = "SELECT " + expression + " AS value"
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||]
               for operation, format in
                   [ "add", "%s+0"; "multiply", "%s*1"; "negate", "-%s"; "absolute", "ABS(%s)" ] do
                   let columns = [ "d"; "dt"; "tm"; "ts" ] |> List.map (fun column -> format.Replace("%s",column) + " AS " + column) |> String.concat ","
                   let sql = "SELECT " + columns + " FROM temporal_arithmetic ORDER BY id"
                   Contract.query (operation + "-columns-text") sql
                   Contract.preparedQuery (operation + "-columns-binary") sql [||]
               Contract.query "string-time-result" "SELECT SUBTIME('2008-01-01 01:01:01.000002','1:1:1.000002') AS value"
               Contract.query "typed-time-result" "SELECT CAST(SUBTIME(CAST('2008-01-01 01:01:01.000002' AS DATETIME(6)),'1:1:1.000002') AS CHAR) AS value"
               Contract.prepare "prepare-datetime" "datetime-arithmetic" Query "SELECT ?+0 AS value" [| box (DateTime(2020,1,1)) |]
               Contract.invoke "whole-datetime" "datetime-arithmetic" OracleSuccess
               Contract.invokeWith "fractional-datetime" "datetime-arithmetic" [| box (DateTime(2020,1,1).AddMilliseconds(123.0)) |]
               Contract.close "close-datetime" "datetime-arithmetic" |]
          Cleanup = [| "DROP TABLE IF EXISTS temporal_arithmetic"; "SET sql_mode=DEFAULT"; "SET time_zone=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private temporalNumericConversion =
        { Name = "temporal-numeric-conversion"
          Setup = [| "SET sql_mode=''"; "SET time_zone='+00:00'"
                     "CREATE TABLE temporal_numbers(d DATE,dt DATETIME(6),tm TIME(6),ts TIMESTAMP(6))"
                     "INSERT INTO temporal_numbers VALUES('2020-01-01','2020-01-02 03:04:05.123456','-12:34:56.123456','2020-01-02 03:04:05.123456')"
                     "CREATE TABLE temporal_zero(d DATE,dt DATETIME(6))"
                     "INSERT INTO temporal_zero VALUES('2020-00-01','2020-00-01 03:04:05.123456')" |]
          Steps =
            [| for name, sql in
                   [ "date", "SELECT CAST('2020-01-01' AS DATE)/2 AS divided,CAST('2020-01-01' AS DATE)+0 AS added,CAST('2020-01-01' AS DATE)=20200101 AS compared"
                     "datetime", "SELECT CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))/2 AS divided,CAST('2020-01-02 03:04:05.123456' AS DATETIME(6))+0 AS added"
                     "time", "SELECT CAST('-12:34:56.123456' AS TIME(6))/2 AS divided"
                     "zero", "SELECT d/2 AS date_value,dt/2 AS datetime_value FROM temporal_zero"
                     "columns", "SELECT d/2 AS d,dt/2 AS dt,tm/2 AS tm,ts/2 AS ts FROM temporal_numbers" ] do
                   Contract.query (name + "-text") sql
                   Contract.preparedQuery (name + "-binary") sql [||]
               Contract.execute "local-zone" "SET time_zone='+02:00'"
               Contract.query "local-timestamp-text" "SELECT ts/2 AS value FROM temporal_numbers"
               Contract.preparedQuery "local-timestamp-binary" "SELECT ts/2 AS value FROM temporal_numbers" [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS temporal_numbers"; "DROP TABLE IF EXISTS temporal_zero"; "SET sql_mode=DEFAULT"; "SET time_zone=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private componentDateTimeFractions =
        { Name = "component-datetime-fractions"
          Setup = [| "CREATE TABLE component_fraction(dt DATETIME(2))"; "CREATE TABLE ts_fraction(ts TIMESTAMP(2))" |]
          Steps =
            [| for modeIndex, (mode, strict, truncate) in
                   [ "ALLOW_INVALID_DATES", false, false
                     "ALLOW_INVALID_DATES,STRICT_TRANS_TABLES", true, false
                     "ALLOW_INVALID_DATES,TIME_TRUNCATE_FRACTIONAL", false, true ] |> List.indexed do
                   Contract.execute (sprintf "mode-%d" modeIndex) ("SET sql_mode='" + mode + "'")
                   for precision in 0..6 do
                       for fraction in [ "123456"; "999999" ] do
                           let name = sprintf "precision-%d-%d-%s" modeIndex precision fraction
                           let sql = sprintf "SELECT CAST(CAST('2020-00-01 03:04:05.%s' AS DATETIME(%d)) AS CHAR) AS value" fraction precision
                           Contract.query (name + "-text") sql
                           Contract.preparedQuery (name + "-binary") sql [||]
                   for dateIndex, (date, rejectsCarry) in
                       [ "2020-00-01", true; "0000-00-00", true; "2023-02-31", true
                         "0000-02-29", true; "0000-02-31", true
                         "0000-01-01", false; "0000-03-01", false; "0000-12-31", false ] |> List.indexed do
                       for clockIndex, (clock, carries) in
                           [ "03:04:05.129", false; "03:04:05.999", true; "23:59:59.999", true ] |> List.indexed do
                           let name = sprintf "%d-%d-%d" modeIndex dateIndex clockIndex
                           let input = date + " " + clock
                           let sql = "SELECT CAST(CAST('" + input + "' AS DATETIME(2)) AS CHAR) AS value"
                           Contract.query (name + "-cast") sql
                           Contract.query (name + "-cast-warnings") "SHOW WARNINGS"
                           Contract.preparedQuery (name + "-binary-cast") sql [||]
                           Contract.query (name + "-binary-warnings") "SHOW WARNINGS"
                           Contract.execute (name + "-clear") "DELETE FROM component_fraction"
                           let insert = Contract.execute (name + "-insert") ("INSERT INTO component_fraction VALUES('" + input + "')")
                           if strict && not truncate && rejectsCarry && carries then
                               insert |> Contract.fails 1292 "22007"
                           else insert
                           Contract.query (name + "-insert-warnings") "SHOW WARNINGS"
                           Contract.query (name + "-stored") "SELECT CAST(dt AS CHAR) AS value FROM component_fraction"
               for modeIndex, (mode, strict) in [ "", false; "STRICT_TRANS_TABLES", true; "TIME_TRUNCATE_FRACTIONAL", false ] |> List.indexed do
                   Contract.execute (sprintf "timestamp-mode-%d" modeIndex) ("SET sql_mode='" + mode + "'")
                   for inputIndex, (input, valid) in
                       [ "0000-00-00 00:00:00", true
                         "0000-00-00 00:00:00.001", false
                         "0000-00-00 00:00:00.129", false
                         "2020-00-01 00:00:00", false
                         "0000-01-01 00:00:00", false ] |> List.indexed do
                       let name = sprintf "timestamp-%d-%d" modeIndex inputIndex
                       Contract.execute (name + "-clear") "DELETE FROM ts_fraction"
                       let insert = Contract.execute (name + "-insert") ("INSERT INTO ts_fraction VALUES('" + input + "')")
                       if strict && not valid then insert |> Contract.fails 1292 "22007" else insert
                       Contract.query (name + "-warnings") "SHOW WARNINGS"
                       Contract.query (name + "-stored") "SELECT CAST(ts AS CHAR) AS value FROM ts_fraction" |]
          Cleanup = [| "DROP TABLE IF EXISTS component_fraction"; "DROP TABLE IF EXISTS ts_fraction"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private calendarCasts =
        { Name = "calendar-casts"
          Setup = [||]
          Steps =
            [| for index, mode in
                   [ ""; "NO_ZERO_DATE"; "NO_ZERO_IN_DATE"; "NO_ZERO_DATE,NO_ZERO_IN_DATE"
                     "STRICT_TRANS_TABLES"; "ALLOW_INVALID_DATES" ] |> List.indexed do
                   Contract.execute (sprintf "mode-%d" index) ("SET sql_mode='" + mode + "'")
                   for inputIndex, source in
                       [ "'2020-00-01'"; "'0000-00-00'"; "'0000-01-01'"; "'2023-02-31'"
                         "'0000-02-29'"; "'0000-02-31'"
                         "'2020-13-01'"; "'nonsense'"; "'0'"; "0"; "20200101"
                         "'2020-00-01 03:04:05.123456'" ] |> List.indexed do
                       for target in [ "DATE"; "DATETIME(6)" ] do
                           let name = sprintf "%d-%d-%s" index inputIndex target
                           let sql = sprintf "SELECT CAST(%s AS %s)/1 AS value" source target
                           Contract.query (name + "-text") sql
                           Contract.query (name + "-text-warnings") "SHOW WARNINGS"
                           Contract.preparedQuery (name + "-binary") sql [||]
                           Contract.query (name + "-binary-warnings") "SHOW WARNINGS"
               Contract.execute "text-precision-mode" "SET sql_mode=''"
               for index, expression in
                   [ "CAST(CAST('2020-00-01' AS DATETIME(6)) AS CHAR)"
                     "CONCAT(CAST('2020-01-01' AS DATETIME(3)))"
                     "CAST(CAST('-12:34:56.12' AS TIME(4)) AS CHAR)"
                     "CAST(CAST(NULL AS DATETIME(6)) AS CHAR)"
                     "HEX(CAST(CAST('2020-01-01' AS DATETIME(3)) AS BINARY))" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value"
                   Contract.query (sprintf "text-precision-%d" index) sql
                   Contract.preparedQuery (sprintf "binary-text-precision-%d" index) sql [||]
               Contract.execute "allow-year-zero-invalid" "SET sql_mode='ALLOW_INVALID_DATES'"
               Contract.query "year-zero-invalid-literals" "SELECT CAST(DATE '0000-02-29' AS CHAR) AS d,CAST(TIMESTAMP '0000-02-31 03:04:05' AS CHAR) AS dt"
               Contract.execute "reject-year-zero-invalid" "SET sql_mode=''"
               Contract.query "year-zero-invalid-literal-error" "SELECT DATE '0000-02-29' AS d" |> Contract.fails 1525 "HY000"
               Contract.execute "strict-zero-modes" "SET sql_mode='STRICT_TRANS_TABLES,NO_ZERO_DATE,NO_ZERO_IN_DATE'"
               Contract.query "year-zero-literals" "SELECT DATE '0000-01-01'/1 AS d,TIMESTAMP '0000-01-01 03:04:05'/1 AS dt"
               Contract.execute "year-zero-table" "CREATE TABLE calendar_year(d DATE,dt DATETIME(6))"
               Contract.execute "year-zero-insert" "INSERT INTO calendar_year VALUES('0000-01-01','0000-01-01 03:04:05.123456')"
               Contract.query "year-zero-columns" "SELECT d/1 AS d,dt/1 AS dt FROM calendar_year" |]
          Cleanup = [| "DROP TABLE IF EXISTS calendar_year"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private numericAggregateConversion =
        { Name = "numeric-aggregate-conversion"
          Setup =
            [| "CREATE TABLE aggregate_warning_input(id INT PRIMARY KEY,v VARCHAR(30))"
               "INSERT INTO aggregate_warning_input VALUES(1,'12x'),(2,'12x'),(3,'bad'),(4,''),(5,'  '),(6,NULL),(7,' 2 ')" |]
          Steps =
            [| for name, sql in
                   [ "single", "SELECT SUM('001.25') AS s,AVG('001.25') AS a,SUM('foo') AS invalid_sum,SUM('12abc') AS prefix_sum"
                     "binary", "SELECT SUM(_binary '12x') AS s,AVG(_binary 'bad') AS a"
                     "overflow", "SELECT SUM('1e999') AS s,AVG('1e-999') AS a"
                     "distinct", "SELECT SUM(DISTINCT v) AS s,AVG(DISTINCT v) AS a,COUNT(DISTINCT v) AS c FROM (SELECT '1' AS v UNION ALL SELECT '01' UNION ALL SELECT '2') t"
                     "window", "SELECT SUM(v) OVER () AS s,AVG(v) OVER () AS a FROM (SELECT '001.25' AS v) t" ] do
                   Contract.query (name + "-text") sql
                   Contract.query (name + "-text-warnings") "SHOW WARNINGS"
                   Contract.preparedQuery (name + "-binary") sql [||]
                   Contract.query (name + "-binary-warnings") "SHOW WARNINGS"
               for index, expression in
                   [ "SUM(v)"; "AVG(v)"; "SUM(DISTINCT v)"; "AVG(DISTINCT v)"; "COUNT(v)"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN CURRENT ROW AND CURRENT ROW)"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)"
                     "AVG(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND CURRENT ROW)"
                     "SUM(v) OVER()"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "AVG(v) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING)"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING)"
                     "SUM(v) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 FOLLOWING)"
                     "SUM(v) OVER(PARTITION BY MOD(id,2) ORDER BY id)"
                     "SUM(v) OVER(ORDER BY FLOOR(id/3))" ] |> List.indexed do
                   let name = sprintf "warnings-%d" index
                   let order = if expression.Contains("OVER") then " ORDER BY id" else ""
                   let sql = "SELECT " + expression + " AS value FROM aggregate_warning_input" + order
                   Contract.query (name + "-text") sql
                   Contract.query (name + "-text-warnings") "SHOW WARNINGS"
                   Contract.preparedQuery (name + "-binary") sql [||]
                   Contract.query (name + "-binary-warnings") "SHOW WARNINGS" |]
          Cleanup = [| "DROP TABLE IF EXISTS aggregate_warning_input" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private singleGroupOrdering =
        { Name = "single-group-ordering"
          Setup =
            [| "CREATE TABLE single_group_order(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body))"
               "INSERT INTO single_group_order VALUES(1,'database concurrency'),(2,'storage transactions')"
               "ANALYZE TABLE single_group_order" |]
          Steps =
            [| for index, sql in
                   [ "SELECT COUNT(*) AS n FROM single_group_order ORDER BY body"
                     "SELECT COUNT(*) AS n FROM single_group_order WHERE 1=0 ORDER BY body"
                     "SELECT COUNT(*) AS n FROM single_group_order WHERE MATCH(body) AGAINST('database') ORDER BY body" ]
                   |> List.indexed do
                   let name = sprintf "order-%d" index
                   Contract.query (name + "-text") sql
                   Contract.preparedQuery (name + "-binary") sql [||]
               for index, (sql, code, state) in
                   [ "SELECT COUNT(*) AS n FROM single_group_order ORDER BY missing", 1054, "42S22"
                     "SELECT COUNT(*) AS n FROM single_group_order GROUP BY body ORDER BY id", 1055, "42000"
                     "SELECT COUNT(*) AS n,ROW_NUMBER() OVER(ORDER BY body) AS rn FROM single_group_order", 1140, "42000" ]
                   |> List.indexed do
                   let name = sprintf "invalid-order-%d" index
                   Contract.query (name + "-text") sql |> Contract.fails code state
                   Contract.preparedQuery (name + "-binary") sql [||] |> Contract.fails code state
               Contract.execute "reset-counter" "SET @n=0"
               Contract.query "sort-side-effect" "SELECT COUNT(*) AS n FROM single_group_order ORDER BY (@n:=@n+1)"
               Contract.query "counter" "SELECT @n"
               Contract.query "projection-side-effect" "SELECT COUNT(*)+(@n:=@n+1) AS n FROM single_group_order"
               Contract.query "projection-counter" "SELECT @n" |]
          Cleanup = [| "DROP TABLE IF EXISTS single_group_order" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private naturalAggregates =
        { Name = "natural-fulltext-aggregates"
          Setup =
            [| "CREATE TABLE natural_aggregates(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body))"
               "INSERT INTO natural_aggregates VALUES(1,'database concurrency'),(2,'storage transactions')"
               "ANALYZE TABLE natural_aggregates" |]
          Steps =
            [| for index, sql in
                   [ "SELECT COUNT(*) AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('database')"
                     "SELECT SUM(id)+COUNT(*) AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('database')"
                     "SELECT 1 AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('database') HAVING COUNT(*)>0"
                     "SELECT COUNT(*) AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('absent')"
                     "SELECT COUNT(*) AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('database') LIMIT 1"
                     "SELECT SUM(COUNT(*)) OVER() AS n FROM natural_aggregates WHERE MATCH(body) AGAINST('database')" ]
                   |> List.indexed do
                   let name = sprintf "aggregate-%d" index
                   Contract.query (name + "-text") sql
                   Contract.preparedQuery (name + "-binary") sql [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS natural_aggregates" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private shortFullTextLookups =
        { Name = "short-fulltext-lookups"
          Setup =
            [| "CREATE TABLE short_lookups(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body))"
               "INSERT INTO short_lookups VALUES(1,'ffi'),(2,'orchard'),(3,'zzzz'),(4,'ﬃ')"
               "ANALYZE TABLE short_lookups" |]
          Steps =
            [| for modeIndex, mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE"; "WITH QUERY EXPANSION" ] |> List.indexed do
                   for queryIndex, query in [ "ﬃ"; "ﬃ ﬃ"; "+ﬃ"; "\"ﬃ\""; "\"ﬃ orchard\""; "ffi ffi"; "+ffi +ﬃ"; "ﬃ ﬃ ﬃ" ] |> List.indexed do
                       let name = sprintf "short-%d-%d" modeIndex queryIndex
                       let score = sprintf "MATCH(body) AGAINST('%s' %s)" query mode
                       let matches = "SELECT id FROM short_lookups WHERE " + score + " ORDER BY id"
                       let scores = "SELECT id,CAST(" + score + " AS DECIMAL(12,5)) AS relevance FROM short_lookups ORDER BY id"
                       Contract.query (name + "-text") matches
                       Contract.preparedQuery (name + "-binary") matches [||]
                       Contract.query (name + "-scores-text") scores
                       Contract.preparedQuery (name + "-scores-binary") scores [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS short_lookups" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private fullTextExpansionSeeds =
        let values =
            [ for id in 1..25 do
                  let word = "uniqueword" + string (char (int 'a' + id - 1))
                  yield sprintf "(%d,'orchard %s')" id word
                  yield sprintf "(%d,'%s')" (100 + id) word
              yield "(999,'unrelated')" ] |> String.concat ","
        let score = "MATCH(body) AGAINST('orchard' WITH QUERY EXPANSION)"
        { Name = "fulltext-expansion-seeds"
          Setup =
            [| "CREATE TABLE expansion_seeds(id INT PRIMARY KEY,body TEXT,FULLTEXT ft(body))"
               "INSERT INTO expansion_seeds VALUES" + values
               "ANALYZE TABLE expansion_seeds" |]
          Steps =
            [| Contract.query "reported-limit" "SELECT @@ft_query_expansion_limit,@@GLOBAL.ft_query_expansion_limit"
               Contract.query "global-limit" "SHOW GLOBAL VARIABLES LIKE 'ft_query_expansion_limit'"
               Contract.query "session-show-limit" "SHOW SESSION VARIABLES LIKE 'ft_query_expansion_limit'"
               Contract.query "session-read-limit" "SELECT @@SESSION.ft_query_expansion_limit" |> Contract.fails 1238 "HY000"
               Contract.execute "session-set-limit" "SET SESSION ft_query_expansion_limit=1" |> Contract.fails 1238 "HY000"
               Contract.execute "global-set-limit" "SET GLOBAL ft_query_expansion_limit=1" |> Contract.fails 1238 "HY000"
               for index, sql in
                   [ "SELECT id FROM expansion_seeds WHERE " + score + " ORDER BY id"
                     "SELECT id FROM expansion_seeds WHERE id=125 AND " + score
                     "SELECT id FROM expansion_seeds WHERE id>=121 AND " + score + " ORDER BY id"
                     "SELECT id,CAST(" + score + " AS DECIMAL(12,5)) AS relevance FROM expansion_seeds ORDER BY id" ] |> List.indexed do
                   Contract.query (sprintf "seeds-%d-text" index) sql
                   Contract.preparedQuery (sprintf "seeds-%d-binary" index) sql [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS expansion_seeds" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private naturalPhrases =
        { Name = "natural-fulltext-phrases"
          Setup =
            [| "CREATE TABLE natural_phrases(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b))"
               "INSERT INTO natural_phrases VALUES(1,'mysql','security'),(2,'mysql security',''),(3,'mysqlsecurity',NULL),(4,'my','sql'),(5,'sql','mysql security'),(6,'mysql extra security',''),(7,'security mysql',''),(8,'database',''),(9,'mysql the security',''),(10,'mysql x security','')"
               "ANALYZE TABLE natural_phrases" |]
          Steps =
            [| for modeIndex, mode in [ "IN NATURAL LANGUAGE MODE"; "WITH QUERY EXPANSION" ] |> List.indexed do
                   for termIndex, term in
                       [ "\"mysql security\""; "\"mysql security\" database"; "mysql \"security database\""; "\"mysql security\" \"security mysql\""; "\"mysql the security\""; "\"the mysql\""; "\"mysql x security\""; "\"mysql mysql\""; "\"mysql security"; "mysql security\""; "\"mysql security\" @100"; "mysql mysql"; "\"mysql\" mysql"; "\"mysql security\" mysql mysql"; "\"mysql security\" \"mysql security\" \"mysql security\""; "\"mysql mysql\" mysql"; "\"security database\" mysql"; "\"mysql security\" security" ] |> List.indexed do
                       let name = sprintf "phrase-%d-%d" modeIndex termIndex
                       let against = sprintf "MATCH(a,b) AGAINST('%s' %s)" term mode
                       let matches = sprintf "SELECT id FROM natural_phrases WHERE %s ORDER BY id" against
                       let scores = sprintf "SELECT id,CAST(%s AS DECIMAL(12,5)) AS relevance FROM natural_phrases ORDER BY id" against
                       Contract.query (name + "-text") matches
                       Contract.preparedQuery (name + "-binary") matches [||]
                       Contract.query (name + "-scores-text") scores
                       Contract.preparedQuery (name + "-scores-binary") scores [||] |]
          Cleanup = [| "DROP TABLE IF EXISTS natural_phrases" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private ngramFullText =
        { Name = "ngram-fulltext"
          Setup =
            [| "CREATE TABLE ngram_docs(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)"
               "INSERT INTO ngram_docs VALUES (1,'生日快乐'),(2,'生日'),(3,'快乐'),(4,'生 日'),(5,'生日 开心'),(6,'abc'),(7,'ab bc'),(8,'a,b'),(9,'dbms'),(10,'mysql'),(11,'日本語'),(12,'日本 語'),(13,'한국어'),(14,'한국 어'),(15,'🙂生日'),(16,'生🙂日'),(17,'生'),(18,''),(19,NULL),(20,'生日生日'),(21,'生日!快乐'),(22,'日快')"
               "CREATE TABLE ngram_boundaries(id INT PRIMARY KEY, body TEXT, FULLTEXT KEY ft(body) WITH PARSER ngram)"
               "INSERT INTO ngram_boundaries VALUES(1,'cdabef'),(2,'cd ab ef'),(3,'cdef'),(4,'cd xx ef'),(5,'cdaef'),(6,'ab生日'),(7,'生日ab快乐'),(8,'生日xx快乐'),(9,'生日 快乐'),(10,'生日　快乐'),(11,'生日，快乐'),(12,'生😀日'),(13,'𠮷野家'),(14,'b,c'),(15,'b_c'),(16,'b''c'),(17,'b-c'),(18,'b.c'),(19,'bc'),(20,'áb'),(21,'生日a快乐'),(22,'生日abc快乐'),(23,'bé'),(24,'生日 生 快乐'),(25,'生日 x 快乐'),(26,'生日 a 快乐')"
               "CREATE TABLE ngram_columns(id INT PRIMARY KEY,a TEXT,b TEXT,FULLTEXT(a,b) WITH PARSER ngram)"
               "INSERT INTO ngram_columns VALUES(1,'生日','快乐'),(2,'生日 快乐',''),(3,'生日快乐',NULL),(4,'生','日'),(5,'日','生日 快乐')" |]
          Steps =
            [| for mode in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE"; "WITH QUERY EXPANSION" ] |> List.indexed do
                   let modeIndex, modeSql = mode
                   for termIndex, term in
                       [ "生日快乐"; "生日"; "生"; "生*"; "生日*"; "生日快乐*"; "\"生日快乐\""; "\"生日 快乐\""; "+生日 +快乐"; "生日 -快乐"; "日本語"; "한국어"; "abc"; "ab"; "bc"; "a,b"; "dbms"; "🙂生"; "生🙂日" ] |> List.indexed do
                       let name = sprintf "search-%d-%d" modeIndex termIndex
                       let sql = sprintf "SELECT id FROM ngram_docs WHERE MATCH(body) AGAINST('%s' %s) ORDER BY id" term modeSql
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||]
               for modeIndex, modeSql in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] |> List.indexed do
                   for termIndex, term in
                       [ "cdabef"; "\"cdabef\""; "\"cd ef\""; "cdef"; "生日a快乐"; "生日快乐"; "\"生日 快乐\""; "𠮷野"; "𠮷*"; "áb"; "áb*"; "b,c"; "b_c"; "b'c"; "b-c"; "b.c"; "生日　快乐"; "生日，快乐"; "\"生日，快乐\""; "\"生日 快乐\" @2"; "\"生日 快乐\" @3"; "+\"生日 快乐\""; "+生日 -快乐"; "+(生日 快乐)"; "ab"; "be"; "\"生日 快乐\" @100"; "\"生日 生 快乐\""; "\"生日 x 快乐\""; "\"生 生日\""; "\"生日 生\""; "\"a 生日\"" ] |> List.indexed do
                       let name = sprintf "boundary-%d-%d" modeIndex termIndex
                       let sql = sprintf "SELECT id FROM ngram_boundaries WHERE MATCH(body) AGAINST('%s' %s) ORDER BY id" (term.Replace("'", "''")) modeSql
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||]
               for modeIndex, modeSql in [ "IN NATURAL LANGUAGE MODE"; "IN BOOLEAN MODE" ] |> List.indexed do
                   for termIndex, term in [ "生日快乐"; "\"生日 快乐\""; "生日"; "+生日 +快乐" ] |> List.indexed do
                       let name = sprintf "columns-%d-%d" modeIndex termIndex
                       let sql = sprintf "SELECT id FROM ngram_columns WHERE MATCH(a,b) AGAINST('%s' %s) ORDER BY id" term modeSql
                       Contract.query (name + "-text") sql
                       Contract.preparedQuery (name + "-binary") sql [||]
               Contract.preparedQuery "parameter-query" "SELECT id FROM ngram_docs WHERE MATCH(body) AGAINST(? IN BOOLEAN MODE) ORDER BY id" [| box "生日快乐" |]
               Contract.query "ngram-size" "SELECT @@ngram_token_size,@@GLOBAL.ngram_token_size"
               Contract.query "session-size" "SELECT @@SESSION.ngram_token_size" |> Contract.fails 1238 "HY000"
               Contract.execute "session-size-write" "SET SESSION ngram_token_size=3" |> Contract.fails 1238 "HY000"
               Contract.execute "global-size-write" "SET GLOBAL ngram_token_size=3" |> Contract.fails 1238 "HY000"
               Contract.execute "unknown-parser" "CREATE FULLTEXT INDEX missing ON ngram_docs(body) WITH PARSER missing_parser" |> Contract.fails 1128 "HY000"
               Contract.execute "ordinary-parser" "CREATE INDEX ordinary ON ngram_docs(body(10)) WITH PARSER ngram" |> Contract.fails 1064 "42000"
               Contract.execute "update" "UPDATE ngram_docs SET body='中文检索' WHERE id=1"
               Contract.execute "delete" "DELETE FROM ngram_docs WHERE id=2"
               Contract.execute "insert" "INSERT INTO ngram_docs VALUES(23,'生日')"
               Contract.execute "begin" "START TRANSACTION"
               Contract.execute "uncommitted-update" "UPDATE ngram_docs SET body='生日' WHERE id=3"
               Contract.execute "rollback" "ROLLBACK"
               Contract.query "mutated-postings" "SELECT id FROM ngram_docs WHERE MATCH(body) AGAINST('生日') ORDER BY id"
               Contract.execute "copy-definition" "CREATE TABLE ngram_copied LIKE ngram_docs"
               Contract.execute "copy-rows" "INSERT INTO ngram_copied SELECT * FROM ngram_docs"
               Contract.query "copied-postings" "SELECT id FROM ngram_copied WHERE MATCH(body) AGAINST('生日') ORDER BY id"
               Contract.execute "create-table" "CREATE TABLE ngram_created(id INT PRIMARY KEY, body TEXT)"
               Contract.execute "create-index" "CREATE FULLTEXT INDEX ft ON ngram_created(body) WITH PARSER ngram"
               Contract.execute "alter-table" "CREATE TABLE ngram_altered(id INT PRIMARY KEY, body TEXT)"
               Contract.execute "alter-index" "ALTER TABLE ngram_altered ADD FULLTEXT KEY ft(body) WITH PARSER ngram"
               for table in [ "ngram_created"; "ngram_altered" ] do
                   Contract.execute (table + "-insert") (sprintf "INSERT INTO %s VALUES(1,'生日快乐'),(2,'生日')" table)
                   Contract.query (table + "-phrase") (sprintf "SELECT id FROM %s WHERE MATCH(body) AGAINST('生日快乐' IN BOOLEAN MODE) ORDER BY id" table) |]
          Cleanup = [| "DROP TABLE IF EXISTS ngram_docs,ngram_boundaries,ngram_columns,ngram_copied,ngram_created,ngram_altered" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private storedFunctionSet =
        { Name = "stored-function-set"
          Setup =
            [| "CREATE FUNCTION set_tick(step INT) RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE delta INT DEFAULT step; SET @n=COALESCE(@n,0)+delta,@label=CONCAT('count,',@n); RETURN @n; END"
               "CREATE FUNCTION nested_set(step INT) RETURNS INT NOT DETERMINISTIC NO SQL BEGIN SET @nested=set_tick(step); RETURN @nested; END"
               "CREATE FUNCTION raise_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='failed assignment'; RETURN 0; END"
               "CREATE FUNCTION handled_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE CONTINUE HANDLER FOR SQLSTATE '45000' SET @handled=1; SET @n=@n+1,@bad=raise_set(),@tail=7; RETURN @n; END"
               "CREATE FUNCTION failed_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN SET @n=@n+1,@bad=raise_set(),@tail=9; RETURN 0; END"
               "CREATE FUNCTION loop_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE i INT DEFAULT 0; WHILE i<3 DO SET @n=COALESCE(@n,0)+i; SET i=i+1; END WHILE; RETURN @n; END"
               "CREATE FUNCTION structured_set() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE i INT DEFAULT 0; counting: LOOP SET i=i+1; IF i=3 THEN LEAVE counting; END IF; END LOOP counting; REPEAT SET @n=COALESCE(@n,0)+1; SET i=i-1; UNTIL i=0 END REPEAT; RETURN @n; END"
               "CREATE PROCEDURE set_pair(IN value INT) SET @n=value,@label=CONCAT('pair,',@n),@'quoted,name'=@n"
               "CREATE TABLE set_inputs(id INT PRIMARY KEY)"
               "INSERT INTO set_inputs VALUES(1),(2),(3)"
               "CREATE TRIGGER set_capture BEFORE INSERT ON set_inputs FOR EACH ROW SET @n=NEW.id,@label=CONCAT('row,',@n)" |]
          Steps =
            [| for prepare in [ false; true ] do
                   let query name sql =
                       if prepare then Contract.preparedQuery (name + "-prepared") sql [||]
                       else Contract.query name sql
                   Contract.execute "reset" "SET @n=10,@handled=0,@tail=0"
                   query "local-set" "SELECT set_tick(2) AS value"
                   Contract.query "local-state" "SELECT @n AS n,@label AS label"
                   query "nested-set" "SELECT nested_set(3) AS value"
                   Contract.query "nested-state" "SELECT @n AS n,@label AS label,@nested AS nested"
                   query "handled-set" "SELECT handled_set() AS value"
                   Contract.query "handled-state" "SELECT @n AS n,@handled AS handled,@tail AS tail"
                   Contract.execute "tail-reset" "SET @tail=0"
                   query "failed-set" "SELECT failed_set() AS value" |> Contract.fails 1644 "45000"
                   Contract.query "failed-state" "SELECT @n AS n,@tail AS tail"
                   query "loop-set" "SELECT loop_set() AS value"
                   query "structured-set" "SELECT structured_set() AS value"
                   Contract.execute "window-reset" "SET @n=0"
                   query "window-set" "SELECT id,SUM(set_tick(1)) OVER(ORDER BY id) AS value FROM set_inputs ORDER BY id"
                   Contract.query "window-state" "SELECT @n AS n,@label AS label"
               Contract.execute "procedure-set" "CALL set_pair(23)"
               Contract.query "procedure-state" "SELECT @n AS n,@label AS label,@'quoted,name' AS quoted"
               Contract.execute "trigger-set" "INSERT INTO set_inputs VALUES(31)"
               Contract.query "trigger-state" "SELECT @n AS n,@label AS label"
               Contract.execute "top-level-reset" "SET @n=10,@label='old'"
               Contract.execute "top-level-set" "SET @n=@n+2,@label=CONCAT('count,',@n)"
               Contract.query "top-level-state" "SELECT @n AS n,@label AS label" |]
          Cleanup =
            [| "DROP TABLE IF EXISTS set_inputs"
               "DROP PROCEDURE IF EXISTS set_pair"
               for name in [ "structured_set"; "loop_set"; "failed_set"; "handled_set"; "raise_set"; "nested_set"; "set_tick" ] do
                   "DROP FUNCTION IF EXISTS " + name
               "SET @n=NULL,@label=NULL,@nested=NULL,@handled=NULL,@tail=NULL,@bad=NULL,@'quoted,name'=NULL" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private volatileWindowInputs =
        { Name = "volatile-window-inputs"
          Setup =
            [| "CREATE TABLE volatile_inputs(id INT PRIMARY KEY)"
               "INSERT INTO volatile_inputs VALUES(1),(2),(3)"
               "CREATE FUNCTION tick() RETURNS INT NOT DETERMINISTIC NO SQL RETURN (@n:=COALESCE(@n,0)+1)"
               "CREATE FUNCTION twice_tick() RETURNS INT NOT DETERMINISTIC NO SQL RETURN tick()+tick()"
               "CREATE FUNCTION fail_tick() RETURNS INT NOT DETERMINISTIC NO SQL BEGIN DECLARE ignored INT DEFAULT (@n:=COALESCE(@n,0)+1); SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='counter failed'; RETURN 0; END"
               "CREATE FUNCTION guarded_value(v INT) RETURNS INT DETERMINISTIC NO SQL BEGIN IF v=2 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='invalid row'; END IF; RETURN v*10; END"
               "CREATE TABLE guarded_inputs(id INT PRIMARY KEY,v INT)"
               "INSERT INTO guarded_inputs VALUES(1,1),(2,2),(3,3)" |]
          Steps =
            [| for index, expression in
                   [ "SUM(tick()) OVER()"
                     "SUM(tick()) OVER(ORDER BY id)"
                     "AVG(tick()) OVER(ORDER BY id)"
                     "SUM(tick()) OVER(ORDER BY id DESC)"
                     "SUM(tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "SUM(tick()) OVER(ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING)"
                     "SUM(tick()) OVER(ORDER BY id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING)"
                     "COUNT(tick()) OVER(ORDER BY id)"
                     "MIN(tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "MAX(tick()) OVER(ORDER BY id ROWS BETWEEN 1 FOLLOWING AND 2 FOLLOWING)"
                     "SUM(CONCAT(tick(),'x')) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "JSON_ARRAYAGG(tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "JSON_OBJECTAGG(id,tick()) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "JSON_OBJECTAGG(tick(),id) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "SUM(ABS(CONCAT(tick(),'x'))) OVER(ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)"
                     "BIT_OR(tick()) OVER(ORDER BY id)"
                     "VAR_POP(tick()) OVER(ORDER BY id)" ] |> List.indexed do
                   for prepare in [ false; true ] do
                       let name = sprintf "%d-%b" index prepare
                       Contract.execute (name + "-reset") "SET @n=0"
                       let sql = "SELECT id," + expression + " AS value FROM volatile_inputs ORDER BY id"
                       if prepare then Contract.preparedQuery name sql [||] else Contract.query name sql
                       Contract.query (name + "-warnings") "SHOW WARNINGS"
                       Contract.query (name + "-counter") "SELECT @n AS calls"
               Contract.execute "nested-reset" "SET @n=10"
               Contract.query "nested" "SELECT twice_tick() AS value"
               Contract.query "nested-counter" "SELECT @n AS calls"
               Contract.query "failed-call" "SELECT fail_tick() AS value" |> Contract.fails 1644 "45000"
               Contract.query "failed-counter" "SELECT @n AS calls"
               Contract.execute "failed-update" "UPDATE guarded_inputs SET v=guarded_value(id)" |> Contract.fails 1644 "45000"
               Contract.query "update-atomicity" "SELECT id,v FROM guarded_inputs ORDER BY id"
               Contract.execute "failed-insert" "INSERT INTO guarded_inputs SELECT id+10,guarded_value(id) FROM volatile_inputs" |> Contract.fails 1644 "45000"
               Contract.query "insert-atomicity" "SELECT id,v FROM guarded_inputs ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS guarded_inputs"; "DROP FUNCTION IF EXISTS guarded_value"; "DROP FUNCTION IF EXISTS fail_tick"; "DROP FUNCTION IF EXISTS twice_tick"; "DROP FUNCTION IF EXISTS tick"; "DROP TABLE IF EXISTS volatile_inputs"; "SET @n=NULL" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private offsetRangeAggregates =
        { Name = "offset-range-aggregates"
          Setup =
            [| "CREATE TABLE range_inputs(id INT PRIMARY KEY,k INT,dt DATETIME,v VARCHAR(20),u BIGINT UNSIGNED,d DECIMAL(20,5),f DOUBLE,day DATE,tm TIME)"
               "INSERT INTO range_inputs VALUES(1,NULL,NULL,'1x',NULL,NULL,NULL,NULL,NULL),(2,NULL,NULL,'2x',NULL,NULL,NULL,NULL,NULL),(3,1,'2020-01-02','4x',18446744073709551609,0.1,1,'2020-01-02','00:00:01'),(4,3,'2020-01-04','8x',18446744073709551611,0.3,3,'2020-01-04','00:00:03'),(5,3,'2020-01-04','16x',18446744073709551611,0.3,3,'2020-01-04','00:00:03'),(6,7,'2020-01-08','32x',18446744073709551615,0.7,7,'2020-01-08','00:00:07')" |]
          Steps =
            [| for key, offset in
                   [ "k", "1"; "u", "1"; "d", "0.1"; "f", "1"
                     "dt", "INTERVAL 1 DAY"; "day", "INTERVAL 1 DAY"; "tm", "INTERVAL 1 SECOND" ] do
                   for direction in [ "ASC"; "DESC" ] do
                       for index, frame in
                           [ "UNBOUNDED PRECEDING AND " + offset + " PRECEDING"
                             "UNBOUNDED PRECEDING AND " + offset + " FOLLOWING"
                             offset + " PRECEDING AND UNBOUNDED FOLLOWING"
                             offset + " FOLLOWING AND UNBOUNDED FOLLOWING"
                             offset + " PRECEDING AND " + offset + " FOLLOWING" ] |> List.indexed do
                           for filterName, filter in [ "nullable", ""; "non-null", " WHERE k IS NOT NULL"; "all-null", " WHERE k IS NULL" ] do
                               let name = sprintf "%s-%s-%d-%s" key direction index filterName
                               let sql = "SELECT id,SUM(v) OVER(ORDER BY " + key + " " + direction + " RANGE BETWEEN " + frame + ") AS value FROM range_inputs" + filter + " ORDER BY id"
                               let expectation step =
                                   let increasingOffset = if direction = "ASC" then " FOLLOWING" else " PRECEDING"
                                   if key = "u" && filterName <> "all-null" && frame.Contains(offset + increasingOffset) then
                                       step |> Contract.fails 1690 "22003"
                                   else
                                       step
                               Contract.query (name + "-text") sql |> expectation
                               Contract.query (name + "-text-warnings") "SHOW WARNINGS"
                               Contract.preparedQuery (name + "-binary") sql [||] |> expectation
                               Contract.query (name + "-binary-warnings") "SHOW WARNINGS" |]
          Cleanup = [| "DROP TABLE IF EXISTS range_inputs" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private approximateAggregateDescriptors =
        { Name = "approximate-aggregate-descriptors"
          Setup = [||]
          Steps =
            [| for increment in [ 0; 4; 10 ] do
                   Contract.execute (sprintf "increment-%d" increment) (sprintf "SET div_precision_increment=%d" increment)
                   let sql = sprintf "SELECT SUM(NULL) AS null_sum,AVG(NULL) AS null_average,SUM(DISTINCT NULL) AS distinct_sum,AVG(DISTINCT NULL) AS distinct_average,SUM('1.25') AS text_sum,AVG('1.25') AS text_average,SUM(1.25e0) AS double_sum,AVG(1.25e0) AS double_average,SUM(CAST(NULL AS DECIMAL(10,2))) AS exact_sum,AVG(CAST(NULL AS DECIMAL(10,2))) AS exact_average /* increment=%d */" increment
                   Contract.query (sprintf "text-%d" increment) sql
                   Contract.preparedQuery (sprintf "binary-%d" increment) sql [||] |]
          Cleanup = [| "SET div_precision_increment=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private divisionPrecisionIncrement =
        { Name = "division-precision-increment"
          Setup = [| "CREATE TABLE precision_source(n INT)"; "INSERT INTO precision_source VALUES (1),(2),(2)" |]
          Steps =
            [| Contract.execute "initial" "SET div_precision_increment=4"
               Contract.execute "sql-prepare" "PREPARE precision_query FROM 'SELECT 1/3 AS quotient'"
               Contract.prepare "binary-prepare" "precision" Query "SELECT 1/3 AS quotient /* binary */" [||]
               for increment in [ 0; 1; 4; 9 ] do
                   let name = string increment
                   Contract.execute (name + "-set") ("SET div_precision_increment=" + name)
                   Contract.query (name + "-ordinary") "SELECT 1/3 AS quotient,10.00/3 AS decimal_quotient,(1/3)*3 AS product"
                   Contract.query (name + "-average") "SELECT AVG(n) AS average,AVG(DISTINCT n) AS distinct_average FROM precision_source"
                   Contract.query (name + "-window") "SELECT AVG(n) OVER () AS average,(AVG(n) OVER ())*3 AS product FROM precision_source"
                   Contract.query (name + "-sql") "EXECUTE precision_query"
                   Contract.invoke (name + "-binary") "precision" OracleSuccess
               Contract.close "binary-close" "precision"
               Contract.execute "sql-close" "DEALLOCATE PREPARE precision_query"
               Contract.execute "schema-initial" "SET div_precision_increment=4"
               Contract.execute "schema-prepare" "PREPARE precision_schema FROM 'SELECT n/3 AS quotient FROM precision_source ORDER BY n'"
               Contract.execute "schema-setting" "SET div_precision_increment=1"
               Contract.query "schema-retained" "EXECUTE precision_schema"
               Contract.execute "schema-alter" "ALTER TABLE precision_source ADD COLUMN extra INT"
               Contract.query "schema-refreshed" "EXECUTE precision_schema"
               Contract.execute "schema-close" "DEALLOCATE PREPARE precision_schema"
               Contract.execute "parameter-initial" "SET div_precision_increment=4"
               Contract.prepare "parameter-prepare" "precision-parameter" Query "SELECT ? AS parameter_value,1/3 AS quotient" [| box 1L |]
               Contract.invoke "parameter-first" "precision-parameter" OracleSuccess
               Contract.execute "parameter-setting" "SET div_precision_increment=1"
               Contract.invokeWith "parameter-refresh" "precision-parameter" [| box 1.25M |]
               Contract.close "parameter-close" "precision-parameter"
               for value in [ "-1"; "31" ] do
                   Contract.execute (value + "-clamp") ("SET div_precision_increment=" + value)
                   Contract.query (value + "-warning") "SHOW WARNINGS"
                   Contract.query (value + "-value") "SELECT @@div_precision_increment AS setting"
               for value in [ "1.5"; "'2'"; "NULL" ] do
                   Contract.execute (value + "-invalid") ("SET div_precision_increment=" + value) |> Contract.fails 1232 "42000" |]
          Cleanup = [| "SET div_precision_increment=DEFAULT"; "DROP TABLE IF EXISTS precision_source" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |]; "statement:set", [| "text-differential" |] |] }

    let private unsignedNegation =
        { Name = "unsigned-negation-boundaries"
          Setup = [| "CREATE TABLE negation_values(u BIGINT UNSIGNED,s BIGINT)"
                     "INSERT INTO negation_values VALUES(1,1),(9223372036854775808,-9223372036854775808),(18446744073709551615,2)" |]
          Steps =
            [| Contract.query "small" "SELECT -u AS value FROM negation_values WHERE u=1"
               Contract.query "boundary" "SELECT -u AS value FROM negation_values WHERE u=9223372036854775808"
               Contract.query "unsigned-overflow" "SELECT -u FROM negation_values WHERE u=18446744073709551615" |> Contract.fails 1690 "22003"
               Contract.query "signed-overflow" "SELECT -s FROM negation_values WHERE s=-9223372036854775808" |> Contract.fails 1690 "22003"
               Contract.query "constants" "SELECT -CAST(18446744073709551615 AS UNSIGNED) AS u,-CAST(-9223372036854775808 AS SIGNED) AS s"
               Contract.prepare "prepare" "negation" Query "SELECT -CAST(? AS UNSIGNED) AS value" [| box 1UL |]
               Contract.invoke "bound-small" "negation" OracleSuccess
               Contract.invokeWith "bound-boundary" "negation" [| box 9223372036854775808UL |]
               Contract.invokeWith "bound-overflow" "negation" [| box UInt64.MaxValue |] |> Contract.fails 1690 "22003"
               Contract.invokeWith "bound-recovery" "negation" [| box 2UL |]
               Contract.close "close" "negation"
               Contract.preparedQuery "bare-parameter" "SELECT -? AS value" [| box UInt64.MaxValue |]
               for index, operand in
                   [ "COALESCE(18446744073709551615,0)"
                     "IFNULL(18446744073709551615,0)"
                     "IF(1,18446744073709551615,0)"
                     "GREATEST(18446744073709551615,0)"
                     "LEAST(18446744073709551615,18446744073709551615)"
                     "NULLIF(18446744073709551615,0)"
                     "ROUND(18446744073709551615,0)"
                     "TRUNCATE(18446744073709551615,0)"
                     "CASE WHEN 1 THEN 18446744073709551615 ELSE 0 END"
                     "COALESCE(CAST(-9223372036854775808 AS SIGNED),0)" ] |> List.indexed do
                   let sql = "SELECT -" + operand + " AS value"
                   Contract.query (sprintf "constant-%d-text" index) sql
                   Contract.preparedQuery (sprintf "constant-%d-binary" index) sql [||]
               for index, expression in
                   [ "COALESCE(18446744073709551615,0)+1"
                     "IF(1,18446744073709551615,0)+1"
                     "CASE WHEN 1 THEN 18446744073709551615 ELSE 0 END+1" ] |> List.indexed do
                   let sql = "SELECT " + expression + " AS value"
                   Contract.query (sprintf "mixed-arithmetic-%d-text" index) sql
                   Contract.preparedQuery (sprintf "mixed-arithmetic-%d-binary" index) sql [||]
               Contract.preparedQuery "conditional-runtime-decimal" "SELECT -COALESCE(CAST(? AS UNSIGNED),0) AS value" [| box UInt64.MaxValue |]
               Contract.preparedQuery "conditional-runtime-overflow" "SELECT -COALESCE(CAST(? AS UNSIGNED),CAST(0 AS UNSIGNED)) AS value" [| box UInt64.MaxValue |]
               |> Contract.fails 1690 "22003" |]
          Cleanup = [| "DROP TABLE negation_values" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-differential" |] |] }

    let private constantNullFallbackIndexes =
        { Name = "constant-null-fallback-indexes"
          Setup =
            [| "CREATE TABLE constant_null_fallback(id INT PRIMARY KEY,v INT)"
               "INSERT INTO constant_null_fallback VALUES(1,10),(2,20),(3,30)" |]
          Steps =
            [| Contract.query "coalesce-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=COALESCE(NULL,2)"
               Contract.query "ifnull-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=IFNULL(NULL,2)"
               Contract.query "if-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=IF(1,2,3)"
               Contract.query "nullif-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=NULLIF(2,3)"
               Contract.query "greatest-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=GREATEST(1,2)"
               Contract.query "least-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=LEAST(2,3)"
               Contract.query "negative-if-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=-IF(1,-2,3)"
               Contract.query "searched-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 1=1 THEN 2 ELSE 3 END"
               Contract.query "simple-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE 2 WHEN 2 THEN 3 ELSE 1 END"
               Contract.query "null-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN NULL THEN 3 ELSE 2 END"
               Contract.query "logical-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 1=1 AND NOT(2=3) THEN 2 ELSE 3 END"
               Contract.query "between-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 2 BETWEEN 1 AND 3 THEN 2 ELSE 3 END"
               Contract.query "in-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 2 IN (1,2) THEN 2 ELSE 3 END"
               Contract.query "truth-test-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 1 IS TRUE THEN 2 ELSE 3 END"
               Contract.query "or-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 0 OR 1 THEN 2 ELSE 3 END"
               Contract.query "warning-condition-case-equality"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN ABS('1x')=1 AND 2 BETWEEN 1 AND 3 THEN 2 ELSE 3 END"
               Contract.query "warning-condition-case-conditions" "SHOW WARNINGS"
               Contract.query "case-warning-bound-hit"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 1 THEN ABS('2x') ELSE 3 END"
               Contract.query "case-warning-bound-hit-conditions" "SHOW WARNINGS"
               Contract.query "case-warning-bound-miss"
                   "SELECT v FROM constant_null_fallback WHERE id=CASE WHEN 1 THEN ABS('9x') ELSE 3 END"
               Contract.query "case-warning-bound-miss-conditions" "SHOW WARNINGS"
               Contract.query "warning-bound-hit"
                   "SELECT v FROM constant_null_fallback WHERE id=IF(1,ABS('2x'),3)"
               Contract.query "warning-bound-hit-conditions" "SHOW WARNINGS"
               Contract.query "warning-bound-miss"
                   "SELECT v FROM constant_null_fallback WHERE id=IF(1,ABS('9x'),3)"
               Contract.query "warning-bound-miss-conditions" "SHOW WARNINGS"
               Contract.query "coalesce-range"
                   "SELECT id FROM constant_null_fallback WHERE id BETWEEN IFNULL(NULL,2) AND COALESCE(NULL,3) ORDER BY id" |]
          Cleanup = [| "DROP TABLE constant_null_fallback" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private integralDoubleIndexProbes =
        { Name = "integral-double-index-probes"
          Setup =
            [| "CREATE TABLE narrow_double_probe(id TINYINT PRIMARY KEY)"
               "INSERT INTO narrow_double_probe VALUES(127)"
               "CREATE TABLE wide_double_probe(id BIGINT PRIMARY KEY)"
               "INSERT INTO wide_double_probe VALUES(9007199254740992),(9007199254740993)" |]
          Steps =
            [| Contract.query "out-of-range" "SELECT id FROM narrow_double_probe WHERE id=1000e0"
               Contract.query "rounded-neighbors"
                   "SELECT id FROM wide_double_probe WHERE id=9007199254740992e0 ORDER BY id" |]
          Cleanup = [| "DROP TABLE wide_double_probe"; "DROP TABLE narrow_double_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private isNullFunctionalIndex =
        { Name = "isnull-functional-index"
          Setup =
            [| "CREATE TABLE null_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_isnull ((ISNULL(v))))"
               "INSERT INTO null_probe VALUES(1,NULL),(2,'a'),(3,NULL),(4,'b')" |]
          Steps =
            [| Contract.query "nulls" "SELECT id,ISNULL(v) FROM null_probe WHERE ISNULL(v)=1 ORDER BY id"
               Contract.query "non-nulls" "SELECT id,ISNULL(v) FROM null_probe WHERE ISNULL(v)=0 ORDER BY id"
               Contract.query "unknown" "SELECT id FROM null_probe WHERE ISNULL(v)=NULL ORDER BY id"
               Contract.execute "update" "UPDATE null_probe SET v=NULL WHERE id=2"
               Contract.query "nulls-after-update" "SELECT id FROM null_probe WHERE ISNULL(v)=1 ORDER BY id" |]
          Cleanup = [| "DROP TABLE null_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private asciiFunctionalIndex =
        { Name = "ascii-functional-index"
          Setup =
            [| "CREATE TABLE ascii_probe(id INT PRIMARY KEY,v VARCHAR(20) CHARACTER SET latin1,KEY ix_ascii ((ASCII(v))))"
               "INSERT INTO ascii_probe VALUES(1,'é'),(2,''),(3,NULL),(4,'A')"
               "CREATE TABLE ascii_numeric_probe(id INT PRIMARY KEY,v INT(4) ZEROFILL,KEY ix_ascii ((ASCII(v))))"
               "INSERT INTO ascii_numeric_probe VALUES(1,1),(2,12)" |]
          Steps =
            [| Contract.query "source-bytes" "SELECT id,ASCII(v) FROM ascii_probe ORDER BY id"
               Contract.query "latin1-key" "SELECT id FROM ascii_probe WHERE ASCII(v)=233 ORDER BY id"
               Contract.query "empty-key" "SELECT id FROM ascii_probe WHERE ASCII(v)=0 ORDER BY id"
               Contract.query "padded-numeric-key" "SELECT id,ASCII(v) FROM ascii_numeric_probe WHERE ASCII(v)=48 ORDER BY id"
               Contract.execute "update" "UPDATE ascii_probe SET v='B' WHERE id=1"
               Contract.query "old-key-removed" "SELECT id FROM ascii_probe WHERE ASCII(v)=233 ORDER BY id"
               Contract.query "new-key-added" "SELECT id FROM ascii_probe WHERE ASCII(v)=66 ORDER BY id" |]
          Cleanup = [| "DROP TABLE ascii_probe"; "DROP TABLE ascii_numeric_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private ordFunctionalIndex =
        { Name = "ord-functional-index"
          Setup =
            [| "CREATE TABLE ord_latin1_probe(id INT PRIMARY KEY,v VARCHAR(20) CHARACTER SET latin1,KEY ix_ord ((ORD(v))))"
               "INSERT INTO ord_latin1_probe VALUES(1,'é'),(2,''),(3,NULL),(4,'A')"
               "CREATE TABLE ord_utf8_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_ord ((ORD(v))))"
               "INSERT INTO ord_utf8_probe VALUES(1,'é'),(2,'😀')"
               "CREATE TABLE ord_binary_probe(id INT PRIMARY KEY,v VARBINARY(20),KEY ix_ord ((ORD(v))))"
               "INSERT INTO ord_binary_probe VALUES(1,X'C3A9'),(2,X'')"
               "CREATE TABLE ord_numeric_probe(id INT PRIMARY KEY,v INT(4) ZEROFILL,KEY ix_ord ((ORD(v))))"
               "INSERT INTO ord_numeric_probe VALUES(1,1),(2,12)" |]
          Steps =
            [| Contract.query "latin1-source" "SELECT id,ORD(v) FROM ord_latin1_probe ORDER BY id"
               Contract.query "latin1-key" "SELECT id FROM ord_latin1_probe WHERE ORD(v)=233 ORDER BY id"
               Contract.query "utf8-source" "SELECT id,ORD(v) FROM ord_utf8_probe ORDER BY id"
               Contract.query "utf8-key" "SELECT id FROM ord_utf8_probe WHERE ORD(v)=50089 ORDER BY id"
               Contract.query "binary-source" "SELECT id,ORD(v) FROM ord_binary_probe ORDER BY id"
               Contract.query "numeric-key" "SELECT id,ORD(v) FROM ord_numeric_probe WHERE ORD(v)=48 ORDER BY id"
               Contract.execute "update" "UPDATE ord_latin1_probe SET v='B' WHERE id=1"
               Contract.query "old-key-removed" "SELECT id FROM ord_latin1_probe WHERE ORD(v)=233 ORDER BY id"
               Contract.query "new-key-added" "SELECT id FROM ord_latin1_probe WHERE ORD(v)=66 ORDER BY id" |]
          Cleanup =
            [| "DROP TABLE ord_latin1_probe"
               "DROP TABLE ord_utf8_probe"
               "DROP TABLE ord_binary_probe"
               "DROP TABLE ord_numeric_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private unhexFunctionalIndex =
        { Name = "unhex-functional-index"
          Setup =
            [| "CREATE TABLE unhex_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_unhex ((UNHEX(v))))"
               "INSERT INTO unhex_probe VALUES(1,'41'),(2,'4'),(3,NULL),(4,'61')"
               "CREATE TABLE unhex_invalid_probe(v VARCHAR(20))"
               "INSERT INTO unhex_invalid_probe VALUES('GG')"
               "CREATE TABLE unhex_numeric_probe(id INT PRIMARY KEY,v INT(4) ZEROFILL,KEY ix_unhex ((UNHEX(v))))"
               "INSERT INTO unhex_numeric_probe VALUES(1,12),(2,1)"
               "CREATE TABLE unhex_unicode_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_unhex ((UNHEX(v))))"
               "INSERT INTO unhex_unicode_probe VALUES(1,'C3A9')" |]
          Steps =
            [| Contract.query "decoded-values" "SELECT id,HEX(UNHEX(v)) FROM unhex_probe ORDER BY id"
               Contract.query "decoded-key" "SELECT id FROM unhex_probe WHERE UNHEX(v)=X'41' ORDER BY id"
               Contract.query "decoded-null" "SELECT id FROM unhex_probe WHERE UNHEX(v) IS NULL ORDER BY id"
               Contract.query "padded-numeric-key" "SELECT id,HEX(UNHEX(v)) FROM unhex_numeric_probe WHERE UNHEX(v)=X'0012' ORDER BY id"
               Contract.query "unicode-comparison" "SELECT id FROM unhex_unicode_probe WHERE UNHEX(v)='é'"
               Contract.execute "invalid-strict-insert" "INSERT INTO unhex_probe VALUES(5,'GG')" |> Contract.fails 1411 "HY000"
               Contract.query "strict-insert-atomic" "SELECT COUNT(*) FROM unhex_probe"
               Contract.execute "invalid-strict-index" "CREATE INDEX ix_unhex ON unhex_invalid_probe ((UNHEX(v)))" |> Contract.fails 1411 "HY000"
               Contract.query "failed-index-absent" "SHOW INDEX FROM unhex_invalid_probe"
               Contract.execute "non-strict-mode" "SET sql_mode='NO_ENGINE_SUBSTITUTION'"
               Contract.execute "invalid-non-strict-insert" "INSERT INTO unhex_probe VALUES(5,'GG')"
               Contract.query "invalid-non-strict-warning" "SHOW WARNINGS"
               Contract.query "invalid-null-key" "SELECT id FROM unhex_probe WHERE UNHEX(v) IS NULL ORDER BY id"
               Contract.execute "restore-mode" "SET sql_mode='STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION'" |]
          Cleanup =
            [| "DROP TABLE unhex_probe"
               "DROP TABLE unhex_invalid_probe"
               "DROP TABLE unhex_numeric_probe"
               "DROP TABLE unhex_unicode_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private hexFunctionalIndex =
        { Name = "hex-functional-index"
          Setup =
            [| "CREATE TABLE hex_text_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_hex ((HEX(v))))"
               "INSERT INTO hex_text_probe VALUES(1,'A'),(2,'B'),(3,NULL)"
               "CREATE TABLE hex_latin1_probe(id INT PRIMARY KEY,v VARCHAR(20) CHARACTER SET latin1,KEY ix_hex ((HEX(v))))"
               "INSERT INTO hex_latin1_probe VALUES(1,_latin1 X'E9'),(2,'A')"
               "CREATE TABLE hex_binary_probe(id INT PRIMARY KEY,v VARBINARY(8),KEY ix_hex ((HEX(v))))"
               "INSERT INTO hex_binary_probe VALUES(1,X'00FF'),(2,X'41')"
               "CREATE TABLE hex_numeric_probe(id INT PRIMARY KEY,v INT(4) ZEROFILL,KEY ix_hex ((HEX(v))))"
               "INSERT INTO hex_numeric_probe VALUES(1,12),(2,1)"
               "CREATE TABLE hex_composed_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_hex ((HEX(TRIM(v)))))"
               "INSERT INTO hex_composed_probe VALUES(1,' A '),(2,' B ')"
               "CREATE TABLE hex_decimal_probe(v DECIMAL(20,0),KEY ix_hex ((HEX(v))))" |]
          Steps =
            [| Contract.query "text-key" "SELECT id FROM hex_text_probe WHERE HEX(v)='41' ORDER BY id"
               Contract.query "latin1-key" "SELECT id,HEX(v) FROM hex_latin1_probe WHERE HEX(v)='E9' ORDER BY id"
               Contract.query "null-result" "SELECT id FROM hex_text_probe WHERE HEX(v) IS NULL ORDER BY id"
               Contract.query "binary-key" "SELECT id FROM hex_binary_probe WHERE HEX(v)='00FF' ORDER BY id"
               Contract.query "binary-lowercase-probe" "SELECT id FROM hex_binary_probe WHERE HEX(v)='00ff' ORDER BY id"
               Contract.query "numeric-key" "SELECT id,HEX(v) FROM hex_numeric_probe WHERE HEX(v)='C' ORDER BY id"
               Contract.query "composed-key" "SELECT id FROM hex_composed_probe WHERE HEX(TRIM(v))='41' ORDER BY id"
               Contract.query "numeric-rounding" "SELECT HEX(-1),HEX(2.5),HEX(CAST(2.5 AS DECIMAL(4,1)))"
               Contract.execute "strict-decimal-overflow" "INSERT INTO hex_decimal_probe VALUES(99999999999999999999)" |> Contract.fails 3751 "01000"
               Contract.query "strict-overflow-atomic" "SELECT COUNT(*) FROM hex_decimal_probe"
               Contract.execute "non-strict-mode" "SET sql_mode='NO_ENGINE_SUBSTITUTION'"
               Contract.execute "non-strict-decimal-overflow" "INSERT INTO hex_decimal_probe VALUES(99999999999999999999)"
               Contract.query "non-strict-overflow-warning" "SHOW WARNINGS"
               Contract.query "non-strict-clamped-result" "SELECT HEX(v) FROM hex_decimal_probe"
               Contract.execute "restore-mode" "SET sql_mode='STRICT_TRANS_TABLES,NO_ENGINE_SUBSTITUTION'" |]
          Cleanup =
            [| "DROP TABLE hex_text_probe"
               "DROP TABLE hex_latin1_probe"
               "DROP TABLE hex_binary_probe"
               "DROP TABLE hex_numeric_probe"
               "DROP TABLE hex_composed_probe"
               "DROP TABLE hex_decimal_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private digestFunctionalIndexes =
        { Name = "digest-functional-indexes"
          Setup =
            [| "CREATE TABLE digest_text_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_md5 ((MD5(v))),KEY ix_sha ((SHA1(v))))"
               "INSERT INTO digest_text_probe VALUES(1,'A'),(2,'B'),(3,NULL)"
               "CREATE TABLE digest_binary_probe(id INT PRIMARY KEY,v VARBINARY(8),KEY ix_sha ((SHA(v))))"
               "INSERT INTO digest_binary_probe VALUES(1,X'00FF'),(2,X'41')"
               "CREATE TABLE digest_latin1_probe(id INT PRIMARY KEY,v VARCHAR(20) CHARACTER SET latin1,KEY ix_md5 ((MD5(v))))"
               "INSERT INTO digest_latin1_probe VALUES(1,_latin1 X'E9'),(2,'A')"
               "CREATE TABLE digest_numeric_probe(id INT PRIMARY KEY,v INT(4) ZEROFILL,KEY ix_md5 ((MD5(v))))"
               "INSERT INTO digest_numeric_probe VALUES(1,12),(2,1)"
               "CREATE TABLE digest_composed_probe(id INT PRIMARY KEY,v VARCHAR(20),KEY ix_sha ((SHA1(TRIM(v)))))"
               "INSERT INTO digest_composed_probe VALUES(1,' A '),(2,' B ')" |]
          Steps =
            [| Contract.query "md5-key" "SELECT id FROM digest_text_probe WHERE MD5(v)=MD5('A') ORDER BY id"
               Contract.query "sha1-key" "SELECT id FROM digest_text_probe WHERE SHA1(v)=SHA1('A') ORDER BY id"
               Contract.query "digest-members" "SELECT id FROM digest_text_probe WHERE MD5(v) IN (MD5('A'),MD5('B')) ORDER BY id"
               Contract.query "null-digest" "SELECT id FROM digest_text_probe WHERE MD5(v) IS NULL ORDER BY id"
               Contract.query "binary-sha-alias" "SELECT id FROM digest_binary_probe WHERE SHA1(v)=SHA1(X'00FF') ORDER BY id"
               Contract.query "binary-uppercase-probe" "SELECT id FROM digest_binary_probe WHERE SHA(v)='AA3E5DCDD77B153F2E59BD0D8794FDE33CB4E486' ORDER BY id"
               Contract.query "sha-arity" "SELECT SHA('a','b')" |> Contract.fails 1582 "42000"
               Contract.query "latin1-bytes" "SELECT id,MD5(v) FROM digest_latin1_probe WHERE MD5(v)='3406877694691ddd1dfb0aca54681407' ORDER BY id"
               Contract.query "numeric-display" "SELECT id,MD5(v) FROM digest_numeric_probe WHERE MD5(v)=MD5('0012') ORDER BY id"
               Contract.query "composed-sha1" "SELECT id FROM digest_composed_probe WHERE SHA1(TRIM(v))=SHA1('A') ORDER BY id" |]
          Cleanup =
            [| "DROP TABLE digest_text_probe"
               "DROP TABLE digest_binary_probe"
               "DROP TABLE digest_latin1_probe"
               "DROP TABLE digest_numeric_probe"
               "DROP TABLE digest_composed_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private signFunctionalIndex =
        { Name = "sign-functional-index"
          Setup =
            [| "CREATE TABLE sign_probe(id INT PRIMARY KEY,score INT,KEY ix_sign ((SIGN(score))))"
               "INSERT INTO sign_probe VALUES(1,-5),(2,-2),(3,0),(4,3),(5,9)" |]
          Steps =
            [| Contract.query "negative" "SELECT id FROM sign_probe WHERE SIGN(score)=-1 ORDER BY id"
               Contract.query "nonnegative-range"
                   "SELECT id FROM sign_probe WHERE SIGN(score) BETWEEN 0 AND 1 ORDER BY id"
               Contract.query "ordered" "SELECT id,SIGN(score) FROM sign_probe ORDER BY SIGN(score),id LIMIT 3"
               Contract.execute "update" "UPDATE sign_probe SET score=7 WHERE id=2"
               Contract.query "negative-after-update"
                   "SELECT id FROM sign_probe WHERE SIGN(score)=-1 ORDER BY id"
               Contract.query "positive-after-update"
                   "SELECT id FROM sign_probe WHERE SIGN(score)=1 ORDER BY id" |]
          Cleanup = [| "DROP TABLE sign_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private roundedFunctionalIndexes =
        { Name = "rounded-functional-indexes"
          Setup =
            [| "CREATE TABLE rounded_probe(id INT PRIMARY KEY,exact_value DECIMAL(8,2),text_value VARCHAR(20),KEY ix_floor ((FLOOR(exact_value))),KEY ix_ceil ((CEILING(exact_value))),KEY ix_floor_text ((FLOOR(text_value))))"
               "INSERT INTO rounded_probe VALUES(1,-2.50,'-2.5'),(2,0.01,'0.01'),(3,2.99,'2.99')" |]
          Steps =
            [| Contract.query "floor-decimal" "SELECT id FROM rounded_probe WHERE FLOOR(exact_value)=2"
               Contract.query "ceil-alias" "SELECT id FROM rounded_probe WHERE CEIL(exact_value)=1"
               Contract.query "floor-text" "SELECT id FROM rounded_probe WHERE FLOOR(text_value)=-3"
               Contract.query "ordered" "SELECT id,FLOOR(exact_value),CEILING(exact_value) FROM rounded_probe ORDER BY id"
               Contract.execute "update" "UPDATE rounded_probe SET exact_value=4.01 WHERE id=3"
               Contract.query "floor-after-update" "SELECT id FROM rounded_probe WHERE FLOOR(exact_value)=2"
               Contract.query "ceil-after-update" "SELECT id FROM rounded_probe WHERE CEILING(exact_value)=5"
               Contract.execute "strict-truncated-index"
                   "INSERT INTO rounded_probe VALUES(4,12.00,'12x')" |> Contract.fails 3751 "01000"
               Contract.execute "permissive-mode" "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"
               Contract.execute "permissive-truncated-index"
                   "INSERT INTO rounded_probe VALUES(4,12.00,'12x')"
               Contract.query "truncated-index-warning" "SHOW WARNINGS"
               Contract.query "truncated-index-lookup"
                   "SELECT id FROM rounded_probe WHERE FLOOR(text_value)=12" |]
          Cleanup = [| "DROP TABLE rounded_probe" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private tableStatisticsCache =
        let rows =
            "SELECT TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='table_stats_probe'"
        let firstReadRows =
            "SELECT TABLE_ROWS FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='first_read_stats_probe'"
        let cardinality =
            "SELECT INDEX_NAME,SEQ_IN_INDEX,CARDINALITY FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='table_stats_probe' ORDER BY INDEX_NAME,SEQ_IN_INDEX"

        { Name = "table-statistics-cache"
          Setup =
            [| "CREATE TABLE table_stats_probe(id INT PRIMARY KEY,category INT,KEY k_category(category),KEY k_pair(category,id))"
               "CREATE TABLE first_read_stats_probe(id INT PRIMARY KEY)" |]
          Steps =
            [| Contract.execute "first-read-insert" "INSERT INTO first_read_stats_probe VALUES(1),(2)"
               Contract.query "first-read-after-insert" firstReadRows
               Contract.execute "second-insert" "INSERT INTO first_read_stats_probe VALUES(3)"
               Contract.query "first-read-cache-retained" firstReadRows
               Contract.execute "analyze-empty" "ANALYZE TABLE table_stats_probe"
               Contract.query "empty-estimate" rows
               Contract.query "empty-cardinality" cardinality
               Contract.execute "insert-three" "INSERT INTO table_stats_probe VALUES(1,1),(2,1),(3,2)"
               Contract.query "cached-after-insert" rows
               Contract.query "cached-cardinality-after-insert" cardinality
               Contract.execute "direct-statistics" "SET SESSION information_schema_stats_expiry=0"
               Contract.query "live-after-insert" rows
               Contract.execute "cached-statistics" "SET SESSION information_schema_stats_expiry=86400"
               Contract.query "cached-again" rows
               Contract.execute "analyze-three" "ANALYZE TABLE table_stats_probe"
               Contract.query "refreshed-estimate" rows
               Contract.query "refreshed-cardinality" cardinality
               Contract.query "refreshed-show-index" "SHOW INDEX FROM table_stats_probe"
               Contract.execute "delete-one" "DELETE FROM table_stats_probe WHERE id=3"
               Contract.query "cached-after-delete" rows
               Contract.query "cached-cardinality-after-delete" cardinality
               Contract.execute "direct-again" "SET SESSION information_schema_stats_expiry=0"
               Contract.query "live-after-delete" rows |]
          Cleanup = [| "DROP TABLE table_stats_probe"; "DROP TABLE first_read_stats_probe" |]
          Coverage = [| "statement:analyze-table", [| "text-differential" |] |] }

    let private preparedSchemaChanges =
        { Name = "prepared-schema-type-refresh"
          Setup = [| "CREATE TABLE schema_source(id INT)"; "INSERT INTO schema_source VALUES(1)"; "CREATE TABLE schema_other(id INT)" |]
          Steps =
            [| Contract.execute "initial" "SET @schema_v=-2"
               Contract.execute "prepare" "PREPARE schema_type FROM 'SELECT @schema_v AS value FROM schema_source'"
               Contract.execute "decimal-value" "SET @schema_v=1.75"
               Contract.query "retained" "EXECUTE schema_type"
               Contract.execute "row-write" "UPDATE schema_source SET id=2"
               Contract.query "after-row-write" "EXECUTE schema_type"
               Contract.execute "unrelated-ddl" "ALTER TABLE schema_other ADD COLUMN extra INT"
               Contract.query "after-unrelated-ddl" "EXECUTE schema_type"
               Contract.execute "column-ddl" "ALTER TABLE schema_source ADD COLUMN extra INT"
               Contract.query "after-column-ddl" "EXECUTE schema_type"
               Contract.execute "double-value" "SET @schema_v=1.5e0"
               Contract.execute "index-ddl" "CREATE INDEX schema_ix ON schema_source(id)"
               Contract.query "after-index-ddl" "EXECUTE schema_type"
               Contract.execute "string-value" "SET @schema_v='hello'"
               Contract.execute "comment-ddl" "ALTER TABLE schema_source COMMENT='changed'"
               Contract.query "after-comment-ddl" "EXECUTE schema_type"
               Contract.execute "noop-value" "SET @schema_v=2.75"
               Contract.execute "noop-ddl" "ALTER TABLE schema_source COMMENT='changed'"
               Contract.query "after-noop-ddl" "EXECUTE schema_type"
               Contract.execute "analyze-value" "SET @schema_v=1.5e0"
               Contract.execute "analyze" "ANALYZE TABLE schema_source"
               Contract.query "after-analyze" "EXECUTE schema_type"
               Contract.execute "truncate-value" "SET @schema_v=-8"
               Contract.execute "truncate" "TRUNCATE TABLE schema_source"
               Contract.execute "insert-after-truncate" "INSERT INTO schema_source(id) VALUES(3)"
               Contract.query "after-truncate" "EXECUTE schema_type"
               Contract.execute "view" "CREATE VIEW schema_view AS SELECT id FROM schema_source"
               Contract.execute "nested-view" "CREATE VIEW schema_nested AS SELECT id FROM schema_view"
               Contract.execute "view-prepare" "PREPARE schema_view_type FROM 'SELECT @schema_v AS value FROM schema_nested'"
               Contract.execute "view-value" "SET @schema_v=1.75"
               Contract.execute "view-base-ddl" "ALTER TABLE schema_source ADD COLUMN another INT"
               Contract.query "after-view-base-ddl" "EXECUTE schema_view_type"
               Contract.execute "view-double-value" "SET @schema_v=1.5e0"
               Contract.execute "view-ddl" "ALTER VIEW schema_view AS SELECT id+1 AS id FROM schema_source"
               Contract.query "after-view-ddl" "EXECUTE schema_view_type" |]
          Cleanup = [| "DEALLOCATE PREPARE schema_type"; "DEALLOCATE PREPARE schema_view_type"; "DROP VIEW schema_nested"; "DROP VIEW schema_view"; "DROP TABLE schema_source,schema_other" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private temporaryViewShadowing =
        { Name = "temporary-view-shadowing"
          Setup = [| "CREATE TABLE shadow_source(id INT)"; "INSERT INTO shadow_source VALUES(42)"; "CREATE VIEW shadow_view AS SELECT id FROM shadow_source" |]
          Steps =
            [| Contract.execute "create-temporary" "CREATE TEMPORARY TABLE shadow_view(label VARCHAR(12))"
               Contract.execute "insert-temporary" "INSERT INTO shadow_view VALUES('temporary')"
               Contract.query "read-temporary" "SELECT * FROM shadow_view"
               Contract.query "read-permanent" "SELECT * FROM shadow_view" |> Contract.on "observer"
               Contract.query "describe-temporary" "DESCRIBE shadow_view"
               Contract.prepare "prepare-temporary" "shadow-query" Query "SELECT * FROM shadow_view ORDER BY 1" [||]
               Contract.invoke "execute-temporary" "shadow-query" OracleSuccess
               Contract.execute "update-temporary" "UPDATE shadow_view SET label='changed'"
               Contract.invoke "execute-updated" "shadow-query" OracleSuccess
               Contract.execute "begin" "START TRANSACTION"
               Contract.execute "insert-permanent" "INSERT INTO shadow_source VALUES(50)"
               Contract.execute "alter-permanent-view" "ALTER VIEW shadow_view AS SELECT id+1 AS id FROM shadow_source"
               Contract.execute "rollback" "ROLLBACK"
               Contract.query "ddl-committed" "SELECT id FROM shadow_source ORDER BY id" |> Contract.on "observer"
               Contract.execute "reject-temporary-source" "CREATE VIEW invalid_shadow AS SELECT * FROM shadow_view" |> Contract.fails 1352 "HY000"
               Contract.query "temporary-unchanged" "SELECT * FROM shadow_view"
               Contract.execute "drop-temporary" "DROP TEMPORARY TABLE shadow_view"
               Contract.invoke "execute-revealed-view" "shadow-query" OracleSuccess
               Contract.close "close-query" "shadow-query"
               Contract.query "read-revealed-view" "SELECT * FROM shadow_view ORDER BY id"
               Contract.execute "create-collision" "CREATE TABLE shadow_collision(id INT)"
               Contract.execute "hide-collision" "CREATE TEMPORARY TABLE shadow_collision(label VARCHAR(12))"
               Contract.execute "reject-create-collision" "CREATE VIEW shadow_collision AS SELECT 1 AS id" |> Contract.fails 1050 "42S01"
               Contract.execute "reject-alter-collision" "ALTER VIEW shadow_collision AS SELECT 1 AS id" |> Contract.fails 1347 "HY000"
               Contract.execute "drop-collision-shadow" "DROP TEMPORARY TABLE shadow_collision"
               Contract.execute "drop-collision" "DROP TABLE shadow_collision" |]
          Cleanup = [| "DROP TEMPORARY TABLE IF EXISTS shadow_view"; "DROP VIEW shadow_view"; "DROP TABLE shadow_source" |]
          Coverage = [| "statement:create_table", [| "text-differential"; "prepared-protocol" |] |] }

    let private joinCandidateTraversal =
        let values = [ 0 .. 1000 ] |> List.map (sprintf "(%d,1)") |> String.concat ","
        { Name = "join-candidate-traversal"
          Setup =
            [| "CREATE TABLE candidate_rows(n INT,k INT)"
               "INSERT INTO candidate_rows VALUES" + values |]
          Steps =
            [| for predicate in [ "a.n+b.n<0"; "a.k=b.k AND a.n+b.n<0" ] do
                   for kind in [ "JOIN"; "LEFT JOIN"; "RIGHT JOIN" ] do
                       let sql = "SELECT COUNT(*) AS n FROM candidate_rows a " + kind + " candidate_rows b ON " + predicate
                       Contract.query (kind + "-" + predicate) sql
               Contract.execute "add-index" "ALTER TABLE candidate_rows ADD KEY(k)"
               for kind in [ "JOIN"; "LEFT JOIN"; "RIGHT JOIN" ] do
                   Contract.query ("indexed-" + kind)
                       ("SELECT COUNT(*) AS n FROM candidate_rows a " + kind + " candidate_rows b ON a.k=b.k AND a.n+b.n<0")
               Contract.query "limited-indexed-residual"
                   "SELECT 1 AS n FROM candidate_rows a JOIN candidate_rows b ON a.k=b.k AND a.n+b.n<0 LIMIT 1"
               Contract.query "limited-non-equi"
                   "SELECT 1 AS n FROM candidate_rows a JOIN candidate_rows b ON a.n+b.n>=0 LIMIT 1" |]
          Cleanup = [| "DROP TABLE candidate_rows" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private selectTimeoutSettings =
        { Name = "select-timeout-settings"
          Setup = [||]
          Steps =
            [| Contract.query "defaults" "SELECT @@session.max_execution_time,@@global.max_execution_time"
               for value in [ "1"; "4294967296"; "18446744073709551615" ] do
                   Contract.execute ("set-" + value) ("SET max_execution_time=" + value)
                   Contract.preparedQuery ("read-" + value) "SELECT /*+ MAX_EXECUTION_TIME(10000) */ @@max_execution_time AS n" [||]
               for value in [ "1.5"; "'2'"; "NULL" ] do
                   Contract.execute ("reject-" + value) ("SET max_execution_time=" + value) |> Contract.fails 1232 "42000"
               Contract.execute "negative" "SET max_execution_time=-1"
               Contract.query "negative-warning" "SHOW WARNINGS"
               Contract.query "clamped" "SELECT @@max_execution_time"
               Contract.execute "global" "SET GLOBAL max_execution_time=12"
               Contract.query "existing-session" "SELECT @@session.max_execution_time,@@global.max_execution_time"
               Contract.query "new-session" "SELECT @@max_execution_time" |> Contract.on "timeout-observer"
               Contract.execute "session-default" "SET max_execution_time=DEFAULT"
               Contract.query "inherited-default" "SELECT @@max_execution_time"
               Contract.execute "global-default" "SET GLOBAL max_execution_time=DEFAULT"
               Contract.query "retained-session" "SELECT @@session.max_execution_time,@@global.max_execution_time" |]
          Cleanup = [| "SET GLOBAL max_execution_time=DEFAULT"; "SET max_execution_time=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private selectTimeoutExecution =
        let slowRead = "SELECT n,SLEEP(0.1) FROM deadline_rows"
        { Name = "select-timeout-execution"
          Setup = [| "CREATE TABLE deadline_rows(n INT)"; "INSERT INTO deadline_rows VALUES(1),(2)" |]
          Steps =
            [| Contract.prepare "prepare" "deadline-read" Query slowRead [||]
               Contract.execute "enable" "SET max_execution_time=1"
               Contract.query "table-read" slowRead |> Contract.fails 3024 "HY000"
               Contract.invoke "prepared-read" "deadline-read" (OracleError(3024, "HY000"))
               Contract.query "scalar-sleep" "SELECT SLEEP(0.1) AS slept"
               Contract.query "scalar-benchmark" "SELECT BENCHMARK(10000000,SHA2('abc',256)) AS n"
               Contract.query "union" "SELECT SLEEP(0.1) UNION ALL SELECT 1" |> Contract.fails 3024 "HY000"
               Contract.execute "begin" "START TRANSACTION"
               Contract.execute "insert" "INSERT INTO deadline_rows VALUES(3)"
               Contract.execute "savepoint" "SAVEPOINT retained"
               Contract.query "transaction-read" slowRead |> Contract.fails 3024 "HY000"
               Contract.execute "disable" "SET max_execution_time=0"
               Contract.execute "retained-savepoint" "ROLLBACK TO retained"
               Contract.execute "commit" "COMMIT"
               Contract.query "retained-writes" "SELECT n FROM deadline_rows ORDER BY n"
               Contract.close "close" "deadline-read" |]
          Cleanup = [| "SET max_execution_time=0"; "DROP TABLE deadline_rows" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private routineAlterations =
        { Name = "routine-alterations"
          Setup =
            [| "CREATE USER 'routine_owner'@'%'"
               "CREATE DEFINER='routine_owner'@'%' PROCEDURE alter_p() SELECT 1"
               "CREATE DEFINER='routine_owner'@'%' FUNCTION alter_f() RETURNS INT DETERMINISTIC RETURN 1" |]
          Steps =
            [| Contract.execute "procedure-comment" "ALTER PROCEDURE alter_p COMMENT 'changed'"
               Contract.query "procedure-metadata"
                   "SELECT ROUTINE_COMMENT,SQL_DATA_ACCESS,SECURITY_TYPE FROM information_schema.routines WHERE ROUTINE_NAME='alter_p'"
               Contract.execute "procedure-characteristics"
                   "ALTER PROCEDURE alter_p SQL SECURITY INVOKER READS SQL DATA LANGUAGE SQL COMMENT 'it''s changed'"
               Contract.query "procedure-definition" "SHOW CREATE PROCEDURE alter_p"
               Contract.execute "function-characteristics" "ALTER FUNCTION alter_f SQL SECURITY INVOKER NO SQL COMMENT 'fn'"
               Contract.query "function-metadata"
                   "SELECT ROUTINE_COMMENT,SQL_DATA_ACCESS,SECURITY_TYPE,IS_DETERMINISTIC FROM information_schema.routines WHERE ROUTINE_NAME='alter_f'"
               Contract.query "function-definition" "SHOW CREATE FUNCTION alter_f"
               Contract.execute "duplicate-comment" "ALTER PROCEDURE alter_p COMMENT 'first' COMMENT 'second'"
               Contract.query "last-comment"
                   "SELECT ROUTINE_COMMENT FROM information_schema.routines WHERE ROUTINE_NAME='alter_p'"
               Contract.execute "reject-procedure-determinism" "ALTER PROCEDURE alter_p DETERMINISTIC" |> Contract.fails 1064 "42000"
               Contract.execute "reject-function-determinism" "ALTER FUNCTION alter_f NOT DETERMINISTIC" |> Contract.fails 1064 "42000"
               Contract.execute "missing" "ALTER PROCEDURE missing_p COMMENT 'x'" |> Contract.fails 1305 "42000"
               Contract.execute "empty" "ALTER PROCEDURE alter_p"
               Contract.execute "change-mode" "SET sql_mode='ANSI_QUOTES'"
               Contract.execute "preserve-mode" "ALTER PROCEDURE alter_p COMMENT 'mode'"
               Contract.query "creation-mode"
                   "SELECT SQL_MODE FROM information_schema.routines WHERE ROUTINE_NAME='alter_p'"
               Contract.query "alter-time"
                   "SELECT LAST_ALTERED>=CREATED AS valid_time FROM information_schema.routines WHERE ROUTINE_NAME='alter_p'" |]
          Cleanup = [| "SET sql_mode=DEFAULT"; "DROP PROCEDURE alter_p"; "DROP FUNCTION alter_f"; "DROP USER 'routine_owner'@'%'" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private routineTimeoutHints =
        { Name = "routine-timeout-hints"
          Setup = [||]
          Steps =
            [| Contract.execute "create-procedure"
                   "CREATE PROCEDURE lifetime_p() BEGIN IF 0 THEN SELECT /*+ MAX_EXECUTION_TIME(1) */ 2; END IF; SELECT 1 AS n; END"
               Contract.query "declaration-warning" "SHOW WARNINGS"
               for label in [ "first"; "second" ] do
                   Contract.query (label + "-call") "CALL lifetime_p()"
                   Contract.query (label + "-warnings") "SHOW WARNINGS"
               Contract.execute "unrelated-table" "CREATE TABLE lifetime_unrelated(n INT)"
               Contract.query "after-table" "CALL lifetime_p()"
               Contract.query "after-table-warnings" "SHOW WARNINGS"
               Contract.execute "create-other-routine" "CREATE PROCEDURE lifetime_other() SELECT 7"
               Contract.query "after-create" "CALL lifetime_p()"
               Contract.query "after-create-warnings" "SHOW WARNINGS"
               Contract.execute "drop-other-routine" "DROP PROCEDURE lifetime_other"
               Contract.query "after-drop" "CALL lifetime_p()"
               Contract.query "after-drop-warnings" "SHOW WARNINGS"
               Contract.execute "alter" "ALTER PROCEDURE lifetime_p COMMENT 'changed'"
               Contract.query "after-alter" "CALL lifetime_p()"
               Contract.query "after-alter-warnings" "SHOW WARNINGS"
               Contract.query "observer-call" "CALL lifetime_p()" |> Contract.on "routine-observer"
               Contract.query "observer-warnings" "SHOW WARNINGS" |> Contract.on "routine-observer"
               Contract.query "observer-repeat" "CALL lifetime_p()" |> Contract.on "routine-observer"
               Contract.query "observer-repeat-warnings" "SHOW WARNINGS" |> Contract.on "routine-observer"
               Contract.execute "create-function"
                   "CREATE FUNCTION lifetime_f() RETURNS INT DETERMINISTIC RETURN (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1)"
               Contract.query "function-declaration-warnings" "SHOW WARNINGS"
               for label in [ "first"; "second" ] do
                   Contract.query (label + "-function") "SELECT lifetime_f() AS n"
                   Contract.query (label + "-function-warnings") "SHOW WARNINGS"
               Contract.execute "create-dynamic"
                   "CREATE PROCEDURE lifetime_dynamic() BEGIN PREPARE s FROM 'SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n'; SHOW WARNINGS; EXECUTE s; END"
               Contract.query "dynamic-declaration-warnings" "SHOW WARNINGS"
               for label in [ "first"; "second" ] do
                   Contract.query (label + "-dynamic") "CALL lifetime_dynamic()"
                   Contract.query (label + "-dynamic-warnings") "SHOW WARNINGS" |]
          Cleanup =
            [| "DROP PROCEDURE lifetime_p"; "DROP PROCEDURE lifetime_dynamic"
               "DROP FUNCTION lifetime_f"; "DROP TABLE lifetime_unrelated" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private selectTimeoutHints =
        { Name = "select-timeout-hints"
          Setup = [| "CREATE TABLE hint_t(n INT)"; "INSERT INTO hint_t VALUES(1),(2)" |]
          Steps =
            [| for index, sql in
                   [
                      "(SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n)"
                      "WITH c AS (SELECT 1 AS n) SELECT /*+ MAX_EXECUTION_TIME(10000) */ n FROM c"
                      "SELECT 1 AS n UNION ALL SELECT /*+ MAX_EXECUTION_TIME(10000) */ 2"
                      "SELECT /*+ MAX_EXECUTION_TIME(10000) */ 1 AS n UNION ALL SELECT /*+ MAX_EXECUTION_TIME(2) */ 2"
                      "SELECT /*+ MAX_EXECUTION_TIME(0) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(01) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(-1) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(+1) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(1.5) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME('1') */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(NULL) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME() */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(1,2) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(4294967295) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(4294967296) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(0) MAX_EXECUTION_TIME(2) */ 1 AS n"
                      "SELECT /*+ MAX_EXECUTION_TIME(1.5) MAX_EXECUTION_TIME(2) */ 1 AS n"
                      "SELECT (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS n"
                      "SELECT 1 /*+ MAX_EXECUTION_TIME(1) */ AS n"
                   ] |> List.indexed do
                   Contract.query (sprintf "query-%d" index) sql
                   Contract.query (sprintf "warnings-%d" index) "SHOW WARNINGS"
               for index, sql in
                   [ "CREATE VIEW hint_view AS SELECT /*+ MAX_EXECUTION_TIME(1) */ n FROM hint_t"
                     "ALTER VIEW hint_view AS SELECT /*+ MAX_EXECUTION_TIME(1) */ n FROM hint_t"
                     "CREATE OR REPLACE VIEW hint_view AS SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ n FROM hint_t"
                     "CREATE OR REPLACE VIEW hint_view AS SELECT /*+ MAX_EXECUTION_TIME(-1) */ n FROM hint_t" ] |> List.indexed do
                   Contract.execute (sprintf "view-%d" index) sql
                   Contract.query (sprintf "view-warnings-%d" index) "SHOW WARNINGS"
               Contract.query "view-rows" "SELECT n FROM hint_view ORDER BY n"
               Contract.execute "update-hint" "UPDATE /*+ MAX_EXECUTION_TIME(1) */ hint_t SET n=n"
               Contract.query "update-warning" "SHOW WARNINGS"
               Contract.prepare "prepare-hints" "hint-query" Query
                   "SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) */ 1 AS n" [||]
               Contract.query "prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "execute-hints" "hint-query" OracleSuccess
               Contract.query "execute-warnings" "SHOW WARNINGS"
               Contract.close "close-hints" "hint-query"
               Contract.prepare "prepare-deadline" "hint-deadline" Query
                   "SELECT /*+ MAX_EXECUTION_TIME(1) */ n,SLEEP(0.1) FROM hint_t" [||]
               Contract.invoke "hint-deadline" "hint-deadline" (OracleError(3024, "HY000"))
               Contract.close "close-deadline" "hint-deadline"
               Contract.execute "session-deadline" "SET max_execution_time=1"
               Contract.query "override-deadline" "SELECT /*+ MAX_EXECUTION_TIME(10000) */ n,SLEEP(0.01) AS slept FROM hint_t ORDER BY n"
               Contract.query "fallback-deadline" "SELECT /*+ MAX_EXECUTION_TIME(0) */ n,SLEEP(0.1) FROM hint_t" |> Contract.fails 3024 "HY000" |]
          Cleanup = [| "SET max_execution_time=0"; "DROP VIEW hint_view"; "DROP TABLE hint_t" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private tableHintResolution =
        { Name = "tableHintResolution"
          Setup = [| "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))" |]
          Steps =
            [|
               Contract.query "case-1-1" "SELECT /*+ BKA(t) */ 1 AS n"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "SELECT /*+ QB_NAME(q) BKA(t) */ 1 AS n"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "SELECT /*+ BKA(t@q) */ 1 AS n"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "SELECT /*+ BKA(@q t) */ 1 AS n"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "SELECT /*+ QB_NAME(q) BKA(t@q) */ 1 AS n"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "SELECT /*+ BKA(z,t) NO_INDEX(z) */ 1 AS n FROM t"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "SELECT /*+ BKA(t) */ 1 AS n FROM t AS a"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "SELECT /*+ BKA(a) */ 1 AS n FROM t AS a"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-9-1" "SELECT /*+ BKA(A) */ 1 AS n FROM t AS a"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "SELECT /*+ BKA(a) NO_BKA(a) */ 1 AS n FROM t AS a"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "SELECT /*+ BKA(t) BKA(t) */ 1 AS n"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.query "case-12-1" "SELECT /*+ BKA(t) NO_BKA(t) */ 1 AS n"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-13-1" "SELECT /*+ NO_INDEX(t absent) */ 1 AS n FROM t"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "SELECT /*+ BKA(a) */ 1 AS n FROM (SELECT 1) a"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "SELECT (SELECT /*+ BKA(t) */ 1) AS n"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "SELECT /*+ BKA(t) */ 1 AS n UNION ALL SELECT /*+ BKA(t) */ 2"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.query "case-17-1" "SELECT /*+ BKA(t) */ 1 AS n FROM absent" |> Contract.fails 1146 "42S02"
               Contract.query "case-18-1" "SELECT /*+ BKA(t) BOGUS */ 1 AS n"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.query "case-19-1" "SELECT /*+ BKA(t) SET_VAR(max_points_in_geometry=2) MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.execute "case-20-1" "PREPARE s FROM 'SELECT /*+ BKA(t) */ 1 AS n'"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.query "case-20-3" "EXECUTE s"
               Contract.query "case-20-4" "SHOW WARNINGS"
               Contract.query "case-21-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT /*+ BKA(y) */ * FROM c"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.query "case-22-1" "SELECT /*+ BKA(x) */ (SELECT /*+ BKA(y) */ 1) AS n FROM (SELECT /*+ BKA(z) */ 1) a"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.query "case-23-1" "SELECT /*+ BKA(t@q) */ 1 AS n FROM (SELECT /*+ QB_NAME(q) */ 1 FROM t) a"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.query "case-24-1" "SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t) */ 1 AS n"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.query "case-25-1" "SELECT /*+ QB_NAME(q) */ (SELECT /*+ QB_NAME(q) BKA(t) */ 1) AS n"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.query "case-26-1" "SELECT /*+ BKA(@q) */ 1 AS n"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.query "case-27-1" "SELECT /*+ BKA(z,a) BKA(y,a) */ 1 AS n FROM t a"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.query "case-28-1" "SELECT /*+ BKA(z) NO_INDEX(z) BKA(y) */ 1 AS n"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.query "case-29-1" "SELECT /*+ NO_INDEX(t absent,missing) */ 1 AS n FROM t"
               Contract.query "case-29-2" "SHOW WARNINGS"
               Contract.query "case-30-1" "SELECT /*+ NO_INDEX(z absent) */ 1 AS n FROM t"
               Contract.query "case-30-2" "SHOW WARNINGS"
               Contract.query "case-31-1" "SELECT /*+ BKA(t@`select#1`) */ 1 AS n"
               Contract.query "case-31-2" "SHOW WARNINGS"
               Contract.query "case-32-1" "SELECT /*+ BKA(t@q,t@q) */ 1 AS n"
               Contract.query "case-32-2" "SHOW WARNINGS"
               Contract.query "case-33-1" "SELECT /*+ QB_NAME(Q) BKA(t@q) */ 1 AS n"
               Contract.query "case-33-2" "SHOW WARNINGS"
               Contract.query "case-34-1" "SELECT /*+ BKA(t) */ 1 AS n FROM t"
               Contract.query "case-34-2" "SHOW WARNINGS"
               Contract.query "case-35-1" "SELECT /*+ BKA(t) */ 1 AS n FROM t AS t"
               Contract.query "case-35-2" "SHOW WARNINGS"
               Contract.execute "case-36-1" "CREATE VIEW v AS SELECT /*+ BKA(t) */ 1 AS n"
               Contract.query "case-36-2" "SHOW WARNINGS"
               Contract.query "case-37-1" "SELECT (WITH c AS (SELECT /*+ BKA(c_missing) */ 1 AS n) SELECT /*+ BKA(q_missing) */ n FROM c) AS n"
               Contract.query "case-37-2" "SHOW WARNINGS"
               Contract.query "case-38-1" "SELECT * FROM (WITH c AS (SELECT /*+ BKA(c_missing) */ 1 AS n) SELECT /*+ BKA(q_missing) */ n FROM c) a"
               Contract.query "case-38-2" "SHOW WARNINGS"
               Contract.query "case-39-1" "SELECT /*+ NO_INDEX(t i1) NO_INDEX(t i2) */ 1 AS n FROM t"
               Contract.query "case-39-2" "SHOW WARNINGS"
               Contract.query "case-40-1" "SELECT /*+ INDEX(t i1) NO_INDEX(t i2) */ 1 AS n FROM t"
               Contract.query "case-40-2" "SHOW WARNINGS"
               Contract.query "case-41-1" "SELECT /*+ NO_INDEX(t i1) NO_INDEX(t i1) */ 1 AS n FROM t"
               Contract.query "case-41-2" "SHOW WARNINGS"
               Contract.query "case-42-1" "SELECT /*+ NO_INDEX(t i1,i2) NO_INDEX(t i2) */ 1 AS n FROM t"
               Contract.query "case-42-2" "SHOW WARNINGS"
               Contract.query "case-43-1" "SELECT /*+ NO_INDEX(t) INDEX(t i1) */ 1 AS n FROM t"
               Contract.query "case-43-2" "SHOW WARNINGS"
               Contract.query "case-44-1" "SELECT /*+ BKA() BKA() */ 1 AS n"
               Contract.query "case-44-2" "SHOW WARNINGS"
               Contract.query "case-45-1" "SELECT /*+ BKA() NO_BKA() */ 1 AS n"
               Contract.query "case-45-2" "SHOW WARNINGS"
               Contract.query "case-46-1" "SELECT /*+ MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) QB_NAME(q) QB_NAME(r) */ 1 AS n"
               Contract.query "case-46-2" "SHOW WARNINGS"
               Contract.query "case-47-1" "SELECT /*+ QB_NAME(q) QB_NAME(r) MAX_EXECUTION_TIME(1) MAX_EXECUTION_TIME(2) */ 1 AS n"
               Contract.query "case-47-2" "SHOW WARNINGS"
               Contract.query "case-48-1" "/*!80000 */ SELECT /*+ BKA(x) */ 1 AS n"
               Contract.query "case-48-2" "SHOW WARNINGS"
               Contract.query "case-49-1" "SELECT /*+ BKA(t) */ 1 AS n FROM t"
               Contract.query "case-49-2" "SHOW WARNINGS"
               Contract.query "case-49-3" "SELECT 1"
               Contract.query "case-49-4" "SHOW WARNINGS"
               Contract.query "case-50-1" "SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t@r) */ 1 AS n"
               Contract.query "case-50-2" "SHOW WARNINGS"
               Contract.query "case-51-1" "SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(t@q) */ 1 AS n"
               Contract.query "case-51-2" "SHOW WARNINGS"
               Contract.query "case-52-1" "SELECT /*+ QB_NAME(q) */ (SELECT /*+ QB_NAME(q) BKA(t@q) */ 1) AS n FROM t"
               Contract.query "case-52-2" "SHOW WARNINGS"
               Contract.prepare "binary-prepare" "table-hint" Query "SELECT /*+ BKA(t) */ 1 AS n" [||]
               Contract.query "binary-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute" "table-hint" OracleSuccess
               Contract.query "binary-execute-warnings" "SHOW WARNINGS"
               Contract.close "binary-close" "table-hint"
            |]
          Cleanup = [| "DEALLOCATE PREPARE s"; "DROP VIEW v"; "DROP TABLE t" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private mixedOptimizerHints =
        { Name = "mixedOptimizerHints"
          Setup = [|  |]
          Steps =
            [|
               Contract.query "case-1-1" "SELECT /*+ BOGUS */ 1 AS n"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "SELECT /*+ BOGUS MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "SELECT /*+ BOGUS() MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) BOGUS */ 1 AS n"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "SELECT /*+ MAX_EXECUTION_TIME(10000), MAX_EXECUTION_TIME(2) */ 1 AS n"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "SELECT /*+ MAX_EXECUTION_TIME(10000)MAX_EXECUTION_TIME(2) */ 1 AS n"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "SELECT /*+ BKA(t) MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "SELECT /*+ BKA() MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-9-1" "SELECT /*+ BKA MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "SELECT /*+ QB_NAME(q) MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "SELECT /*+ QB_NAME() MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.query "case-12-1" "SELECT /*+ SET_VAR(max_points_in_geometry=3) MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-13-1" "SELECT /*+ BOGUS SET_VAR(max_points_in_geometry=3) */ 1 AS n"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "SELECT /*+ SET_VAR(max_points_in_geometry=3) BOGUS */ 1 AS n"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=3) */ 1 AS n"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "SELECT /*+ max_execution_time(10000) bogus */ 1 AS n"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.query "case-17-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) 123 */ 1 AS n"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.query "case-18-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) /* x */ */ 1 AS n" |> Contract.fails 1064 "42000"
               Contract.query "case-19-1" "SELECT /*+ JOIN_FIXED_ORDER() MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.query "case-20-1" "SELECT /*+ NO_INDEX(t) MAX_EXECUTION_TIME(10000) */ 1 AS n"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.query "case-21-1" "SELECT /*+ MAX_EXECUTION_TIME(1.5) BOGUS */ 1 AS n"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.query "case-22-1" "SELECT /*+ MAX_EXECUTION_TIME(4294967296) BOGUS */ 1 AS n"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.query "case-23-1" "SELECT /*+ BOGUS SET_VAR(max_points_in_geometry=3) */ @@max_points_in_geometry AS n"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.query "case-24-1" "SELECT /*+ SET_VAR(max_points_in_geometry=3) BOGUS */ @@max_points_in_geometry AS n"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.query "case-25-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) BOGUS */ 1 AS n"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.query "case-26-1" "SELECT /*+ SET_VAR(max_points_in_geometry=2) BOGUS */ 1 AS n"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.query "case-27-1" "SELECT /*+ BKA(t) BOGUS */ 1 AS n"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.query "case-28-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) SET_VAR(max_points_in_geometry=2) MAX_EXECUTION_TIME(3) */ 1 AS n"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.query "case-29-1" "SELECT /*+ SET_VAR(max_points_in_geometry=3) MAX_EXECUTION_TIME(-1) SET_VAR(no_such_variable=1) */ 1 AS n"
               Contract.query "case-29-2" "SHOW WARNINGS"
            |]
          Cleanup = [|  |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private hintFamilyConflicts =
        { Name = "hint-family-conflicts"
          Setup = [| "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))" |]
          Steps =
            [|
               Contract.query "case-1-1" "SELECT /*+ BKA(x) NO_BKA(x) */ 1 AS n"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "SELECT /*+ BNL(x) NO_BNL(x) */ 1 AS n"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "SELECT /*+ HASH_JOIN(x) NO_HASH_JOIN(x) */ 1 AS n"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "SELECT /*+ MERGE(x) NO_MERGE(x) */ 1 AS n"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "SELECT /*+ DERIVED_CONDITION_PUSHDOWN(x) NO_DERIVED_CONDITION_PUSHDOWN(x) */ 1 AS n"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "SELECT /*+ MRR(x) NO_MRR(x) */ 1 AS n"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "SELECT /*+ INDEX_MERGE(x) NO_INDEX_MERGE(x) */ 1 AS n"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "SELECT /*+ SKIP_SCAN(x) NO_SKIP_SCAN(x) */ 1 AS n"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-9-1" "SELECT /*+ INDEX(x) NO_INDEX(x) */ 1 AS n"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "SELECT /*+ JOIN_INDEX(x) NO_JOIN_INDEX(x) */ 1 AS n"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "SELECT /*+ GROUP_INDEX(x) NO_GROUP_INDEX(x) */ 1 AS n"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.query "case-12-1" "SELECT /*+ ORDER_INDEX(x) NO_ORDER_INDEX(x) */ 1 AS n"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-13-1" "SELECT /*+ JOIN_PREFIX(a,b) JOIN_PREFIX(b,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "SELECT /*+ JOIN_SUFFIX(a,b) JOIN_SUFFIX(b,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "SELECT /*+ JOIN_ORDER(a,b) JOIN_ORDER(b,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "SELECT /*+ JOIN_PREFIX(a,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.query "case-17-1" "SELECT /*+ JOIN_ORDER(a,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.query "case-18-1" "SELECT /*+ JOIN_PREFIX(x) JOIN_PREFIX(y) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.query "case-19-1" "SELECT /*+ JOIN_ORDER(x) JOIN_ORDER(y) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.query "case-20-1" "SELECT /*+ JOIN_SUFFIX(x) JOIN_SUFFIX(y) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.query "case-21-1" "SELECT /*+ JOIN_FIXED_ORDER() JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.query "case-22-1" "SELECT /*+ JOIN_FIXED_ORDER() JOIN_PREFIX(a,b) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.query "case-23-1" "SELECT /*+ JOIN_PREFIX(a,b) JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.query "case-24-1" "SELECT /*+ JOIN_PREFIX(a) JOIN_SUFFIX(a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.query "case-25-1" "SELECT /*+ JOIN_ORDER(a,b) JOIN_PREFIX(b) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.query "case-26-1" "SELECT /*+ JOIN_FIXED_ORDER(@missing) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.query "case-27-1" "SELECT /*+ SEMIJOIN(@missing FIRSTMATCH) */ 1 AS n FROM t"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.query "case-28-1" "SELECT /*+ SUBQUERY(@missing MATERIALIZATION) */ 1 AS n FROM t"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.query "case-29-1" "SELECT /*+ INDEX(t i1) JOIN_INDEX(t i2) */ 1 AS n FROM t"
               Contract.query "case-29-2" "SHOW WARNINGS"
               Contract.query "case-30-1" "SELECT /*+ JOIN_INDEX(t i1) GROUP_INDEX(t i2) */ 1 AS n FROM t"
               Contract.query "case-30-2" "SHOW WARNINGS"
               Contract.query "case-31-1" "SELECT /*+ INDEX_MERGE(t i1) INDEX_MERGE(t i2) */ 1 AS n FROM t"
               Contract.query "case-31-2" "SHOW WARNINGS"
               Contract.query "case-32-1" "SELECT /*+ SKIP_SCAN(t i1) SKIP_SCAN(t i2) */ 1 AS n FROM t"
               Contract.query "case-32-2" "SHOW WARNINGS"
               Contract.query "case-33-1" "SELECT /*+ MRR(t i1) MRR(t i2) */ 1 AS n FROM t"
               Contract.query "case-33-2" "SHOW WARNINGS"
               Contract.query "case-34-1" "SELECT /*+ NO_ICP(t i1) NO_ICP(t i2) */ 1 AS n FROM t"
               Contract.query "case-34-2" "SHOW WARNINGS"
               Contract.query "case-35-1" "SELECT /*+ NO_RANGE_OPTIMIZATION(t i1) NO_RANGE_OPTIMIZATION(t i2) */ 1 AS n FROM t"
               Contract.query "case-35-2" "SHOW WARNINGS"
               Contract.query "case-36-1" "SELECT /*+ BKA(@missing x,y) */ 1 AS n FROM t"
               Contract.query "case-36-2" "SHOW WARNINGS"
               Contract.query "case-37-1" "SELECT /*+ QB_NAME(q) BKA(x@q) BKA(x@q) */ 1 AS n FROM t"
               Contract.query "case-37-2" "SHOW WARNINGS"
               Contract.query "case-38-1" "SELECT /*+ INDEX_MERGE(t i1) NO_INDEX_MERGE(t i2) */ 1 AS n FROM t"
               Contract.query "case-38-2" "SHOW WARNINGS"
               Contract.query "case-39-1" "SELECT /*+ INDEX_MERGE(t i1,i2) INDEX_MERGE(t i1,i2) */ 1 AS n FROM t"
               Contract.query "case-39-2" "SHOW WARNINGS"
               Contract.query "case-40-1" "SELECT /*+ MRR(t i1) NO_MRR(t i1) */ 1 AS n FROM t"
               Contract.query "case-40-2" "SHOW WARNINGS"
               Contract.query "case-41-1" "SELECT /*+ MRR(t i1,i2) NO_MRR(t i2) */ 1 AS n FROM t"
               Contract.query "case-41-2" "SHOW WARNINGS"
               Contract.query "case-42-1" "SELECT /*+ MRR(t i1) MRR(t i1,i2) */ 1 AS n FROM t"
               Contract.query "case-42-2" "SHOW WARNINGS"
               Contract.query "case-43-1" "SELECT /*+ MRR(x i1,i2) MRR(x i2,i3) */ 1 AS n FROM t"
               Contract.query "case-43-2" "SHOW WARNINGS"
               Contract.query "case-44-1" "SELECT /*+ MRR(t) MRR(t i1) */ 1 AS n FROM t"
               Contract.query "case-44-2" "SHOW WARNINGS"
               Contract.query "case-45-1" "SELECT /*+ NO_MRR(t) MRR(t i1) */ 1 AS n FROM t"
               Contract.query "case-45-2" "SHOW WARNINGS"
               Contract.query "case-46-1" "SELECT /*+ MRR(t i1) MRR(t) */ 1 AS n FROM t"
               Contract.query "case-46-2" "SHOW WARNINGS"
               Contract.query "case-47-1" "SELECT /*+ JOIN_ORDER() JOIN_ORDER() */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-47-2" "SHOW WARNINGS"
               Contract.query "case-48-1" "SELECT /*+ JOIN_ORDER(x,y) JOIN_ORDER(x,z) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-48-2" "SHOW WARNINGS"
               Contract.query "case-49-1" "SELECT /*+ JOIN_PREFIX(x,x) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-49-2" "SHOW WARNINGS"
               Contract.query "case-50-1" "SELECT /*+ QB_NAME(q) JOIN_PREFIX(@q a,b) JOIN_PREFIX(@q b,a) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-50-2" "SHOW WARNINGS"
               Contract.query "case-51-1" "SELECT /*+ QB_NAME(q) JOIN_PREFIX(x@q) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-51-2" "SHOW WARNINGS"
               Contract.query "case-52-1" "SELECT /*+ JOIN_PREFIX(x@missing) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-52-2" "SHOW WARNINGS"
               Contract.query "case-53-1" "SELECT /*+ JOIN_PREFIX(@missing x) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-53-2" "SHOW WARNINGS"
               Contract.query "case-54-1" "SELECT /*+ JOIN_FIXED_ORDER(@q) QB_NAME(q) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-54-2" "SHOW WARNINGS"
               Contract.query "case-55-1" "SELECT /*+ SEMIJOIN(FIRSTMATCH) SUBQUERY(MATERIALIZATION) */ 1 AS n FROM t"
               Contract.query "case-55-2" "SHOW WARNINGS"
               Contract.query "case-56-1" "SELECT /*+ SUBQUERY(MATERIALIZATION) SEMIJOIN(FIRSTMATCH) */ 1 AS n FROM t"
               Contract.query "case-56-2" "SHOW WARNINGS"
               Contract.query "case-57-1" "SELECT /*+ SEMIJOIN(FIRSTMATCH) SEMIJOIN(LOOSESCAN) */ 1 AS n FROM t"
               Contract.query "case-57-2" "SHOW WARNINGS"
               Contract.query "case-58-1" "SELECT /*+ NO_SEMIJOIN(MATERIALIZATION) SEMIJOIN(FIRSTMATCH) */ 1 AS n FROM t"
               Contract.query "case-58-2" "SHOW WARNINGS"
               Contract.query "case-59-1" "SELECT /*+ SUBQUERY(MATERIALIZATION) SUBQUERY(INTOEXISTS) */ 1 AS n FROM t"
               Contract.query "case-59-2" "SHOW WARNINGS"
               Contract.query "case-60-1" "SELECT /*+ JOIN_ORDER(a,b) JOIN_FIXED_ORDER() */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-60-2" "SHOW WARNINGS"
               Contract.query "case-61-1" "SELECT /*+ JOIN_FIXED_ORDER() JOIN_ORDER(a,b) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-61-2" "SHOW WARNINGS"
               Contract.query "case-62-1" "SELECT /*+ BKA(t@q) QB_NAME(q) */ 1 AS n FROM t"
               Contract.query "case-62-2" "SHOW WARNINGS"
               Contract.query "case-63-1" "SELECT /*+ BKA(@q t) QB_NAME(q) */ 1 AS n FROM t"
               Contract.query "case-63-2" "SHOW WARNINGS"
               Contract.query "case-64-1" "SELECT /*+ SEMIJOIN(@q FIRSTMATCH) QB_NAME(q) */ 1 AS n FROM t"
               Contract.query "case-64-2" "SHOW WARNINGS"
               Contract.query "case-65-1" "SELECT /*+ QB_NAME(q) JOIN_PREFIX(@q x) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-65-2" "SHOW WARNINGS"
               Contract.query "case-66-1" "SELECT /*+ QB_NAME(q) JOIN_PREFIX(a@q) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-66-2" "SHOW WARNINGS"
               Contract.query "case-67-1" "SELECT /*+ MRR(x i1) NO_MRR(x i2) */ 1 AS n FROM t"
               Contract.query "case-67-2" "SHOW WARNINGS"
               Contract.query "case-68-1" "SELECT /*+ NO_MRR(x i1) MRR(x i2) */ 1 AS n FROM t"
               Contract.query "case-68-2" "SHOW WARNINGS"
               Contract.query "case-69-1" "SELECT /*+ MRR(t i1,i1) */ 1 AS n FROM t"
               Contract.query "case-69-2" "SHOW WARNINGS"
               Contract.query "case-70-1" "SELECT /*+ NO_ICP(t i1) NO_ICP(t i1) */ 1 AS n FROM t"
               Contract.query "case-70-2" "SHOW WARNINGS"
               Contract.query "case-71-1" "SELECT /*+ NO_RANGE_OPTIMIZATION(t i1) NO_RANGE_OPTIMIZATION(t i1) */ 1 AS n FROM t"
               Contract.query "case-71-2" "SHOW WARNINGS"
               Contract.query "case-72-1" "SELECT /*+ INDEX_MERGE(t missing) BKA(x) */ 1 AS n FROM t"
               Contract.query "case-72-2" "SHOW WARNINGS"
               Contract.query "case-73-1" "SELECT /*+ JOIN_PREFIX(@q a) QB_NAME(q) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-73-2" "SHOW WARNINGS"
               Contract.query "case-74-1" "SELECT /*+ QB_NAME(q) SEMIJOIN(@q FIRSTMATCH) SEMIJOIN(@q LOOSESCAN) */ 1 AS n FROM t"
               Contract.query "case-74-2" "SHOW WARNINGS"
               Contract.query "case-75-1" "SELECT /*+ QB_NAME(q) SUBQUERY(@q MATERIALIZATION) SUBQUERY(@q INTOEXISTS) */ 1 AS n FROM t"
               Contract.query "case-75-2" "SHOW WARNINGS"
               Contract.query "case-76-1" "SELECT /*+ SEMIJOIN() SEMIJOIN() */ 1 AS n FROM t"
               Contract.query "case-76-2" "SHOW WARNINGS"
               Contract.query "case-77-1" "SELECT /*+ SEMIJOIN(FIRSTMATCH,MATERIALIZATION) SEMIJOIN(DUPSWEEDOUT,LOOSESCAN) */ 1 AS n FROM t"
               Contract.query "case-77-2" "SHOW WARNINGS"
               Contract.query "case-78-1" "SELECT /*+ INDEX_MERGE(t i1,i1) */ 1 AS n FROM t"
               Contract.query "case-78-2" "SHOW WARNINGS"
               Contract.query "case-79-1" "SELECT /*+ MRR(x i1) MRR(x) */ 1 AS n FROM t"
               Contract.query "case-79-2" "SHOW WARNINGS"
               Contract.query "case-80-1" "SELECT /*+ MRR(x i1) NO_MRR(x) */ 1 AS n FROM t"
               Contract.query "case-80-2" "SHOW WARNINGS"
               Contract.query "case-81-1" "SELECT /*+ BKA(x) JOIN_PREFIX(y) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-81-2" "SHOW WARNINGS"
               Contract.query "case-82-1" "SELECT /*+ JOIN_PREFIX(y) BKA(x) */ 1 AS n FROM t a JOIN t b"
               Contract.query "case-82-2" "SHOW WARNINGS"
               Contract.query "case-83-1" "SELECT /*+ MRR(x i1) BKA(x) */ 1 AS n FROM t"
               Contract.query "case-83-2" "SHOW WARNINGS"
               Contract.query "case-84-1" "SELECT /*+ MRR(x i1) BKA(y) */ 1 AS n FROM t"
               Contract.query "case-84-2" "SHOW WARNINGS"
               Contract.query "case-85-1" "SELECT /*+ MRR(x i1) NO_INDEX(x missing) */ 1 AS n FROM t"
               Contract.query "case-85-2" "SHOW WARNINGS"
               Contract.query "case-86-1" "SELECT /*+ NO_INDEX(x missing) BKA(x) */ 1 AS n FROM t"
               Contract.query "case-86-2" "SHOW WARNINGS"
               Contract.query "case-87-1" "SELECT /*+ NO_INDEX(y missing) BKA(x) */ 1 AS n FROM t"
               Contract.query "case-87-2" "SHOW WARNINGS"
               Contract.query "case-88-1" "SELECT /*+ NO_MRR(x) BKA(x) NO_BNL(x) */ 1 AS n FROM t"
               Contract.query "case-88-2" "SHOW WARNINGS"
               Contract.query "case-89-1" "SELECT /*+ INDEX_MERGE(@missing t i1) */ 1 AS n FROM t"
               Contract.query "case-89-2" "SHOW WARNINGS"
               Contract.query "case-90-1" "SELECT /*+ INDEX_MERGE(t@q i1) QB_NAME(q) */ 1 AS n FROM t"
               Contract.query "case-90-2" "SHOW WARNINGS"
               Contract.query "case-91-1" "SELECT /*+ QB_NAME(q) INDEX_MERGE(t@q i1) */ 1 AS n FROM t"
               Contract.query "case-91-2" "SHOW WARNINGS"
               Contract.query "case-92-1" "SELECT /*+ BKA() BKA(x) */ 1 AS n FROM t"
               Contract.query "case-92-2" "SHOW WARNINGS"
               Contract.query "case-93-1" "SELECT /*+ NO_BKA() BKA(x) */ 1 AS n FROM t"
               Contract.query "case-93-2" "SHOW WARNINGS"
               Contract.query "case-94-1" "SELECT /*+ BKA(x) NO_BKA() */ 1 AS n FROM t"
               Contract.query "case-94-2" "SHOW WARNINGS"
               Contract.query "case-95-1" "SELECT /*+ QB_NAME(q) BKA(x) */ (SELECT /*+ QB_NAME(q) BKA(y) */ 1) AS n"
               Contract.query "case-95-2" "SHOW WARNINGS"
               Contract.query "case-96-1" "SELECT /*+ QB_NAME(outerq) */ (SELECT /*+ BKA(t@outerq) */ 1) AS n FROM t"
               Contract.query "case-96-2" "SHOW WARNINGS"
               Contract.query "case-97-1" "SELECT (SELECT /*+ QB_NAME(q) BKA(x) */ 1) AS n FROM (SELECT /*+ QB_NAME(q) BKA(y) */ 1) a"
               Contract.query "case-97-2" "SHOW WARNINGS"
               Contract.query "case-98-1" "SELECT /*+ BKA(t@q) */ 1 AS n FROM (SELECT /*+ QB_NAME(q) */ 1 FROM t) a"
               Contract.query "case-98-2" "SHOW WARNINGS"
               Contract.query "case-99-1" "SELECT /*+ QB_NAME(rootq) QB_NAME(root_rejected) */ (SELECT /*+ QB_NAME(childq) QB_NAME(child_rejected) */ 1) AS n"
               Contract.query "case-99-2" "SHOW WARNINGS"
               Contract.query "case-100-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) */ (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1) AS n"
               Contract.query "case-100-2" "SHOW WARNINGS"
               Contract.query "case-101-1" "SELECT /*+ QB_NAME(q) QB_NAME(r) */ (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS n"
               Contract.query "case-101-2" "SHOW WARNINGS"
               Contract.query "case-102-1" "SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(x) */ 2 AS n UNION ALL SELECT /*+ BKA(x) */ 3 AS n UNION ALL SELECT /*+ BKA(x) */ 4 AS n UNION ALL SELECT /*+ BKA(x) */ 5 AS n UNION ALL SELECT /*+ BKA(x) */ 6 AS n UNION ALL SELECT /*+ BKA(x) */ 7 AS n UNION ALL SELECT /*+ BKA(x) */ 8 AS n UNION ALL SELECT /*+ BKA(x) */ 9 AS n UNION ALL SELECT /*+ BKA(x) */ 10 AS n UNION ALL SELECT /*+ BKA(x) */ 11 AS n UNION ALL SELECT /*+ BKA(x) */ 12 AS n"
               Contract.query "case-102-2" "SHOW WARNINGS"
               Contract.query "case-103-1" "WITH c AS (SELECT /*+ QB_NAME(q) BKA(y) */ 1 AS n) SELECT /*+ QB_NAME(q) BKA(x) */ n FROM c"
               Contract.query "case-103-2" "SHOW WARNINGS"
               Contract.query "case-104-1" "SELECT (SELECT /*+ BKA(t@q) */ 1) AS n FROM (SELECT /*+ QB_NAME(q) */ 1 AS n FROM t) a"
               Contract.query "case-104-2" "SHOW WARNINGS"
               Contract.query "case-105-1" "SELECT /*+ QB_NAME(q) BKA(@q t) BKA(@q t) */ 1 AS n FROM t"
               Contract.query "case-105-2" "SHOW WARNINGS"
               Contract.query "case-106-1" "SELECT /*+ QB_NAME(q) BKA() BKA(@q x) */ 1 AS n FROM t"
               Contract.query "case-106-2" "SHOW WARNINGS"
               Contract.query "case-107-1" "SELECT /*+ MAX_EXECUTION_TIME(10000) MAX_EXECUTION_TIME(2) */ (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1) AS n"
               Contract.query "case-107-2" "SHOW WARNINGS"
            |]
          Cleanup = [| "DROP TABLE t" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private settingHintContext =
        { Name = "setting-hint-context"
          Setup = [||]
          Steps =
            [|
               Contract.query "case-1-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n, (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry) AS child"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "SELECT /*+ SET_VAR(unknown_outer=1) */ (SELECT /*+ SET_VAR(unknown_inner=1) */ 1) AS n"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS n"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) SET_VAR(max_points_in_geometry=8) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS n"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ 1 AS n UNION ALL SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 2 AS n"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT /*+ SET_VAR(max_points_in_geometry=9) */ n FROM c"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "SELECT /*+ SET_VAR(max_points_in_geometry=2) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS n"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=2) */ 1) AS n"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-9-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) QB_NAME(q) QB_NAME(r) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) QB_NAME(q) QB_NAME(r) */ 1) AS n"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "SELECT /*+ SET_VAR(unknown_outer=1) */ (SELECT /*+ BKA(x) */ 1) AS n"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "SELECT /*+ BKA(x) */ (SELECT /*+ SET_VAR(unknown_inner=1) */ 1) AS n"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.query "case-12-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry='bad') */ 1) AS n"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-13-1" "SELECT /*+ SET_VAR(max_points_in_geometry='bad') SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "SELECT /*+ SET_VAR(max_points_in_geometry=2) SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "SELECT /*+ SET_VAR(unknown=1) SET_VAR(unknown=2) */ 1 AS n"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "PREPARE s FROM 'SELECT /*+ SET_VAR(max_points_in_geometry=2) SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n'"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.query "case-16-3" "EXECUTE s"
               Contract.query "case-16-4" "SHOW WARNINGS"
               Contract.query "case-17-1" "PREPARE s FROM 'SELECT /*+ SET_VAR(unknown=1) */ 1 AS n'"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.query "case-17-3" "EXECUTE s"
               Contract.query "case-17-4" "SHOW WARNINGS"
               Contract.query "case-18-1" "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1) AS n"
               Contract.query "case-18-2" "SELECT @@max_points_in_geometry AS restored"
               Contract.query "case-18-3" "SHOW WARNINGS"
               Contract.prepare "binary-prepare" "setting-hint" Query
                   "SELECT /*+ SET_VAR(max_points_in_geometry=9) */ @@max_points_in_geometry AS n, (SELECT /*+ SET_VAR(max_points_in_geometry=2) */ @@max_points_in_geometry) AS child" [||]
               Contract.query "binary-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute" "setting-hint" OracleSuccess
               Contract.query "binary-execute-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute-again" "setting-hint" OracleSuccess
               Contract.query "binary-execute-again-warnings" "SHOW WARNINGS"
               Contract.close "binary-close" "setting-hint"
               Contract.query "restored-setting" "SELECT @@max_points_in_geometry AS n"
            |]
          Cleanup = [| "DEALLOCATE PREPARE s" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private mutationHintContext =
        { Name = "mutation-hint-context"
          Setup = [||]
          Steps =
            [|
               Contract.execute "case-1-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-1-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-1-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-1-1" "UPDATE /*+ BKA(x) */ t SET id=id"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.execute "case-2-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-2-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-2-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-2-1" "UPDATE /*+ BKA(t) NO_INDEX(t missing) */ t SET id=id"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.execute "case-3-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-3-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-3-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-3-1" "UPDATE /*+ BKA(t) BKA(a) */ t AS a SET id=id"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.execute "case-4-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-4-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-4-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-4-1" "DELETE /*+ BKA(x) */ FROM t WHERE 0"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.execute "case-5-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-5-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-5-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-5-1" "DELETE /*+ QB_NAME(q) BKA(x@q) */ FROM t WHERE 0"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.execute "case-6-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-6-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-6-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-6-1" "INSERT /*+ BKA(x) */ INTO t VALUES(1)"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.execute "case-7-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-7-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-7-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-7-1" "REPLACE /*+ BKA(x) */ INTO t VALUES(1)"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.execute "case-8-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-8-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-8-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-8-1" "INSERT /*+ BKA(x) BKA(t) */ INTO t SELECT /*+ BKA(y) */ 1"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.execute "case-9-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-9-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-9-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-9-1" "REPLACE /*+ BKA(x) BKA(t) */ INTO t SELECT /*+ BKA(y) */ 1"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.execute "case-10-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-10-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-10-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-10-1" "UPDATE /*+ QB_NAME(q) QB_NAME(r) */ t SET id=(SELECT /*+ QB_NAME(q) BKA(x) */ 1)"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.execute "case-11-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-11-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-11-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-11-1" "UPDATE /*+ SET_VAR(max_points_in_geometry=9) */ t SET id=(SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry)"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.execute "case-12-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-12-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-12-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-12-1" "WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) UPDATE /*+ BKA(x) */ t SET id=(SELECT n FROM c)"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.execute "case-13-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-13-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-13-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-13-1" "DELETE /*+ BKA(x) */ FROM t WHERE id IN (SELECT /*+ BKA(y) */ n FROM (SELECT 1 AS n) a)"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.execute "case-14-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-14-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-14-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-14-1" "INSERT /*+ QB_NAME(q) QB_NAME(r) */ INTO t SELECT /*+ QB_NAME(q) BKA(x) */ 1"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.execute "case-15-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-15-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-15-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-15-1" "INSERT /*+ NO_INDEX(t missing) */ INTO t VALUES(1)"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.execute "case-16-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-16-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-16-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-16-1" "UPDATE /*+ BKA(x) NO_BKA(x) */ t SET id=id"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.execute "case-17-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-17-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-17-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-17-1" "DELETE /*+ BKA(x) NO_BKA(x) */ FROM t WHERE 0"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.execute "case-18-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-18-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-18-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-18-1" "INSERT /*+ BKA(x) NO_BKA(x) */ INTO t VALUES(1)"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.execute "case-19-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-19-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-19-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-19-1" "REPLACE /*+ QB_NAME(q) BKA(x@q) */ INTO t VALUES(1)"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.execute "case-20-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-20-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-20-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-20-1" "UPDATE /*+ BKA(a) BKA(t) BKA(b) */ t a JOIN t b ON a.id=b.id SET a.id=a.id"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.execute "case-21-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-21-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-21-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-21-1" "DELETE /*+ BKA(a) BKA(t) BKA(b) */ a FROM t a JOIN t b ON a.id=b.id WHERE 0"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.execute "case-22-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-22-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-22-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-22-1" "INSERT /*+ QB_NAME(q) BKA(x@q) */ INTO t VALUES((SELECT /*+ BKA(y) */ 1))"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.execute "case-23-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-23-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-23-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-23-1" "UPDATE t SET id=(SELECT /*+ BKA(x) */ 1)"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.execute "case-24-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-24-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-24-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-24-1" "WITH c AS (SELECT /*+ QB_NAME(q) BKA(y) */ 1 AS n) UPDATE /*+ QB_NAME(q) BKA(x) */ t SET id=(SELECT /*+ QB_NAME(q) BKA(z) */ n FROM c)"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.execute "case-25-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-25-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-25-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-25-1" "WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) DELETE /*+ BKA(x) */ FROM t WHERE id IN (SELECT /*+ BKA(z) */ n FROM c)"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.execute "case-26-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-26-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-26-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-26-1" "INSERT /*+ BKA(x) */ INTO t SELECT /*+ NO_BKA(x) */ 1"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.execute "case-27-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-27-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-27-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-27-1" "INSERT /*+ BKA(t) */ INTO t SELECT /*+ BKA(t) */ 1"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.execute "case-28-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-28-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-28-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-28-1" "INSERT /*+ BKA(t) BKA(u) */ INTO t SELECT /*+ BKA(t) BKA(u) */ id FROM t u"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.execute "case-29-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-29-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-29-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-29-1" "INSERT /*+ QB_NAME(rootq) BKA(x) */ INTO t SELECT /*+ QB_NAME(childq) BKA(y) */ 1"
               Contract.query "case-29-2" "SHOW WARNINGS"
               Contract.execute "case-30-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-30-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-30-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-30-1" "INSERT /*+ BKA(x) */ INTO t SELECT /*+ BKA(y) */ 1 UNION ALL SELECT /*+ BKA(z) */ 2"
               Contract.query "case-30-2" "SHOW WARNINGS"
               Contract.execute "case-31-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-31-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-31-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-31-1" "INSERT /*+ SET_VAR(max_points_in_geometry=9) */ INTO t SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry"
               Contract.query "case-31-2" "SHOW WARNINGS"
               Contract.query "case-31-3" "SELECT id FROM t ORDER BY id"
               Contract.query "case-31-4" "SHOW WARNINGS"
               Contract.execute "case-32-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-32-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-32-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-32-1" "UPDATE /*+ SET_VAR(max_points_in_geometry=9) */ t SET id=(SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry)"
               Contract.query "case-32-2" "SHOW WARNINGS"
               Contract.query "case-32-3" "SELECT id FROM t"
               Contract.query "case-32-4" "SHOW WARNINGS"
               Contract.execute "case-33-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-33-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-33-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-33-1" "INSERT /*+ BKA(x) */ INTO t VALUES(1) ON DUPLICATE KEY UPDATE id=(SELECT /*+ BKA(y) */ 1)"
               Contract.query "case-33-2" "SHOW WARNINGS"
               Contract.execute "case-34-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-34-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-34-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-34-1" "REPLACE /*+ BKA(x) */ INTO t SET id=(SELECT /*+ BKA(y) */ 1)"
               Contract.query "case-34-2" "SHOW WARNINGS"
               Contract.execute "case-35-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-35-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-35-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-35-1" "PREPARE s FROM 'UPDATE /*+ BKA(x) */ t SET id=id'"
               Contract.query "case-35-2" "SHOW WARNINGS"
               Contract.execute "case-35-3" "EXECUTE s"
               Contract.query "case-35-4" "SHOW WARNINGS"
               Contract.execute "case-36-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-36-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-36-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-36-1" "UPDATE /*+ QB_NAME(rootq) */ t SET id=(SELECT /*+ BKA(t@rootq) */ 1)"
               Contract.query "case-36-2" "SHOW WARNINGS"
               Contract.execute "case-37-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-37-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-37-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-37-1" "WITH c AS (SELECT /*+ BKA(y) */ 1 AS n) UPDATE /*+ BKA(x) */ t SET id=id"
               Contract.query "case-37-2" "SHOW WARNINGS"
               Contract.execute "case-38-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-38-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-38-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-38-1" "UPDATE /*+ BKA(x) */ t JOIN (SELECT /*+ BKA(y) */ 1 AS n) a ON 1 SET id=(SELECT /*+ BKA(z) */ 1)"
               Contract.query "case-38-2" "SHOW WARNINGS"
               Contract.execute "case-39-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-39-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-39-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-39-1" "WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) BKA(y) */ 1 AS n) UPDATE /*+ QB_NAME(q) BKA(x) */ t SET id=id"
               Contract.query "case-39-2" "SHOW WARNINGS"
               Contract.execute "case-40-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-40-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-40-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-40-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) UPDATE /*+ SET_VAR(max_points_in_geometry=9) */ t SET id=@@max_points_in_geometry"
               Contract.query "case-40-2" "SHOW WARNINGS"
               Contract.query "case-40-3" "SELECT id FROM t"
               Contract.query "case-40-4" "SHOW WARNINGS"
               Contract.execute "case-41-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-41-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-41-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-41-1" "INSERT /*+ QB_NAME(q) BKA(x) */ INTO t SELECT /*+ BKA(y) */ 1 UNION ALL SELECT /*+ QB_NAME(q) BKA(z) */ 2"
               Contract.query "case-41-2" "SHOW WARNINGS"
               Contract.execute "case-42-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-42-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-42-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-42-1" "INSERT /*+ SET_VAR(max_points_in_geometry=9) */ INTO t SELECT 1 UNION ALL SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry"
               Contract.query "case-42-2" "SHOW WARNINGS"
               Contract.query "case-42-3" "SELECT id FROM t ORDER BY id"
               Contract.query "case-42-4" "SHOW WARNINGS"
               Contract.execute "case-43-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-43-create" "CREATE TABLE t(id INT, INDEX i1(id), INDEX i2(id))"
               Contract.execute "case-43-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-43-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) UPDATE /*+ BKA(z) */ t SET id=(SELECT n FROM d)"
               Contract.query "case-43-2" "SHOW WARNINGS"
               Contract.prepare "binary-update-prepare" "mutation-hint" Execute
                   "UPDATE /*+ BKA(x) SET_VAR(max_points_in_geometry=9) */ t SET id=(SELECT /*+ SET_VAR(max_points_in_geometry=7) */ @@max_points_in_geometry)" [||]
               Contract.query "binary-update-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-update-execute" "mutation-hint" OracleSuccess
               Contract.query "binary-update-execute-warnings" "SHOW WARNINGS"
               Contract.query "binary-update-rows" "SELECT id FROM t ORDER BY id"
               Contract.close "binary-update-close" "mutation-hint"
               Contract.prepare "binary-insert-prepare" "insert-hint" Execute
                   "INSERT /*+ BKA(x) */ INTO t SELECT /*+ NO_BKA(x) */ 2" [||]
               Contract.query "binary-insert-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-insert-execute" "insert-hint" OracleSuccess
               Contract.query "binary-insert-execute-warnings" "SHOW WARNINGS"
               Contract.close "binary-insert-close" "insert-hint"
            |]
          Cleanup = [| "DEALLOCATE PREPARE s"; "DROP TABLE t" |]
          Coverage = [| "statement:update", [| "text-differential"; "prepared-protocol" |]
                        "statement:delete", [| "text-differential" |]
                        "statement:insert", [| "text-differential"; "prepared-protocol" |]
                        "statement:replace", [| "text-differential" |] |] }

    let private cteHintInstances =
        { Name = "cte-hint-instances"
          Setup = [||]
          Steps =
            [|
               Contract.execute "case-1-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-1-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-1-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-1-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.execute "case-2-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-2-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-2-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-2-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT @@max_points_in_geometry AS n"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.execute "case-3-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-3-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-3-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-3-1" "WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n) SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.execute "case-4-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-4-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-4-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-4-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (SELECT /*+ BKA(y) */ n FROM c) AS n"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.execute "case-5-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-5-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-5-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-5-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT /*+ BKA(z) */ n FROM d"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.execute "case-6-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-6-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-6-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-6-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.execute "case-7-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-7-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-7-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-7-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT (WITH c AS (SELECT 2 AS n) SELECT n FROM c) AS n,@@max_points_in_geometry AS limit_value"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.execute "case-8-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-8-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-8-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-8-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c) AS n"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.execute "case-9-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-9-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-9-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-9-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) UPDATE t SET id=(WITH c AS (SELECT 2 AS n) SELECT n FROM c)"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-9-3" "SELECT id,@@max_points_in_geometry AS limit_value FROM t"
               Contract.query "case-9-4" "SHOW WARNINGS"
               Contract.execute "case-10-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-10-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-10-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-10-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) UPDATE t SET id=(WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c)"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.execute "case-11-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-11-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-11-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-11-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (SELECT /*+ BKA(y) */ 2) AS n FROM c"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.execute "case-12-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-12-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-12-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-12-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n UNION ALL SELECT n FROM c"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.execute "case-13-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-13-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-13-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-13-1" "WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2) SELECT n FROM c"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.execute "case-14-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-14-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-14-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-14-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT 1 AS n"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.execute "case-15-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-15-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-15-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-15-1" "WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT 1 AS n"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.execute "case-16-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-16-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-16-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-16-1" "WITH c AS (SELECT /*+ SET_VAR(unknown=1) */ 1 AS n) SELECT 1 AS n"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.execute "case-17-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-17-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-17-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-17-1" "WITH c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.execute "case-18-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-18-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-18-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-18-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT @@max_points_in_geometry AS n FROM c a JOIN c b ON 1"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.execute "case-19-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-19-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-19-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-19-1" "WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.execute "case-20-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-20-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-20-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-20-1" "WITH c AS (SELECT /*+ SET_VAR(unknown=1) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.execute "case-21-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-21-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-21-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-21-1" "WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.execute "case-22-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-22-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-22-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-22-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT /*+ NO_MERGE(a) NO_MERGE(b) */ a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.execute "case-23-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-23-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-23-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-23-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT n FROM c UNION ALL SELECT n FROM c"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.execute "case-24-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-24-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-24-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-24-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n), d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT a.n,b.n FROM d a JOIN d b ON 1"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.execute "case-25-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-25-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-25-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-25-1" "WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n), d AS (SELECT n FROM c) SELECT @@max_points_in_geometry AS n"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.execute "case-26-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-26-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-26-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-26-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH d AS (SELECT /*+ BKA(y) */ n FROM c) SELECT n FROM d) AS n"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.execute "case-27-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-27-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-27-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-27-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) UPDATE t JOIN c a ON 1 JOIN c b ON 1 SET id=a.n+b.n"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.execute "case-28-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-28-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-28-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-28-1" "WITH RECURSIVE c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2) SELECT a.n,b.n FROM c a JOIN c b ON a.n=b.n ORDER BY a.n"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.execute "case-29-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-29-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-29-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-29-1" "PREPARE s FROM 'WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1'"
               Contract.query "case-29-2" "SHOW WARNINGS"
               Contract.execute "case-29-3" "EXECUTE s"
               Contract.query "case-29-4" "SHOW WARNINGS"
               Contract.execute "case-30-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-30-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-30-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-30-1" "WITH c AS (SELECT /*+ QB_NAME(q) BKA(x) */ 1 AS n) SELECT /*+ BKA(a@q) */ a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-30-2" "SHOW WARNINGS"
               Contract.execute "case-31-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-31-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-31-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-31-1" "WITH a AS (SELECT /*+ BKA(x) */ 1 AS n), b AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT (SELECT n FROM b) AS n FROM a"
               Contract.query "case-31-2" "SHOW WARNINGS"
               Contract.execute "case-32-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-32-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-32-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-32-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c) AS n FROM c"
               Contract.query "case-32-2" "SHOW WARNINGS"
               Contract.execute "case-33-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-33-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-33-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-33-1" "WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) SELECT a.n FROM c a JOIN (SELECT /*+ BKA(d) */ 2 AS n) d ON 1"
               Contract.query "case-33-2" "SHOW WARNINGS"
               Contract.execute "case-34-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-34-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-34-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-34-1" "WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) SELECT (SELECT /*+ BKA(s) */ n FROM c) AS n FROM (SELECT /*+ BKA(d) */ 2 AS n) d"
               Contract.query "case-34-2" "SHOW WARNINGS"
               Contract.execute "case-35-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-35-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-35-seed" "INSERT INTO t VALUES(1)"
               Contract.execute "case-35-1" "WITH c AS (SELECT /*+ BKA(c) */ 1 AS n) UPDATE t JOIN c ON 1 SET id=(SELECT /*+ BKA(s) */ 1)"
               Contract.query "case-35-2" "SHOW WARNINGS"
               Contract.execute "case-36-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-36-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-36-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-36-1" "WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2) SELECT a.n FROM c a JOIN c b ON a.n=b.n JOIN c d ON a.n=d.n ORDER BY a.n"
               Contract.query "case-36-2" "SHOW WARNINGS"
               Contract.execute "case-37-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-37-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-37-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-37-1" "WITH c AS (SELECT /*+ QB_NAME(q) */ 1 AS n) SELECT (SELECT /*+ QB_NAME(s) */ n FROM c) AS n FROM (SELECT /*+ QB_NAME(d) */ 2 AS n) d"
               Contract.query "case-37-2" "SHOW WARNINGS"
               Contract.execute "case-38-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-38-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-38-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-38-1" "WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ n+1 FROM c WHERE n<2 UNION ALL SELECT /*+ BKA(z) */ n+2 FROM c WHERE n<2) SELECT a.n FROM c a JOIN c b ON a.n=b.n ORDER BY a.n"
               Contract.query "case-38-2" "SHOW WARNINGS"
               Contract.execute "case-39-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-39-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-39-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-39-1" "WITH RECURSIVE c AS (SELECT /*+ BKA(x) */ 1 AS n UNION ALL SELECT /*+ BKA(y) */ 2) SELECT a.n FROM c a JOIN c b ON a.n=b.n ORDER BY a.n"
               Contract.query "case-39-2" "SHOW WARNINGS"
               Contract.execute "case-40-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-40-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-40-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-40-1" "WITH c AS (SELECT /*+ BKA(x) */ 1 AS n) SELECT 1 AS n FROM (WITH c AS (SELECT /*+ BKA(y) */ 2 AS n) SELECT n FROM c) d JOIN c a ON 1"
               Contract.query "case-40-2" "SHOW WARNINGS"
               Contract.execute "case-41-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-41-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-41-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-41-1" "WITH a AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n), b AS (SELECT /*+ SET_VAR(max_points_in_geometry=9) */ n FROM a) SELECT @@max_points_in_geometry AS n FROM b x JOIN b y ON 1"
               Contract.query "case-41-2" "SHOW WARNINGS"
               Contract.execute "case-42-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-42-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-42-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-42-1" "SELECT (WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT 1) AS n,@@max_points_in_geometry AS limit_value"
               Contract.query "case-42-2" "SHOW WARNINGS"
               Contract.execute "case-43-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-43-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-43-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-43-1" "SELECT (WITH c AS (SELECT /*+ SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1) AS n,@@max_points_in_geometry AS limit_value"
               Contract.query "case-43-2" "SHOW WARNINGS"
               Contract.execute "case-44-drop" "DROP TABLE IF EXISTS t"
               Contract.execute "case-44-create" "CREATE TABLE t(id INT)"
               Contract.execute "case-44-seed" "INSERT INTO t VALUES(1)"
               Contract.query "case-44-1" "SELECT (WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(1) */ 1 AS n) SELECT 1) AS n"
               Contract.query "case-44-2" "SHOW WARNINGS"
               Contract.prepare "binary-prepare" "cte-hint" Query
                   "WITH c AS (SELECT /*+ BKA(x) SET_VAR(max_points_in_geometry=7) */ 1 AS n) SELECT @@max_points_in_geometry AS n FROM c a JOIN c b ON 1" [||]
               Contract.query "binary-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute" "cte-hint" OracleSuccess
               Contract.query "binary-execute-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute-again" "cte-hint" OracleSuccess
               Contract.query "binary-execute-again-warnings" "SHOW WARNINGS"
               Contract.close "binary-close" "cte-hint"
               Contract.query "restored-setting" "SELECT @@max_points_in_geometry AS n"
            |]
          Cleanup = [| "DEALLOCATE PREPARE s"; "DROP TABLE t" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |]
                        "statement:update", [| "text-differential" |] |] }

    let private cteHintSyntax =
        { Name = "cte-hint-syntax"
          Setup = [||]
          Steps =
            [|
               Contract.query "case-1-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT n FROM c"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1 JOIN c d ON 1"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "WITH c AS (\n SELECT /*+ BOGUS */ 1 AS n\n) SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n   )   SELECT a.n,b.n FROM c a JOIN c b ON 1"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n), d AS (SELECT /*+ OTHER */ n FROM c) SELECT a.n FROM d a JOIN d b ON 1"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT /*+ BKA(x) */ a.n FROM c a JOIN c b ON 1"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-8-3" "SELECT 1 AS n"
               Contract.query "case-8-4" "SHOW WARNINGS"
               Contract.query "case-9-1" "WITH RECURSIVE c AS (SELECT /*+ BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) SELECT n FROM c"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "WITH RECURSIVE c AS (SELECT /*+ BOGUS */ 1 AS n UNION ALL SELECT n+1 FROM c WHERE n<2) SELECT a.n FROM c a JOIN c b ON a.n=b.n ORDER BY a.n"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "WITH c AS (WITH u AS (SELECT /*+ BOGUS */ 1 AS n) SELECT 1 AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.execute "case-12-1" "PREPARE s FROM 'WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1'"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-12-3" "EXECUTE s"
               Contract.query "case-12-4" "SHOW WARNINGS"
               Contract.query "case-13-1" "WITH c AS (SELECT /*+ MAX_EXECUTION_TIME(18446744073709551616) */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "WITH c AS (\n SELECT /*+ BOGUS */ 1 AS n\n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "WITH c AS (WITH u AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM u a JOIN u b ON 1) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.execute "case-17-1" "SET sql_mode='ANSI_QUOTES'"
               Contract.query "case-17-2" "WITH c AS (SELECT /*+ BOGUS */ 1 AS \"n\") SELECT a.\"n\" FROM c a JOIN c b ON 1"
               Contract.query "case-17-3" "SHOW WARNINGS"
               Contract.query "case-18-1" "WITH c AS (SELECT /*+ BOGUS */ ')' AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.query "case-19-1" "WITH c AS (SELECT /*+ BOGUS */ /*!80000 1 */ AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.query "case-20-1" "/*!80000 */ WITH c AS (SELECT /*+ BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.prepare "binary-prepare" "cte-syntax" Query
                   "WITH c AS (SELECT /*+ QB_NAME(q) QB_NAME(r) BOGUS */ 1 AS n) SELECT a.n FROM c a JOIN c b ON 1" [||]
               Contract.query "binary-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "binary-execute" "cte-syntax" OracleSuccess
               Contract.query "binary-execute-warnings" "SHOW WARNINGS"
               Contract.close "binary-close" "cte-syntax"
            |]
          Cleanup = [| "DEALLOCATE PREPARE s"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private joinHintLifecycle =
        { Name = "join-hint-lifecycle"
          Setup = [| "CREATE TABLE t(n INT)"; "INSERT INTO t VALUES(1)" |]
          Steps =
            [| Contract.query "source-free" "SELECT /*+ JOIN_ORDER(x) JOIN_SUFFIX(y) */ 1 AS n"
               Contract.query "source-free-warnings" "SHOW WARNINGS"
               Contract.query "dual" "SELECT /*+ JOIN_PREFIX(x) JOIN_PREFIX(y) */ 1 AS n FROM DUAL"
               Contract.query "dual-warnings" "SHOW WARNINGS"
               Contract.prepare "prepare" "join-hint" Query
                   "SELECT /*+ JOIN_ORDER(x) BKA(y) */ n FROM t" [||]
               Contract.query "prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "execute" "join-hint" OracleSuccess
               Contract.query "execute-warnings" "SHOW WARNINGS"
               Contract.invoke "execute-again" "join-hint" OracleSuccess
               Contract.query "execute-again-warnings" "SHOW WARNINGS"
               Contract.close "close" "join-hint"
               Contract.query "constant-elimination" "SELECT /*+ JOIN_ORDER(x) BKA(y) */ n FROM t WHERE 0"
               Contract.query "constant-elimination-warnings" "SHOW WARNINGS"
               Contract.query "contradiction" "SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE n=1 AND n=2"
               Contract.query "contradiction-warnings" "SHOW WARNINGS"
               Contract.query "subquery-elimination" "SELECT (SELECT /*+ JOIN_ORDER(x) */ n FROM t) AS n WHERE 0"
               Contract.query "subquery-elimination-warnings" "SHOW WARNINGS"
               Contract.query "unchosen-subquery" "SELECT IF(0,(SELECT /*+ JOIN_ORDER(x) */ n FROM t),1) AS n"
               Contract.query "unchosen-subquery-warnings" "SHOW WARNINGS"
               Contract.prepare "where-prepare" "join-where" Query
                   "SELECT /*+ JOIN_ORDER(x) */ n FROM t WHERE ?+0" [| box 0 |]
               Contract.query "where-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "where-false" "join-where" OracleSuccess
               Contract.query "where-false-warnings" "SHOW WARNINGS"
               Contract.invokeWith "where-true" "join-where" [| box 1 |]
               Contract.query "where-true-warnings" "SHOW WARNINGS"
               Contract.invokeWith "where-null" "join-where" [| box DBNull.Value |]
               Contract.query "where-null-warnings" "SHOW WARNINGS"
               Contract.close "where-close" "join-where"
               Contract.prepare "limit-prepare" "join-limit" Query
                   "SELECT /*+ JOIN_ORDER(x) */ n FROM t LIMIT ?" [| box 0 |]
               Contract.query "limit-prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "limit-zero" "join-limit" OracleSuccess
               Contract.query "limit-zero-warnings" "SHOW WARNINGS"
               Contract.invokeWith "limit-one" "join-limit" [| box 1 |]
               Contract.query "limit-one-warnings" "SHOW WARNINGS"
               Contract.invokeWith "limit-zero-again" "join-limit" [| box 0 |]
               Contract.query "limit-zero-again-warnings" "SHOW WARNINGS"
               Contract.close "limit-close" "join-limit"
               Contract.execute "empty-table" "DELETE FROM t"
               Contract.query "empty-query" "SELECT /*+ JOIN_ORDER(x) */ n FROM t"
               Contract.query "empty-query-warnings" "SHOW WARNINGS" |]
          Cleanup = [| "DROP TABLE IF EXISTS t" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private joinHintMerging =
        { Name = "join-hint-merging"
          Setup = [| "CREATE TABLE merge_base(n INT)"; "INSERT INTO merge_base VALUES(1)" |]
          Steps =
            [|
               Contract.query "case-1-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-1-2" "SHOW WARNINGS"
               Contract.query "case-2-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-2-2" "SHOW WARNINGS"
               Contract.query "case-3-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d WHERE 0"
               Contract.query "case-3-2" "SHOW WARNINGS"
               Contract.query "case-4-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d WHERE 0"
               Contract.query "case-4-2" "SHOW WARNINGS"
               Contract.query "case-5-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d"
               Contract.query "case-5-2" "SHOW WARNINGS"
               Contract.query "case-6-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d"
               Contract.query "case-6-2" "SHOW WARNINGS"
               Contract.query "case-7-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d WHERE 0"
               Contract.query "case-7-2" "SHOW WARNINGS"
               Contract.query "case-8-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d WHERE 0"
               Contract.query "case-8-2" "SHOW WARNINGS"
               Contract.query "case-9-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d"
               Contract.query "case-9-2" "SHOW WARNINGS"
               Contract.query "case-10-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d"
               Contract.query "case-10-2" "SHOW WARNINGS"
               Contract.query "case-11-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d WHERE 0"
               Contract.query "case-11-2" "SHOW WARNINGS"
               Contract.query "case-12-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base LIMIT 1) d WHERE 0"
               Contract.query "case-12-2" "SHOW WARNINGS"
               Contract.query "case-13-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d"
               Contract.query "case-13-2" "SHOW WARNINGS"
               Contract.query "case-14-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d"
               Contract.query "case-14-2" "SHOW WARNINGS"
               Contract.query "case-15-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d WHERE 0"
               Contract.query "case-15-2" "SHOW WARNINGS"
               Contract.query "case-16-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ COUNT(*) AS n FROM merge_base) d WHERE 0"
               Contract.query "case-16-2" "SHOW WARNINGS"
               Contract.query "case-17-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d"
               Contract.query "case-17-2" "SHOW WARNINGS"
               Contract.query "case-18-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d"
               Contract.query "case-18-2" "SHOW WARNINGS"
               Contract.query "case-19-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d WHERE 0"
               Contract.query "case-19-2" "SHOW WARNINGS"
               Contract.query "case-20-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base WHERE 0) d WHERE 0"
               Contract.query "case-20-2" "SHOW WARNINGS"
               Contract.query "case-21-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d"
               Contract.query "case-21-2" "SHOW WARNINGS"
               Contract.query "case-22-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d"
               Contract.query "case-22-2" "SHOW WARNINGS"
               Contract.query "case-23-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d WHERE 0"
               Contract.query "case-23-2" "SHOW WARNINGS"
               Contract.query "case-24-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ 1 AS n) d WHERE 0"
               Contract.query "case-24-2" "SHOW WARNINGS"
               Contract.query "case-25-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d"
               Contract.query "case-25-2" "SHOW WARNINGS"
               Contract.query "case-26-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d"
               Contract.query "case-26-2" "SHOW WARNINGS"
               Contract.query "case-27-1" "SELECT  d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d WHERE 0"
               Contract.query "case-27-2" "SHOW WARNINGS"
               Contract.query "case-28-1" "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM (SELECT 1 AS n) t) d WHERE 0"
               Contract.query "case-28-2" "SHOW WARNINGS"
               Contract.query "case-29-1" "WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT n FROM c"
               Contract.query "case-29-2" "SHOW WARNINGS"
               Contract.query "case-30-1" "WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT a.n FROM c a JOIN c b ON a.n=b.n"
               Contract.query "case-30-2" "SHOW WARNINGS"
               Contract.query "case-31-1" "WITH c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) SELECT /*+ NO_MERGE(a) NO_MERGE(b) */ a.n FROM c a JOIN c b ON a.n=b.n"
               Contract.query "case-31-2" "SHOW WARNINGS"
               Contract.query "case-32-1" "SELECT /*+ MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) d"
               Contract.query "case-32-2" "SHOW WARNINGS"
               Contract.query "case-33-1" "SELECT (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) AS n"
               Contract.query "case-33-2" "SHOW WARNINGS"
               Contract.query "case-34-1" "SELECT d.n FROM (SELECT /*+ BKA(x) JOIN_ORDER(y) */ n FROM merge_base) d"
               Contract.query "case-34-2" "SHOW WARNINGS"
               Contract.query "case-36-1" "SELECT /*+ NO_MERGE(d) MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-36-2" "SHOW WARNINGS"
               Contract.query "case-37-1" "SELECT /*+ MERGE(d) NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-37-2" "SHOW WARNINGS"
               Contract.query "case-38-1" "SELECT /*+ NO_MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-38-2" "SHOW WARNINGS"
               Contract.query "case-39-1" "SELECT /*+ QB_NAME(q) NO_MERGE(d@q) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-39-2" "SHOW WARNINGS"
               Contract.query "case-40-1" "SELECT /*+ NO_MERGE(@q d) QB_NAME(q) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d"
               Contract.query "case-40-2" "SHOW WARNINGS"
               Contract.query "case-41-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base GROUP BY n) d WHERE 0"
               Contract.query "case-41-2" "SHOW WARNINGS"
               Contract.query "case-42-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base HAVING 1) d WHERE 0"
               Contract.query "case-42-2" "SHOW WARNINGS"
               Contract.query "case-43-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base ORDER BY n) d"
               Contract.query "case-43-2" "SHOW WARNINGS"
               Contract.query "case-44-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ ROW_NUMBER() OVER() AS n FROM merge_base) d WHERE 0"
               Contract.query "case-44-2" "SHOW WARNINGS"
               Contract.query "case-45-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ (SELECT 1) AS n FROM merge_base) d WHERE 0"
               Contract.query "case-45-2" "SHOW WARNINGS"
               Contract.query "case-46-1" "SELECT d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base UNION ALL SELECT 2) d WHERE 0"
               Contract.query "case-46-2" "SHOW WARNINGS"
               Contract.query "case-47-1" "WITH c AS (SELECT /*+ JOIN_ORDER(x) */ DISTINCT n FROM merge_base) SELECT n FROM c"
               Contract.query "case-47-2" "SHOW WARNINGS"
               Contract.query "case-48-1" "WITH RECURSIVE c AS (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base UNION ALL SELECT n+1 FROM c WHERE n<2) SELECT n FROM c ORDER BY n"
               Contract.query "case-48-2" "SHOW WARNINGS"
               Contract.query "case-49-1" "SELECT /*+ NO_MERGE(d) JOIN_ORDER(parent_missing) */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM merge_base) d"
               Contract.query "case-49-2" "SHOW WARNINGS"
               Contract.query "case-50-1" "SELECT /*+ JOIN_ORDER(parent_missing) */ (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM merge_base) AS n FROM merge_base"
               Contract.query "case-50-2" "SHOW WARNINGS"
               Contract.query "case-51-1" "SELECT /*+ MERGE(d) NO_MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM merge_base) d"
               Contract.query "case-51-2" "SHOW WARNINGS"
               Contract.query "case-52-1" "SELECT /*+ NO_MERGE(d) MERGE() */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM merge_base) d"
               Contract.query "case-52-2" "SHOW WARNINGS"
               Contract.query "case-53-1" "SELECT /*+ NO_MERGE() MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(child_missing) */ n FROM merge_base) d"
               Contract.query "case-53-2" "SHOW WARNINGS"
               Contract.prepare "prepare" "merge-hint" Query
                   "SELECT /*+ NO_MERGE(d) */ d.n FROM (SELECT /*+ JOIN_ORDER(x) */ n FROM merge_base) d WHERE ?" [| box 0 |]
               Contract.query "prepare-warnings" "SHOW WARNINGS"
               Contract.invoke "execute-eliminated" "merge-hint" OracleSuccess
               Contract.query "eliminated-warnings" "SHOW WARNINGS"
               Contract.invokeWith "execute-visible" "merge-hint" [| box 1 |]
               Contract.query "visible-warnings" "SHOW WARNINGS"
               Contract.close "close" "merge-hint"
            |]
          Cleanup = [| "DROP TABLE IF EXISTS merge_base" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private predicateConversion =
        let scripts =
                [ "SELECT 1 AS n WHERE 'x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 'x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING 'x';SHOW WARNINGS", None
                  "SELECT IF('x',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT 'x' AS n;SHOW WARNINGS", None
                  "SELECT 'x' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT 1 AS n WHERE '1x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE '1x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING '1x';SHOW WARNINGS", None
                  "SELECT IF('1x',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT '1x' AS n;SHOW WARNINGS", None
                  "SELECT '1x' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT 1 AS n WHERE '0x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE '0x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING '0x';SHOW WARNINGS", None
                  "SELECT IF('0x',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT '0x' AS n;SHOW WARNINGS", None
                  "SELECT '0x' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT 1 AS n WHERE '';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE '';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING '';SHOW WARNINGS", None
                  "SELECT IF('',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT '' AS n;SHOW WARNINGS", None
                  "SELECT '' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT 1 AS n WHERE ' 1 ';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE ' 1 ';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING ' 1 ';SHOW WARNINGS", None
                  "SELECT IF(' 1 ',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT ' 1 ' AS n;SHOW WARNINGS", None
                  "SELECT ' 1 ' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT 1 AS n WHERE '1e2x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE '1e2x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base HAVING '1e2x';SHOW WARNINGS", None
                  "SELECT IF('1e2x',1,0) AS n;SHOW WARNINGS", None
                  "SELECT NOT '1e2x' AS n;SHOW WARNINGS", None
                  "SELECT '1e2x' IS TRUE AS n;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 0 AND 'x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 1 OR 'x';SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 'x' AND 0;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 'x' OR 1;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE 'x' LIMIT 0;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE s;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE n=1 AND s;SHOW WARNINGS", None
                  "SELECT n FROM predicate_base WHERE '1x';SHOW WARNINGS", None
                  "DELETE FROM predicate_base WHERE 'x';SHOW WARNINGS", Some(1292, "22007")
                  "UPDATE predicate_base SET n=n+1 WHERE 'x';SHOW WARNINGS", Some(1292, "22007")
                  "SELECT a.n FROM predicate_base a JOIN predicate_base b ON 'x';SHOW WARNINGS", None
                  "DELETE FROM predicate_base;SELECT n FROM predicate_base WHERE 'x';SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT n FROM predicate_base WHERE ?';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='1x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='0.5x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n HAVING ?';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT IF(?,1,0) AS n';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT NOT ? AS n';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT ? IS TRUE AS n';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='0.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='1.5';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='1e2';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='1e2x';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v='';EXECUTE p USING @v;SHOW WARNINGS", None
                  "PREPARE p FROM 'SELECT 1 AS n WHERE ?+0';SHOW WARNINGS;SET @v=0.5;EXECUTE p USING @v;SHOW WARNINGS", None ]
        { Name = "predicate-conversion"
          Setup = [| "CREATE TABLE predicate_base(n INT,s VARCHAR(20))" |]
          Steps =
            [| for index, (sql, error) in List.indexed scripts do
                   yield Contract.execute (sprintf "reset-%d" index) "DELETE FROM predicate_base"
                   yield Contract.execute (sprintf "seed-%d" index) "INSERT INTO predicate_base VALUES(1,'x'),(2,'1x'),(3,'0x')"
                   for part, statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.indexed do
                       let step = Contract.query (sprintf "case-%d-%d" index part) statement
                       yield match error with
                             | Some(code, state) when part = 0 -> Contract.fails code state step
                             | _ -> step
               for index, sql in
                   [ "SELECT 1 AS n WHERE ?"; "SELECT 1 AS n HAVING ?"
                     "SELECT IF(?,1,0) AS n"; "SELECT NOT ? AS n"
                     "SELECT ? IS TRUE AS n"; "SELECT 1 AS n WHERE ?+0" ] |> List.indexed do
                   let handle = sprintf "boolean-%d" index
                   yield Contract.prepare (handle + "-prepare") handle Query sql [| box "x" |]
                   yield Contract.query (handle + "-prepare-warnings") "SHOW WARNINGS"
                   for value in [ "x"; "1x"; "0.5x"; "0.5"; ""; "1e2x" ] do
                       yield Contract.invokeWith (handle + "-" + value) handle [| box value |]
                       yield Contract.query (handle + "-warnings-" + value) "SHOW WARNINGS"
                   yield Contract.close (handle + "-close") handle |]
          Cleanup = [| "DROP TABLE IF EXISTS predicate_base" |]
          Coverage = [| "statement:select", [| "text-differential"; "prepared-protocol" |] |] }

    let private mutationConversion =
        let scripts =
                [ "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';UPDATE predicate_base SET n=n+10 WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "SET sql_mode='';DELETE FROM predicate_base WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "UPDATE IGNORE predicate_base SET n=n+10 WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE 'x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE '1x';SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE n=1 OR s;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE 'x' LIMIT 0;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n"
                  "DELETE IGNORE FROM predicate_base WHERE '1x' LIMIT 1;SHOW WARNINGS;SELECT n,s FROM predicate_base ORDER BY n" ]
        { Name = "mutation-conversion"
          Setup = [| "CREATE TABLE predicate_base(n INT,s VARCHAR(20))" |]
          Steps =
            [| for index, sql in List.indexed scripts do
                   yield Contract.execute (sprintf "mode-%d" index) "SET sql_mode=DEFAULT"
                   yield Contract.execute (sprintf "reset-%d" index) "DELETE FROM predicate_base"
                   yield Contract.execute (sprintf "seed-%d" index) "INSERT INTO predicate_base VALUES(1,'x'),(2,'1x'),(3,'0x')"
                   for part, statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries) |> Array.indexed do
                       let name = sprintf "case-%d-%d" index part
                       yield
                           if statement.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
                              || statement.StartsWith("SHOW", StringComparison.OrdinalIgnoreCase) then
                               Contract.query name statement
                           else Contract.execute name statement |]
          Cleanup = [| "DROP TABLE IF EXISTS predicate_base"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:update", [| "text-differential" |]; "statement:delete", [| "text-differential" |] |] }

    let private missingTableDiagnostics =
        let cases =
            [
              "select", "SELECT * FROM absent", Some(1146, "42S02")
              "mixed-case", "SELECT * FROM AbSeNt", Some(1146, "42S02")
              "insert", "INSERT INTO absent VALUES(1)", Some(1146, "42S02")
              "replace", "REPLACE INTO absent VALUES(1)", Some(1146, "42S02")
              "update", "UPDATE absent SET n=1", Some(1146, "42S02")
              "delete", "DELETE FROM absent", Some(1146, "42S02")
              "truncate", "TRUNCATE TABLE absent", Some(1146, "42S02")
              "alter", "ALTER TABLE absent ADD n INT", Some(1146, "42S02")
              "show-create", "SHOW CREATE TABLE absent", Some(1146, "42S02")
              "describe", "DESCRIBE absent", Some(1146, "42S02")
              "show-columns", "SHOW COLUMNS FROM absent", Some(1146, "42S02")
              "show-index", "SHOW INDEX FROM absent", Some(1146, "42S02")
              "explain", "EXPLAIN SELECT * FROM absent", Some(1146, "42S02")
              "create-like", "CREATE TABLE copied LIKE absent", Some(1146, "42S02")
              "create-view", "CREATE VIEW absent_view AS SELECT * FROM absent", Some(1146, "42S02")
              "rename", "RENAME TABLE absent TO renamed", Some(1146, "42S02")
              "drop", "DROP TABLE absent", Some(1051, "42S02")
              "drop-ignore", "DROP TABLE IF EXISTS absent", None
              "prepare", "PREPARE missing_statement FROM 'SELECT * FROM absent'", Some(1146, "42S02")
              "lock", "LOCK TABLES absent READ", Some(1146, "42S02")
              "missing-db-select", "SELECT * FROM nowhere.absent", Some(1049, "42000")
              "missing-db-insert", "INSERT INTO nowhere.absent VALUES(1)", Some(1049, "42000")
              "missing-db-update", "UPDATE nowhere.absent SET n=1", Some(1049, "42000")
              "missing-db-delete", "DELETE FROM nowhere.absent", Some(1049, "42000")
              "missing-db-truncate", "TRUNCATE TABLE nowhere.absent", Some(1146, "42S02")
              "missing-db-alter", "ALTER TABLE nowhere.absent ADD n INT", Some(1049, "42000")
              "missing-db-show-create", "SHOW CREATE TABLE nowhere.absent", Some(1049, "42000")
              "missing-db-create", "CREATE TABLE nowhere.absent(n INT)", Some(1049, "42000")
              "missing-db-drop", "DROP TABLE nowhere.absent", Some(1051, "42S02")
            ]
        { Name = "missing-table-diagnostics"
          Setup = [||]
          Steps =
            [| for name, sql, error in cases do
                   let operation =
                       if [ "SELECT"; "SHOW"; "DESCRIBE"; "EXPLAIN" ] |> List.exists (fun prefix -> sql.StartsWith(prefix, StringComparison.Ordinal)) then
                           Contract.query name sql
                       else Contract.execute name sql
                   yield
                       match error with
                       | Some(code, state) -> operation |> Contract.fails code state
                       | None -> operation
                   yield Contract.query (name + "-warnings") "SHOW WARNINGS"
               yield Contract.query "checksum-missing" "CHECKSUM TABLE absent, nowhere.absent EXTENDED" |> Contract.comparingValues
               yield Contract.query "checksum-missing-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               yield Contract.query "checksum-quick-missing" "CHECKSUM TABLE absent, nowhere.absent QUICK" |> Contract.comparingValues
               yield Contract.query "checksum-quick-missing-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               yield Contract.execute "checksum-create-base" "CREATE TABLE checksum_base(id INT)"
               yield Contract.execute "checksum-create-view" "CREATE VIEW checksum_view AS SELECT id FROM checksum_base"
               yield Contract.query "checksum-view" "CHECKSUM TABLE checksum_view,absent QUICK" |> Contract.comparingValues
               yield Contract.query "checksum-view-warnings" "SHOW WARNINGS" |> Contract.comparingValues
               yield Contract.execute "checksum-create-temporary" "CREATE TEMPORARY TABLE checksum_temp(id INT)"
               yield Contract.query "checksum-temporary-quick" "CHECKSUM TABLE checksum_temp QUICK" |> Contract.comparingValues
               yield Contract.query "checksum-temporary-quick-warnings" "SHOW WARNINGS" |> Contract.comparingValues |]
          Cleanup = [| "DROP VIEW IF EXISTS absent_view"; "DROP VIEW IF EXISTS checksum_view"; "DROP TABLE IF EXISTS copied,checksum_base" |]
          Coverage = [| "statement:select", [| "text-differential" |]; "statement:drop-table", [| "text-differential" |]; "statement:create-view", [| "text-differential" |] |] }

    let private isolatedScriptStepsWithErrors (cases: (string * (int * int * string) list * string list) list) =
        [| for name, errors, statements in cases do
               for index, sql in List.indexed statements do
                   let label = sprintf "%s-%d" name index
                   let operation =
                       if sql.StartsWith("SHOW", StringComparison.Ordinal) || sql.StartsWith("SELECT", StringComparison.Ordinal) || sql.StartsWith("DESCRIBE", StringComparison.Ordinal) then Contract.query label sql
                       else Contract.execute label sql
                   let step =
                       match errors |> List.tryFind (fun (failingIndex, _, _) -> index = failingIndex) with
                       | Some(_, code, state) -> operation |> Contract.fails code state
                       | _ -> operation
                   yield step |> Contract.on name |]

    let private isolatedScriptSteps cases =
        cases
        |> List.map (fun (name, error, statements) -> name, Option.toList error, statements)
        |> isolatedScriptStepsWithErrors

    let private foreignKeyIndexesAndCollisions =
        let reset =
            [ "USE fk_index_probe"
              "SET foreign_key_checks=0"
              "DROP TABLE IF EXISTS child,parent,other,renamed"
              "SET foreign_key_checks=1"
              "CREATE TABLE parent(n INT PRIMARY KEY)" ]
        let cases =
            [
              "index-create-bare", [],
                  [ "CREATE TABLE child(a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-constraint", [],
                  [ "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-index", [],
                  [ "CREATE TABLE child(a INT,b INT,FOREIGN KEY ix(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-both", [],
                  [ "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY ix(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-existing", [],
                  [ "CREATE TABLE child(a INT,b INT,KEY existing(a,b),CONSTRAINT fk FOREIGN KEY ix(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-invisible", [],
                  [ "CREATE TABLE child(a INT,b INT,KEY existing(a) INVISIBLE,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-same-columns", [],
                  [ "CREATE TABLE child(a INT,b INT,CONSTRAINT first_fk FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT second_fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-same-name-columns", [ (5, 1826, "HY000"); (6, 1146, "42S02") ],
                  [ "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-same-index-name", [ (5, 1061, "42000"); (6, 1146, "42S02") ],
                  [ "CREATE TABLE child(a INT,b INT,FOREIGN KEY ix(a) REFERENCES parent(n),FOREIGN KEY ix(b) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-create-occupied-name", [],
                  [ "CREATE TABLE child(a INT,b INT,KEY a(b),FOREIGN KEY(a) REFERENCES parent(n))"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-alter-bare", [],
                  [ "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD FOREIGN KEY(a) REFERENCES parent(n)"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-alter-constraint", [],
                  [ "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n)"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-alter-index", [],
                  [ "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD FOREIGN KEY ix(a) REFERENCES parent(n)"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "index-alter-both", [],
                  [ "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD CONSTRAINT fk FOREIGN KEY ix(a) REFERENCES parent(n)"
                    "SHOW INDEX FROM child"
                    "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' AND TABLE_NAME='child' ORDER BY CONSTRAINT_NAME" ]
              "collision-schema", [ (6, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-case-insensitive", [ (6, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT SHARED FOREIGN KEY(a) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-checks-disabled", [ (7, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "SET foreign_key_checks=0"
                    "CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "SET foreign_key_checks=1"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-generated-explicit", [ (5, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT child_ibfk_1 FOREIGN KEY(b) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-duplicate-explicit-shared-index", [ (5, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,KEY a(a),CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-alter-existing", [ (6, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-alter-other", [ (7, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT)"
                    "ALTER TABLE other ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-alter-drop-add", [ (6, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child DROP FOREIGN KEY shared,ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-alter-add-drop", [ (6, 1826, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),DROP FOREIGN KEY shared"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-alter-two-adds", [ (6, 1061, "42000") ],
                  [ "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n),ADD CONSTRAINT shared FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "collision-missing-parent-precedence", [ (6, 1824, "HY000") ],
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES missing(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_index_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
            ]
        { Name = "foreign-key-indexes-and-collisions"
          Setup = [| "CREATE DATABASE fk_index_probe" |]
          Steps = cases |> List.map (fun (name, errors, statements) -> name, errors, reset @ statements) |> isolatedScriptStepsWithErrors
          Cleanup = [| "DROP DATABASE IF EXISTS fk_index_probe" |]
          Coverage = [| "statement:create-table", [| "text-differential" |]; "statement:alter-table", [| "text-differential" |] |] }

    let private foreignKeyIndexLifecycle =
        let reset =
            [ "USE fk_lifecycle_probe"
              "SET foreign_key_checks=0"
              "DROP TABLE IF EXISTS child,parent"
              "SET foreign_key_checks=1"
              "CREATE TABLE parent(n INT PRIMARY KEY)"
              "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))" ]
        let observe =
            [ "SHOW INDEX FROM child"
              "SELECT CONSTRAINT_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_lifecycle_probe' ORDER BY CONSTRAINT_NAME" ]
        let cases =
            [
              "duplicate-primary", Some(7, 1068, "42000"),
                  [ "ALTER TABLE child ADD PRIMARY KEY(a)"; "ALTER TABLE child ADD PRIMARY KEY(b)" ]
              "duplicate-generated-name", None, [ "ALTER TABLE child ADD INDEX fk(a,b)" ]
              "drop-child", Some(6, 1553, "HY000"),
                  [ "ALTER TABLE child DROP INDEX fk" ]
              "drop-child-off", Some(7, 1553, "HY000"),
                  [ "SET foreign_key_checks=0"
                    "ALTER TABLE child DROP INDEX fk" ]
              "drop-parent", Some(6, 1553, "HY000"),
                  [ "ALTER TABLE parent DROP PRIMARY KEY" ]
              "drop-constraint-index", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,DROP INDEX fk" ]
              "drop-index-constraint", None,
                  [ "ALTER TABLE child DROP INDEX fk,DROP FOREIGN KEY fk" ]
              "replace-index", None,
                  [ "ALTER TABLE child DROP INDEX fk,ADD INDEX replacement(a,b)" ]
              "add-redundant", None,
                  [ "ALTER TABLE child ADD INDEX replacement(a,b)" ]
              "drop-after-replacement", Some(7, 1091, "42000"),
                  [ "ALTER TABLE child ADD INDEX replacement(a,b)"
                    "ALTER TABLE child DROP INDEX fk" ]
              "drop-constraint", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk" ]
              "rename-index", Some(7, 1553, "HY000"),
                  [ "ALTER TABLE child RENAME INDEX fk TO replacement"
                    "ALTER TABLE child DROP INDEX replacement" ]
              "drop-parent-off", Some(7, 1553, "HY000"),
                  [ "SET foreign_key_checks=0"
                    "ALTER TABLE parent DROP PRIMARY KEY" ]
              "replace-parent", None,
                  [ "ALTER TABLE parent DROP PRIMARY KEY,ADD UNIQUE KEY replacement(n)" ]
              "child-alternate", Some(7, 1553, "HY000"),
                  [ "ALTER TABLE child ADD INDEX alternate(a,b)"
                    "ALTER TABLE child DROP INDEX alternate" ]
              "explicit-retained", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,DROP INDEX fk,ADD INDEX explicit_index(a)"
                    "ALTER TABLE child ADD CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n)"
                    "ALTER TABLE child ADD INDEX replacement(a,b)" ]
              "renamed-generated", None,
                  [ "ALTER TABLE child RENAME INDEX fk TO renamed"
                    "ALTER TABLE child ADD INDEX replacement(a,b)" ]
              "orphan-generated", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk"
                    "ALTER TABLE child ADD INDEX replacement(a,b)" ]
              "prefix-replacement", None,
                  [ "ALTER TABLE child ADD INDEX replacement(b,a)" ]
              "unique-replacement", None,
                  [ "ALTER TABLE child ADD UNIQUE INDEX replacement(a,b)" ]
              "create-like", None,
                  [ "CREATE TABLE copied LIKE child"
                    "SHOW INDEX FROM copied"
                    "ALTER TABLE copied ADD INDEX replacement(a,b)"
                    "SHOW INDEX FROM copied"
                    "DROP TABLE copied" ]
              "rename-alone", None,
                  [ "ALTER TABLE child RENAME INDEX fk TO renamed" ]
              "visibility", None,
                  [ "ALTER TABLE child ALTER INDEX fk INVISIBLE"
                    "ALTER TABLE child ADD INDEX replacement(a,b)" ]
              "primary-replacement", None,
                  [ "ALTER TABLE child ADD PRIMARY KEY(a)" ]
              "longer-generated-replacement", None,
                  [ "ALTER TABLE parent ADD COLUMN m INT,ADD UNIQUE INDEX two(n,m)"
                    "ALTER TABLE child ADD CONSTRAINT longer FOREIGN KEY(a,b) REFERENCES parent(n,m)" ]
            ]
        { Name = "foreign-key-index-lifecycle"
          Setup = [| "CREATE DATABASE fk_lifecycle_probe" |]
          Steps = cases |> List.map (fun (name, error, statements) -> name, error, reset @ statements @ observe) |> isolatedScriptSteps
          Cleanup = [| "DROP DATABASE IF EXISTS fk_lifecycle_probe" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private foreignKeyRenameCollisions =
        let reset =
            [ "USE fk_rename_probe"
              "SET foreign_key_checks=0"
              "DROP TABLE IF EXISTS child,other,parent,renamed,parent_new,fk_rename_target.other,fk_rename_target.renamed"
              "SET foreign_key_checks=1"
              "CREATE TABLE parent(n INT PRIMARY KEY)" ]
        let observe =
            [ "SELECT TABLE_SCHEMA,TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA IN ('fk_rename_probe','fk_rename_target') ORDER BY TABLE_SCHEMA,TABLE_NAME"
              "SELECT CONSTRAINT_SCHEMA,CONSTRAINT_NAME,TABLE_NAME,REFERENCED_TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA IN ('fk_rename_probe','fk_rename_target') ORDER BY CONSTRAINT_SCHEMA,TABLE_NAME,CONSTRAINT_NAME" ]
        let cases =
            [
              "rename-generated-collision", Some(7, 1826, "HY000"),
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE child TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE fk_rename_probe.other DROP FOREIGN KEY renamed_ibfk_1" ]
              "alter-generated-collision", Some(7, 1826, "HY000"),
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child RENAME TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE fk_rename_probe.other DROP FOREIGN KEY renamed_ibfk_1" ]
              "rename-cross-explicit", Some(7, 1826, "HY000"),
                  [ "CREATE TABLE child(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE fk_rename_target.other(a INT,CONSTRAINT shared FOREIGN KEY(a) REFERENCES fk_rename_probe.parent(n))"
                    "RENAME TABLE child TO fk_rename_target.renamed" ],
                  [ "ALTER TABLE fk_rename_probe.child DROP FOREIGN KEY shared"
                    "ALTER TABLE fk_rename_target.other DROP FOREIGN KEY shared" ]
              "rename-cross-generated", Some(7, 1826, "HY000"),
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE fk_rename_target.other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES fk_rename_probe.parent(n))"
                    "RENAME TABLE child TO fk_rename_target.renamed" ],
                  [ "ALTER TABLE fk_rename_probe.child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE fk_rename_target.other DROP FOREIGN KEY renamed_ibfk_1" ]
              "rename-prefixed-explicit", None,
                  [ "CREATE TABLE child(a INT,CONSTRAINT child_ibfk_01 FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE child TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.renamed DROP FOREIGN KEY renamed_ibfk_01" ]
              "rename-explicit", None,
                  [ "CREATE TABLE child(a INT,CONSTRAINT custom FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE child TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.renamed DROP FOREIGN KEY custom" ]
              "rename-clear-collision", None,
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE other TO fk_rename_target.other,child TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.renamed DROP FOREIGN KEY renamed_ibfk_1"
                    "ALTER TABLE fk_rename_target.other DROP FOREIGN KEY renamed_ibfk_1" ]
              "rename-batch-atomic", Some(7, 1826, "HY000"),
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "CREATE TABLE other(a INT,CONSTRAINT renamed_ibfk_1 FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE parent TO parent_new,child TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE fk_rename_probe.other DROP FOREIGN KEY renamed_ibfk_1" ]
              "alter-generated", None,
                  [ "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child RENAME TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.renamed DROP FOREIGN KEY renamed_ibfk_1" ]
              "alter-prefixed-explicit", None,
                  [ "CREATE TABLE child(a INT,CONSTRAINT child_ibfk_01 FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child RENAME TO renamed" ],
                  [ "ALTER TABLE fk_rename_probe.renamed DROP FOREIGN KEY renamed_ibfk_01" ]
            ]
        { Name = "foreign-key-rename-collisions"
          Setup = [| "CREATE DATABASE fk_rename_probe"; "CREATE DATABASE fk_rename_target" |]
          // MySQL 8.4.11 can crash when dropping tables after a rename collision; remove constraints first.
          Steps = cases |> List.map (fun (name, error, statements, teardown) -> name, error, reset @ statements @ observe @ teardown) |> isolatedScriptSteps
          Cleanup = [| "SET foreign_key_checks=0"; "DROP DATABASE IF EXISTS fk_rename_probe"; "DROP DATABASE IF EXISTS fk_rename_target"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:rename-table", [| "text-differential" |]; "statement:alter-table", [| "text-differential" |] |] }

    let private foreignKeyAlterDefinitions =
        let reset =
            [ "USE fk_alter_probe"
              "SET foreign_key_checks=0"
              "DROP TABLE IF EXISTS child,parent,renamed"
              "SET foreign_key_checks=1"
              "CREATE TABLE parent(n INT PRIMARY KEY,m INT)"
              "CREATE TABLE child(a INT,b INT,CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n))" ]
        let observe =
            [ "SELECT TABLE_NAME,COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='fk_alter_probe' ORDER BY TABLE_NAME,ORDINAL_POSITION"
              "SELECT TABLE_NAME,CONSTRAINT_NAME,COLUMN_NAME,REFERENCED_TABLE_NAME,REFERENCED_COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='fk_alter_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME,ORDINAL_POSITION" ]
        let cases =
            [
              "drop-missing", Some(6, 1091, "42000"),
                  [ "ALTER TABLE child DROP FOREIGN KEY missing" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-twice", Some(6, 1091, "42000"),
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,DROP FOREIGN KEY fk" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "add-drop-new", Some(6, 1091, "42000"),
                  [ "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(b) REFERENCES parent(n),DROP FOREIGN KEY new_fk" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-add-same", Some(6, 1061, "42000"),
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,ADD CONSTRAINT fk FOREIGN KEY(b) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-child-column", Some(6, 1828, "HY000"),
                  [ "ALTER TABLE child DROP COLUMN a" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-parent-column", Some(6, 1829, "HY000"),
                  [ "ALTER TABLE parent DROP COLUMN n" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-fk-column", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,DROP COLUMN a" ],
                  [  ]
              "drop-column-fk", None,
                  [ "ALTER TABLE child DROP COLUMN a,DROP FOREIGN KEY fk" ],
                  [  ]
              "add-fk-column", None,
                  [ "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(c) REFERENCES parent(n),ADD COLUMN c INT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY new_fk" ]
              "add-column-fk", None,
                  [ "ALTER TABLE child ADD COLUMN c INT,ADD CONSTRAINT new_fk FOREIGN KEY(c) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY new_fk" ]
              "add-fk-modify-compatible", None,
                  [ "ALTER TABLE child ADD COLUMN c BIGINT"
                    "ALTER TABLE child ADD CONSTRAINT final_fk FOREIGN KEY(c) REFERENCES parent(n),MODIFY c INT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY final_fk" ]
              "modify-add-fk-compatible", None,
                  [ "ALTER TABLE child ADD COLUMN c BIGINT"
                    "ALTER TABLE child MODIFY c INT,ADD CONSTRAINT final_fk FOREIGN KEY(c) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY final_fk" ]
              "add-fk-modify-incompatible", Some(6, 3780, "HY000"),
                  [ "ALTER TABLE child ADD CONSTRAINT final_fk FOREIGN KEY(b) REFERENCES parent(n),MODIFY b BIGINT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "modify-drop-fk", None,
                  [ "ALTER TABLE child MODIFY a BIGINT,DROP FOREIGN KEY fk" ],
                  []
              "drop-fk-modify", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,MODIFY a BIGINT" ],
                  []
              "modify-drop-readd-fk", Some(6, 3780, "HY000"),
                  [ "ALTER TABLE child MODIFY a BIGINT,DROP FOREIGN KEY fk,ADD CONSTRAINT fk FOREIGN KEY(a) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "change-compatible", None,
                  [ "ALTER TABLE child CHANGE COLUMN a renamed INT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "change-incompatible", Some(6, 3780, "HY000"),
                  [ "ALTER TABLE child CHANGE COLUMN a renamed BIGINT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-fk-change", None,
                  [ "ALTER TABLE child DROP FOREIGN KEY fk,CHANGE COLUMN a renamed BIGINT" ],
                  []
              "change-drop-fk", None,
                  [ "ALTER TABLE child CHANGE COLUMN a renamed BIGINT,DROP FOREIGN KEY fk" ],
                  []
              "add-fk-change-final", None,
                  [ "ALTER TABLE child ADD CONSTRAINT new_fk FOREIGN KEY(renamed) REFERENCES parent(n),CHANGE COLUMN b renamed INT" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY new_fk" ]
              "change-add-fk-final", None,
                  [ "ALTER TABLE child CHANGE COLUMN b renamed INT,ADD CONSTRAINT new_fk FOREIGN KEY(renamed) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY new_fk" ]
              "rename-child-column", None,
                  [ "ALTER TABLE child RENAME COLUMN a TO renamed" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "rename-parent-column", None,
                  [ "ALTER TABLE parent RENAME COLUMN n TO renamed" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-child-off", Some(7, 1828, "HY000"),
                  [ "SET foreign_key_checks=0"
                    "ALTER TABLE child DROP COLUMN a" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-parent-off", Some(7, 1829, "HY000"),
                  [ "SET foreign_key_checks=0"
                    "ALTER TABLE parent DROP COLUMN n" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "add-rename-table", None,
                  [ "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n),RENAME TO renamed" ],
                  [ "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY renamed_ibfk_1" ]
              "rename-table-add", None,
                  [ "ALTER TABLE child RENAME TO renamed,ADD FOREIGN KEY(b) REFERENCES parent(n)" ],
                  [ "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY renamed_ibfk_1" ]
              "explicit-add-rename-table", None,
                  [ "ALTER TABLE child ADD CONSTRAINT child_ibfk_9 FOREIGN KEY(b) REFERENCES parent(n),RENAME TO renamed" ],
                  [ "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY fk"
                    "ALTER TABLE fk_alter_probe.renamed DROP FOREIGN KEY renamed_ibfk_9" ]
              "add-old-rename-column", Some(6, 1072, "42000"),
                  [ "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n),RENAME COLUMN b TO c" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "add-new-rename-column", None,
                  [ "ALTER TABLE child ADD FOREIGN KEY(c) REFERENCES parent(n),RENAME COLUMN b TO c" ],
                  [ "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE fk_alter_probe.child DROP FOREIGN KEY fk" ]
              "drop-parent-cross-schema", Some(9, 1829, "HY000"),
                  [ "ALTER TABLE child DROP FOREIGN KEY fk"
                    "CREATE DATABASE fk_alter_other"
                    "CREATE TABLE fk_alter_other.external_child(a INT,CONSTRAINT external_fk FOREIGN KEY(a) REFERENCES fk_alter_probe.parent(n))"
                    "ALTER TABLE parent DROP COLUMN n" ],
                  [  ]
            ]
        { Name = "foreign-key-alter-definitions"
          Setup = [| "CREATE DATABASE fk_alter_probe" |]
          Steps = cases |> List.map (fun (name, error, statements, teardown) -> name, error, reset @ statements @ observe @ teardown) |> isolatedScriptSteps
          Cleanup = [| "SET foreign_key_checks=0"; "DROP DATABASE IF EXISTS fk_alter_other"; "DROP DATABASE IF EXISTS fk_alter_probe"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private foreignKeyColumnStorage =
        { Name = "foreign-key-column-storage"
          Setup = [| "CREATE DATABASE fk_column_storage_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_column_storage_probe"
               Contract.execute "create-binary-parent" "CREATE TABLE binary_parent(x BINARY(1) NOT NULL UNIQUE)"
               Contract.execute "create-bit-child" "CREATE TABLE bit_child(x BIT(9),CONSTRAINT fk_bit_child FOREIGN KEY(x) REFERENCES binary_parent(x))"
               Contract.execute "reject-bit-width-change" "ALTER TABLE bit_child MODIFY COLUMN x BIT(16)" |> Contract.fails 1832 "HY000"
               Contract.execute "create-bit-parent" "CREATE TABLE bit_parent(x BIT(8) NOT NULL UNIQUE)"
               Contract.execute "create-binary-child" "CREATE TABLE binary_child(x VARBINARY(2),CONSTRAINT fk_binary_child FOREIGN KEY(x) REFERENCES bit_parent(x))"
               Contract.execute "reject-parent-width-change" "ALTER TABLE bit_parent MODIFY COLUMN x BIT(9) NOT NULL UNIQUE" |> Contract.fails 1833 "HY000"
               Contract.execute "create-enum-parent" "CREATE TABLE enum_parent(x ENUM('a','b') NOT NULL UNIQUE)"
               Contract.execute "create-set-child" "CREATE TABLE set_child(x SET('a','b'),CONSTRAINT fk_set_child FOREIGN KEY(x) REFERENCES enum_parent(x))"
               Contract.execute "extend-set-members" "ALTER TABLE set_child MODIFY COLUMN x SET('a','b','c')"
               Contract.execute "reject-set-member-replacement" "ALTER TABLE set_child MODIFY COLUMN x SET('a','c')" |> Contract.fails 1832 "HY000"
               Contract.execute "create-new-bit-child" "CREATE TABLE new_bit_child(x BIT(8))"
               Contract.execute "add-fk-with-width-change" "ALTER TABLE new_bit_child MODIFY COLUMN x BIT(9),ADD CONSTRAINT fk_new_bit FOREIGN KEY(x) REFERENCES bit_parent(x)"
               Contract.execute "create-null-action-parent" "CREATE TABLE null_action_parent(id INT PRIMARY KEY)"
               Contract.execute "create-delete-null-child" "CREATE TABLE delete_null_child(x INT,CONSTRAINT fk_delete_null FOREIGN KEY(x) REFERENCES null_action_parent(id) ON DELETE SET NULL)"
               Contract.execute "reject-delete-null-not-null" "ALTER TABLE delete_null_child MODIFY x INT NOT NULL" |> Contract.fails 1830 "HY000"
               Contract.execute "reject-delete-null-width-and-nullability" "ALTER TABLE delete_null_child MODIFY x BIGINT NOT NULL" |> Contract.fails 1830 "HY000"
               Contract.execute "drop-delete-null-and-require" "ALTER TABLE delete_null_child DROP FOREIGN KEY fk_delete_null,MODIFY x INT NOT NULL"
               Contract.execute "reject-add-delete-null" "ALTER TABLE delete_null_child ADD CONSTRAINT fk_delete_null FOREIGN KEY(x) REFERENCES null_action_parent(id) ON DELETE SET NULL" |> Contract.fails 1830 "HY000"
               Contract.execute "create-update-null-child" "CREATE TABLE update_null_child(x INT,CONSTRAINT fk_update_null FOREIGN KEY(x) REFERENCES null_action_parent(id) ON UPDATE SET NULL)"
               Contract.execute "reject-update-null-not-null" "ALTER TABLE update_null_child MODIFY x INT NOT NULL" |> Contract.fails 1830 "HY000" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_column_storage_probe" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private yearColumnValues =
        { Name = "year-column-values"
          Setup = [| "CREATE TABLE year_value_input(id INT PRIMARY KEY,y YEAR)" |]
          Steps =
            [| Contract.execute "insert-numeric-years" "INSERT INTO year_value_input VALUES(1,0),(2,24),(3,70),(4,2024)"
               Contract.execute "insert-string-years" "INSERT INTO year_value_input VALUES(5,'0'),(6,'0000'),(8,'000'),(9,'00000')"
               Contract.query "normalized-years" "SELECT id,y+0 FROM year_value_input ORDER BY id"
               Contract.query "display-years" "SELECT id,y FROM year_value_input ORDER BY id"
               Contract.execute "reject-out-of-range-year" "INSERT INTO year_value_input VALUES(7,100)" |> Contract.fails 1264 "22003"
               Contract.execute "reject-truncated-year" "INSERT INTO year_value_input VALUES(10,'24x')" |> Contract.fails 1265 "01000"
               Contract.query "unchanged-after-error" "SELECT COUNT(*) FROM year_value_input"
               Contract.execute "disable-strict-year-mode" "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"
               Contract.execute "insert-truncated-year" "INSERT INTO year_value_input VALUES(10,'24x'),(11,'000x')"
               Contract.query "truncated-year-warnings" "SHOW WARNINGS"
               Contract.query "truncated-year-values" "SELECT id,y+0 FROM year_value_input WHERE id IN (10,11) ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS year_value_input" |]
          Coverage = [| "statement:insert", [| "text-differential" |] |] }

    let private enumSetForeignKeyBytes =
        { Name = "enum-set-foreign-key-bytes"
          Setup = [| "CREATE DATABASE fk_enum_set_bytes_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_enum_set_bytes_probe"
               for index, parentType, childType in
                   [ 1, "ENUM('a','b')", "ENUM('x','y')"
                     2, "ENUM('a','b')", "SET('x','y')"
                     3, "SET('a','b')", "ENUM('x','y')"
                     4, "SET('a','b')", "SET('x','y')" ] do
                   let parent = sprintf "enum_parent_%d" index
                   let child = sprintf "enum_child_%d" index
                   Contract.execute (sprintf "create-parent-%d" index) (sprintf "CREATE TABLE %s(x %s PRIMARY KEY)" parent parentType)
                   Contract.execute (sprintf "create-child-%d" index) (sprintf "CREATE TABLE %s(x %s,CONSTRAINT fk_enum_%d FOREIGN KEY(x) REFERENCES %s(x) ON UPDATE CASCADE ON DELETE CASCADE)" child childType index parent)
                   Contract.execute (sprintf "insert-parent-%d" index) (sprintf "INSERT INTO %s VALUES('a')" parent)
                   Contract.execute (sprintf "insert-child-%d" index) (sprintf "INSERT INTO %s VALUES('x')" child)
                   Contract.execute (sprintf "reject-other-byte-%d" index) (sprintf "INSERT INTO %s VALUES('y')" child) |> Contract.fails 1452 "23000"
                   Contract.execute (sprintf "cascade-update-%d" index) (sprintf "UPDATE %s SET x='b' WHERE x='a'" parent)
                   Contract.query (sprintf "child-after-update-%d" index) (sprintf "SELECT x FROM %s" child)
                   Contract.execute (sprintf "cascade-delete-%d" index) (sprintf "DELETE FROM %s WHERE x='b'" parent)
                   Contract.query (sprintf "child-after-delete-%d" index) (sprintf "SELECT COUNT(*) FROM %s" child)
               Contract.execute "create-wide-set-parent" "CREATE TABLE wide_set_parent(x SET('a','b') PRIMARY KEY)"
               Contract.execute "create-short-enum-child" "CREATE TABLE short_enum_child(x ENUM('x','y'),CONSTRAINT fk_wide_set FOREIGN KEY(x) REFERENCES wide_set_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-wide-set-parent" "INSERT INTO wide_set_parent VALUES('a')"
               Contract.execute "insert-short-enum-child" "INSERT INTO short_enum_child VALUES('x')"
               Contract.execute "cascade-out-of-domain-ordinal" "UPDATE wide_set_parent SET x='a,b' WHERE x='a'"
               Contract.query "out-of-domain-ordinal" "SELECT x,x+0 FROM short_enum_child"
               Contract.query "out-of-domain-empty-label-scan" "SELECT COUNT(*) FROM short_enum_child IGNORE INDEX(fk_wide_set) WHERE x=''"
               Contract.query "out-of-domain-numeric-ordinal-scan" "SELECT COUNT(*) FROM short_enum_child IGNORE INDEX(fk_wide_set) WHERE x=3"
               Contract.query "out-of-domain-quoted-ordinal-scan" "SELECT COUNT(*) FROM short_enum_child IGNORE INDEX(fk_wide_set) WHERE x='3'"
               Contract.execute "delete-out-of-domain-parent" "DELETE FROM wide_set_parent WHERE x='a,b'"
               Contract.query "out-of-domain-child-deleted" "SELECT COUNT(*) FROM short_enum_child" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_enum_set_bytes_probe" |]
          Coverage = [| "statement:foreign-key", [| "text-differential" |] |] }

    let private bitBinaryForeignKeyBytes =
        { Name = "bit-binary-foreign-key-bytes"
          Setup = [| "CREATE DATABASE fk_bit_binary_bytes_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_bit_binary_bytes_probe"
               Contract.execute "create-bit-parent" "CREATE TABLE bit_parent(x BIT(9) PRIMARY KEY)"
               Contract.execute "create-binary-child" "CREATE TABLE binary_child(x VARBINARY(2),CONSTRAINT fk_binary FOREIGN KEY(x) REFERENCES bit_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "create-short-child" "CREATE TABLE short_child(x VARBINARY(1),CONSTRAINT fk_short FOREIGN KEY(x) REFERENCES bit_parent(x))"
               Contract.execute "insert-bit-parent" "INSERT INTO bit_parent VALUES(b'000000001')"
               Contract.query "bit-nine-column-hex" "SELECT HEX(x) FROM bit_parent"
               Contract.execute "insert-equal-bytes" "INSERT INTO binary_child VALUES(X'0001')"
               Contract.execute "reject-shorter-bytes" "INSERT INTO short_child VALUES(X'01')" |> Contract.fails 1452 "23000"
               Contract.execute "cascade-bit-update" "UPDATE bit_parent SET x=b'000000010' WHERE x=b'000000001'"
               Contract.query "binary-child-after-update" "SELECT HEX(x) FROM binary_child"
               Contract.execute "create-binary-parent" "CREATE TABLE binary_parent(x BINARY(2) PRIMARY KEY)"
               Contract.execute "create-bit-child" "CREATE TABLE bit_child(x BIT(9),CONSTRAINT fk_bit FOREIGN KEY(x) REFERENCES binary_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-binary-parent" "INSERT INTO binary_parent VALUES(X'0001')"
               Contract.execute "insert-bit-child" "INSERT INTO bit_child VALUES(b'000000001')"
               Contract.execute "cascade-binary-update" "UPDATE binary_parent SET x=X'0002' WHERE x=X'0001'"
               Contract.query "bit-child-after-update" "SELECT x+0 FROM bit_child"
               Contract.execute "cascade-binary-delete" "DELETE FROM binary_parent WHERE x=X'0002'"
               Contract.query "bit-child-after-delete" "SELECT COUNT(*) FROM bit_child"
               Contract.execute "create-one-bit-parent" "CREATE TABLE one_bit_parent(x BIT(1) PRIMARY KEY)"
               Contract.execute "create-eight-bit-child" "CREATE TABLE eight_bit_child(x BIT(8),CONSTRAINT fk_bit_width FOREIGN KEY(x) REFERENCES one_bit_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-one-bit-parent" "INSERT INTO one_bit_parent VALUES(b'1')"
               Contract.execute "insert-eight-bit-child" "INSERT INTO eight_bit_child VALUES(b'00000001')"
               Contract.execute "cascade-one-bit-update" "UPDATE one_bit_parent SET x=b'0' WHERE x=b'1'"
               Contract.query "eight-bit-child-after-update" "SELECT x+0 FROM eight_bit_child"
               Contract.execute "cascade-one-bit-delete" "DELETE FROM one_bit_parent WHERE x=b'0'"
               Contract.query "eight-bit-child-after-delete" "SELECT COUNT(*) FROM eight_bit_child"
               Contract.execute "create-reverse-eight-bit-parent" "CREATE TABLE reverse_eight_bit_parent(x BIT(8) PRIMARY KEY)"
               Contract.execute "create-one-bit-child" "CREATE TABLE one_bit_child(x BIT(1),CONSTRAINT fk_bit_reverse FOREIGN KEY(x) REFERENCES reverse_eight_bit_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-reverse-eight-bit-parent" "INSERT INTO reverse_eight_bit_parent VALUES(b'00000001')"
               Contract.execute "insert-one-bit-child" "INSERT INTO one_bit_child VALUES(b'1')"
               Contract.query "one-bit-column-hex" "SELECT HEX(x) FROM one_bit_child"
               Contract.query "bit-literal-hex" "SELECT HEX(b'00000001'),HEX(b'000000001')"
               Contract.execute "cascade-reverse-eight-bit-update" "UPDATE reverse_eight_bit_parent SET x=b'00000000' WHERE x=b'00000001'"
               Contract.query "one-bit-child-after-update" "SELECT x+0 FROM one_bit_child"
               Contract.execute "cascade-reverse-eight-bit-delete" "DELETE FROM reverse_eight_bit_parent WHERE x=b'00000000'"
               Contract.query "one-bit-child-after-delete" "SELECT COUNT(*) FROM one_bit_child"
               Contract.execute "reinsert-reverse-eight-bit-parent" "INSERT INTO reverse_eight_bit_parent VALUES(b'00000001')"
               Contract.execute "reinsert-one-bit-child" "INSERT INTO one_bit_child VALUES(b'1')"
               Contract.execute "cascade-out-of-range-bit-update" "UPDATE reverse_eight_bit_parent SET x=b'00000010' WHERE x=b'00000001'"
               Contract.query "out-of-range-bit-child" "SELECT x+0,HEX(x) FROM one_bit_child"
               Contract.execute "cascade-out-of-range-bit-delete" "DELETE FROM reverse_eight_bit_parent WHERE x=b'00000010'"
               Contract.query "out-of-range-bit-child-after-delete" "SELECT COUNT(*) FROM one_bit_child"
               Contract.execute "create-eight-bit-parent" "CREATE TABLE eight_bit_parent(x BIT(8) PRIMARY KEY)"
               Contract.execute "create-nine-bit-child" "CREATE TABLE nine_bit_child(x BIT(9),CONSTRAINT fk_bit_bytes FOREIGN KEY(x) REFERENCES eight_bit_parent(x))"
               Contract.execute "insert-eight-bit-parent" "INSERT INTO eight_bit_parent VALUES(b'00000001')"
               Contract.execute "reject-different-byte-length" "INSERT INTO nine_bit_child VALUES(b'000000001')" |> Contract.fails 1452 "23000"
               Contract.query "nine-bit-child-after-rejection" "SELECT COUNT(*) FROM nine_bit_child" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_bit_binary_bytes_probe" |]
          Coverage = [| "statement:foreign-key", [| "text-differential" |] |] }

    let private yearByteForeignKeys =
        { Name = "year-byte-foreign-keys"
          Setup = [| "CREATE DATABASE fk_year_byte_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_year_byte_probe"
               Contract.execute "create-year-parent" "CREATE TABLE year_parent(x YEAR PRIMARY KEY)"
               Contract.execute "create-tiny-child" "CREATE TABLE tiny_child(x TINYINT UNSIGNED,CONSTRAINT fk_year_tiny FOREIGN KEY(x) REFERENCES year_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-year-parent" "INSERT INTO year_parent VALUES(2024),(0)"
               Contract.execute "insert-tiny-child" "INSERT INTO tiny_child VALUES(124),(0)"
               Contract.execute "update-year-parent" "UPDATE year_parent SET x=2025 WHERE x=2024"
               Contract.query "tiny-child-after-update" "SELECT x FROM tiny_child ORDER BY x"
               Contract.execute "delete-zero-year" "DELETE FROM year_parent WHERE x=0"
               Contract.query "tiny-child-after-delete" "SELECT x FROM tiny_child ORDER BY x"
               Contract.execute "create-tiny-parent" "CREATE TABLE tiny_parent(x TINYINT UNSIGNED PRIMARY KEY)"
               Contract.execute "create-year-child" "CREATE TABLE year_child(x YEAR,CONSTRAINT fk_tiny_year FOREIGN KEY(x) REFERENCES tiny_parent(x) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-tiny-parent" "INSERT INTO tiny_parent VALUES(124),(0)"
               Contract.execute "insert-year-child" "INSERT INTO year_child VALUES(2024),(0)"
               Contract.execute "update-tiny-parent" "UPDATE tiny_parent SET x=125 WHERE x=124"
               Contract.query "year-child-after-update" "SELECT x FROM year_child ORDER BY x"
               Contract.execute "delete-zero-tiny" "DELETE FROM tiny_parent WHERE x=0"
               Contract.query "year-child-after-delete" "SELECT x FROM year_child ORDER BY x"
               Contract.execute "reject-signed-tiny-child" "CREATE TABLE signed_tiny_child(x TINYINT,CONSTRAINT fk_signed FOREIGN KEY(x) REFERENCES year_parent(x))" |> Contract.fails 3780 "HY000" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_year_byte_probe" |]
          Coverage = [| "statement:foreign-key", [| "text-differential" |] |] }

    let private timeDateForeignKeys =
        { Name = "time-date-foreign-keys"
          Setup = [| "CREATE DATABASE fk_time_date_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_time_date_probe"
               for index, parentType, childType, parentValue, childValue in
                   [ 1, "DATETIME", "TIME", "2024-01-01 12:34:56", "12:34:56"
                     2, "TIME", "DATETIME", "12:34:56", "2024-01-01 12:34:56"
                     3, "TIMESTAMP(2)", "TIME(4)", "2024-01-01 12:34:56.12", "12:34:56.1200"
                     4, "TIME(4)", "TIMESTAMP(2)", "12:34:56.1200", "2024-01-01 12:34:56.12"
                     5, "DATETIME", "TIMESTAMP", "2024-01-01 12:34:56", "2024-01-01 12:34:56"
                     6, "TIMESTAMP", "DATETIME", "2024-01-01 12:34:56", "2024-01-01 12:34:56"
                     7, "DATETIME", "TIMESTAMP", "0000-00-00 00:00:00", "0000-00-00 00:00:00"
                     8, "TIMESTAMP", "DATETIME", "0000-00-00 00:00:00", "0000-00-00 00:00:00" ] do
                   let parent = sprintf "time_parent_%d" index
                   let child = sprintf "time_child_%d" index
                   let foreignKey = sprintf "fk_time_%d" index
                   if index = 7 then
                       Contract.execute "allow-zero-dates" "SET SESSION sql_mode='NO_ENGINE_SUBSTITUTION'"
                   Contract.execute (sprintf "create-parent-%d" index) (sprintf "CREATE TABLE %s(x %s PRIMARY KEY)" parent parentType)
                   Contract.execute (sprintf "create-child-%d" index) (sprintf "CREATE TABLE %s(x %s,CONSTRAINT %s FOREIGN KEY(x) REFERENCES %s(x))" child childType foreignKey parent)
                   Contract.execute (sprintf "insert-parent-%d" index) (sprintf "INSERT INTO %s VALUES('%s')" parent parentValue)
                   Contract.execute (sprintf "reject-child-%d" index) (sprintf "INSERT INTO %s VALUES('%s')" child childValue) |> Contract.fails 1452 "23000"
                   Contract.query (sprintf "empty-child-%d" index) (sprintf "SELECT COUNT(*) FROM %s" child)
               Contract.execute "restore-sql-mode" "SET SESSION sql_mode=DEFAULT"
               for kind, baseValue in
                   [ "TIME", "12:34:56"
                     "DATETIME", "2024-01-02 12:34:56"
                     "TIMESTAMP", "2024-01-02 12:34:56" ] do
                   for parentPrecision, childPrecision, matches in
                       [ 0, 0, true
                         0, 1, false
                         1, 2, true
                         2, 1, true
                         2, 3, false
                         3, 4, true
                         5, 6, true ] do
                       let label = sprintf "%s-%d-%d" kind parentPrecision childPrecision
                       let parent = sprintf "fraction_parent_%s_%d_%d" kind parentPrecision childPrecision
                       let child = sprintf "fraction_child_%s_%d_%d" kind parentPrecision childPrecision
                       let foreignKey = sprintf "fk_fraction_%s_%d_%d" kind parentPrecision childPrecision
                       let value = baseValue + (if parentPrecision = 0 then ".000000" else ".100000")
                       Contract.execute (label + "-create-parent") (sprintf "CREATE TABLE %s(x %s(%d) PRIMARY KEY)" parent kind parentPrecision)
                       Contract.execute (label + "-create-child") (sprintf "CREATE TABLE %s(x %s(%d),CONSTRAINT %s FOREIGN KEY(x) REFERENCES %s(x))" child kind childPrecision foreignKey parent)
                       Contract.execute (label + "-insert-parent") (sprintf "INSERT INTO %s VALUES('%s')" parent value)
                       let insertion = Contract.execute (label + "-insert-child") (sprintf "INSERT INTO %s VALUES('%s')" child value)
                       if matches then insertion else insertion |> Contract.fails 1452 "23000"
                       Contract.query (label + "-child-count") (sprintf "SELECT COUNT(*) FROM %s" child) |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_time_date_probe" |]
          Coverage = [| "statement:foreign-key", [| "text-differential" |] |] }

    let private decimalForeignKeyBytes =
        let cases =
            [ "DECIMAL(5,2)", "DECIMAL(5,2)", "1.20", "1.20", true
              "DECIMAL(5,2)", "DECIMAL(6,2)", "1.20", "1.20", true
              "DECIMAL(5,2)", "DECIMAL(7,2)", "1.20", "1.20", false
              "DECIMAL(5,2)", "DECIMAL(7,2)", "0.00", "0.00", false
              "DECIMAL(5,2)", "DECIMAL(5,1)", "1.00", "1.0", true
              "DECIMAL(5,2)", "DECIMAL(5,1)", "1.02", "1.2", true
              "DECIMAL(5,2)", "DECIMAL(5,1)", "-1.02", "-1.2", true
              "DECIMAL(5,2)", "DECIMAL(5,1)", "1.20", "1.2", false
              "DECIMAL(5,2)", "DECIMAL(5,3)", "1.00", "1.000", false
              "DECIMAL(14,4)", "DECIMAL(15,4)", "1234567890.1234", "1234567890.1234", true
              "DECIMAL(14,4)", "DECIMAL(16,4)", "1234567890.1234", "1234567890.1234", false
              "DECIMAL(14,4)", "DECIMAL(14,3)", "1234567890.0123", "1234567890.123", true
              "DECIMAL(14,4)", "DECIMAL(14,3)", "-1234567890.0123", "-1234567890.123", true ]
        { Name = "decimal-foreign-key-bytes"
          Setup = [| "CREATE DATABASE fk_decimal_bytes_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_decimal_bytes_probe"
               for index, (parentType, childType, parentValue, childValue, matches) in List.indexed cases do
                   let parent = sprintf "decimal_parent_%d" index
                   let child = sprintf "decimal_child_%d" index
                   let foreignKey = sprintf "fk_decimal_%d" index
                   Contract.execute (sprintf "create-parent-%d" index) (sprintf "CREATE TABLE %s(k %s PRIMARY KEY)" parent parentType)
                   Contract.execute (sprintf "create-child-%d" index) (sprintf "CREATE TABLE %s(k %s,CONSTRAINT %s FOREIGN KEY(k) REFERENCES %s(k))" child childType foreignKey parent)
                   Contract.execute (sprintf "insert-parent-%d" index) (sprintf "INSERT INTO %s VALUES(%s)" parent parentValue)
                   let insertion = Contract.execute (sprintf "insert-child-%d" index) (sprintf "INSERT INTO %s VALUES(%s)" child childValue)
                   if matches then insertion else insertion |> Contract.fails 1452 "23000"
                   Contract.query (sprintf "child-rows-%d" index) (sprintf "SELECT k FROM %s" child)
               Contract.execute "create-cascade-parent" "CREATE TABLE decimal_cascade_parent(k DECIMAL(5,2) PRIMARY KEY)"
               Contract.execute "create-cascade-child" "CREATE TABLE decimal_cascade_child(k DECIMAL(5,1),CONSTRAINT fk_decimal_cascade FOREIGN KEY(k) REFERENCES decimal_cascade_parent(k) ON UPDATE CASCADE ON DELETE CASCADE)"
               Contract.execute "insert-cascade-parent" "INSERT INTO decimal_cascade_parent VALUES(1.02)"
               Contract.execute "insert-cascade-child" "INSERT INTO decimal_cascade_child VALUES(1.2)"
               Contract.execute "update-cascade-parent" "UPDATE decimal_cascade_parent SET k=1.03 WHERE k=1.02"
               Contract.query "child-after-update" "SELECT k FROM decimal_cascade_child"
               Contract.execute "delete-cascade-parent" "DELETE FROM decimal_cascade_parent WHERE k=1.03"
               Contract.query "child-after-delete" "SELECT COUNT(*) FROM decimal_cascade_child" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_decimal_bytes_probe" |]
          Coverage = [| "statement:foreign-key", [| "text-differential" |] |] }

    let private foreignKeyGeneratedActions =
        { Name = "foreign-key-generated-actions"
          Setup = [| "CREATE DATABASE fk_generated_action_probe" |]
          Steps =
            [| Contract.execute "select-database" "USE fk_generated_action_probe"
               Contract.execute "create-parent" "CREATE TABLE parent(id INT PRIMARY KEY)"
               Contract.execute "reject-update-cascade" "CREATE TABLE child(id INT,g INT AS(id+1) STORED,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON UPDATE CASCADE)" |> Contract.fails 3104 "HY000"
               Contract.execute "reject-delete-set-null" "CREATE TABLE child(id INT,g INT AS(id+1) STORED,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON DELETE SET NULL)" |> Contract.fails 3104 "HY000"
               Contract.execute "reject-update-set-null" "CREATE TABLE child(id INT,g INT AS(id+1) STORED,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON UPDATE SET NULL)" |> Contract.fails 3104 "HY000"
               Contract.execute "reject-virtual-action-first" "CREATE TABLE child(id INT,g INT AS(id+1) VIRTUAL,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON UPDATE CASCADE)" |> Contract.fails 3104 "HY000"
               Contract.execute "create-child-cascade" "CREATE TABLE child(id INT,g INT AS(id+1) STORED,KEY(g),CONSTRAINT fk FOREIGN KEY(g) REFERENCES parent(id) ON DELETE CASCADE)"
               Contract.execute "insert-parent" "INSERT INTO parent VALUES(2)"
               Contract.execute "insert-child" "INSERT INTO child(id) VALUES(1)"
               Contract.execute "cascade-delete" "DELETE FROM parent WHERE id=2"
               Contract.query "remaining-children" "SELECT COUNT(*) FROM child"
               Contract.execute "create-generated-parent" "CREATE TABLE generated_parent(id INT PRIMARY KEY,g INT AS(id+1) STORED,UNIQUE KEY(g))"
               Contract.execute "create-ordinary-child" "CREATE TABLE ordinary_child(x INT,CONSTRAINT fk_ordinary FOREIGN KEY(x) REFERENCES generated_parent(g) ON UPDATE CASCADE)" |]
          Cleanup = [| "DROP DATABASE IF EXISTS fk_generated_action_probe" |]
          Coverage = [| "statement:create-table", [| "text-differential" |] |] }

    let private foreignKeyNames =
        let cases =
            [
              "unnamed", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n),FOREIGN KEY(b) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "explicit-sequence", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,CONSTRAINT child_ibfk_7 FOREIGN KEY(a) REFERENCES parent(n),FOREIGN KEY(b) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "alter", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME"
                    "ALTER TABLE child DROP FOREIGN KEY child_ibfk_2"
                    "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "drop-all", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child DROP FOREIGN KEY child_ibfk_1"
                    "ALTER TABLE child ADD FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "rename", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "RENAME TABLE child TO renamed"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME"
                    "ALTER TABLE renamed ADD FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "alter-explicit-sequence", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,CONSTRAINT child_ibfk_7 FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "alter-explicit-batch", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT)"
                    "ALTER TABLE child ADD CONSTRAINT child_ibfk_7 FOREIGN KEY(a) REFERENCES parent(n),ADD FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "alter-drop-add", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child DROP FOREIGN KEY child_ibfk_1,ADD FOREIGN KEY(a) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "leading-zero", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(a INT,b INT,CONSTRAINT child_ibfk_07 FOREIGN KEY(a) REFERENCES parent(n))"
                    "ALTER TABLE child ADD FOREIGN KEY(b) REFERENCES parent(n)"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
              "case-folding", None,
                  [ "USE fk_names_probe"
                    "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,other,renamed"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE Child(a INT,FOREIGN KEY(a) REFERENCES parent(n))"
                    "SELECT CONSTRAINT_NAME,TABLE_NAME FROM information_schema.REFERENTIAL_CONSTRAINTS WHERE CONSTRAINT_SCHEMA='fk_names_probe' ORDER BY TABLE_NAME,CONSTRAINT_NAME" ]
            ]
        { Name = "foreign-key-names"
          Setup = [| "CREATE DATABASE fk_names_probe" |]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "DROP DATABASE IF EXISTS fk_names_probe" |]
          Coverage = [| "statement:create-table", [| "text-differential" |]; "statement:alter-table", [| "text-differential" |] |] }

    let private updateIgnoreConstraints =
        let cases =
            [
              "mixed", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                    "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)"
                    "UPDATE IGNORE child SET n=id ORDER BY id"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id"
                    "SELECT * FROM audit" ]
              "reverse", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                    "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)"
                    "UPDATE IGNORE child SET n=id ORDER BY id DESC"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id"
                    "SELECT * FROM audit" ]
              "limit", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                    "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)"
                    "UPDATE IGNORE child SET n=2 ORDER BY id LIMIT 2"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id"
                    "SELECT * FROM audit" ]
              "joined", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                    "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)"
                    "UPDATE IGNORE child JOIN parent ON parent.n=child.n SET child.n=child.id"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id"
                    "SELECT * FROM audit" ]
              "parent", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "UPDATE IGNORE parent SET n=n+10"
                    "SHOW WARNINGS"
                    "SELECT * FROM parent ORDER BY n"
                    "SELECT * FROM child ORDER BY id" ]
              "duplicate", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TABLE audit(phase VARCHAR(10),id INT)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('before',OLD.id)"
                    "CREATE TRIGGER au AFTER UPDATE ON child FOR EACH ROW INSERT INTO audit VALUES('after',NEW.id)"
                    "UPDATE IGNORE child SET id=1"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id"
                    "SELECT * FROM audit" ]
              "trigger-error", Some(8, 1452, "23000"),
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "CREATE TRIGGER bu BEFORE UPDATE ON child FOR EACH ROW SIGNAL SQLSTATE '23000' SET MYSQL_ERRNO=1452,MESSAGE_TEXT='trigger failure'"
                    "UPDATE IGNORE child SET n=3"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "mixed-no-triggers", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "UPDATE IGNORE child SET n=id ORDER BY id"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "single-evaluation", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "SET @calls=0"
                    "UPDATE IGNORE child SET n=(@calls:=@calls+1) ORDER BY id"
                    "SHOW WARNINGS"
                    "SELECT @calls"
                    "SELECT * FROM child ORDER BY id" ]
              "duplicate-no-triggers", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,audit"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1),(3)"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
                    "UPDATE IGNORE child SET id=1"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
            ]
        { Name = "update-ignore-constraints"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "SET foreign_key_checks=0"; "DROP TABLE IF EXISTS child,parent,audit"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:update", [| "text-differential" |]; "statement:create-trigger", [| "text-differential" |] |] }

    let private routineUpdateWrites =
        let setup functionSql =
            [ "SET foreign_key_checks=0"
              "DROP TABLE IF EXISTS child,parent"
              "SET foreign_key_checks=1"
              "DROP FUNCTION IF EXISTS promote_parent"
              "CREATE TABLE parent(n INT PRIMARY KEY)"
              "INSERT INTO parent VALUES(1),(2)"
              "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
              "INSERT INTO child VALUES(1,1),(2,1),(3,1)"
              functionSql ]
        let successful = setup "CREATE FUNCTION promote_parent() RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN UPDATE parent SET n=3 WHERE n=2; RETURN 3; END"
        let failing = setup "CREATE FUNCTION promote_parent(input_id INT) RETURNS INT DETERMINISTIC MODIFIES SQL DATA BEGIN UPDATE parent SET n=3 WHERE n=2; IF input_id=2 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='stop'; END IF; RETURN 3; END"
        let observe = [ "SHOW WARNINGS"; "SELECT * FROM child ORDER BY id"; "SELECT * FROM parent ORDER BY n" ]
        let scenario name preparation statement error finish =
            let expectedError = error |> Option.map (fun (code, state) -> List.length preparation, code, state)
            name, expectedError, preparation @ [ statement ] @ observe @ finish
        let cases =
            [ for name, statement in
                  [ "plain", "UPDATE child SET n=promote_parent()"
                    "ignore", "UPDATE IGNORE child SET n=promote_parent()"
                    "predicate", "UPDATE child SET n=3 WHERE promote_parent()=3"
                    "predicate-no-match", "UPDATE child SET n=3 WHERE promote_parent()=0"
                    "joined-independent", "UPDATE child JOIN (SELECT 1 AS seed) AS one ON 1=1 SET child.n=promote_parent()"
                    "join-predicate", "UPDATE child JOIN (SELECT 1 AS seed) AS one ON promote_parent()=3 SET child.n=3" ] do
                  yield scenario name successful statement None []
              yield scenario "protected-parent" successful
                  "UPDATE child JOIN parent ON child.n=parent.n SET child.n=promote_parent()"
                  (Some(1442, "HY000")) []
              for isolation in [ "REPEATABLE READ"; "READ COMMITTED"; "READ UNCOMMITTED"; "SERIALIZABLE" ] do
                  for finish in [ "COMMIT"; "ROLLBACK" ] do
                      let preparation = successful @ [ "SET SESSION TRANSACTION ISOLATION LEVEL " + isolation; "START TRANSACTION" ]
                      yield scenario (isolation + "-" + finish) preparation
                          "UPDATE IGNORE child SET n=promote_parent()" None ([ finish ] @ observe)
              for name, statement in
                  [ "fatal-plain", "UPDATE child SET n=promote_parent(id) ORDER BY id"
                    "fatal-ignore", "UPDATE IGNORE child SET n=promote_parent(id) ORDER BY id"
                    "fatal-predicate", "UPDATE child SET n=3 WHERE promote_parent(id)=3"
                    "fatal-joined", "UPDATE child JOIN (SELECT 1 AS seed) AS one ON 1=1 SET child.n=promote_parent(id)" ] do
                  yield scenario name failing statement (Some(1644, "45000")) []
              yield scenario "fatal-join-predicate-transaction" (failing @ [ "START TRANSACTION" ])
                  "UPDATE child JOIN (SELECT 1 AS seed) AS one ON promote_parent(child.id)=3 SET child.n=3"
                  (Some(1644, "45000")) ([ "COMMIT" ] @ observe) ]
        { Name = "routine-update-writes"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "DROP FUNCTION IF EXISTS promote_parent"; "SET foreign_key_checks=0"; "DROP TABLE IF EXISTS child,parent"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:update", [| "text-differential" |] |] }

    let private foreignKeyRowValidation =
        let cases =
            [
              "insert", Some(7, 1452, "23000"),
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1)"
                    "INSERT INTO child VALUES(2,2)"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "missing-parent", Some(10, 1452, "23000"),
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1)"
                    "SET foreign_key_checks=0"
                    "DROP TABLE parent"
                    "SET foreign_key_checks=1"
                    "INSERT INTO child VALUES(2,2)"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "missing-parent-null", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1)"
                    "SET foreign_key_checks=0"
                    "DROP TABLE parent"
                    "SET foreign_key_checks=1"
                    "INSERT INTO child VALUES(2,NULL)"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "missing-parent-nonkey", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1)"
                    "ALTER TABLE child ADD payload INT"
                    "SET foreign_key_checks=0"
                    "DROP TABLE parent"
                    "SET foreign_key_checks=1"
                    "UPDATE child SET payload=7"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY id" ]
              "orphan-covering-False", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,payload INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1,0)"
                    "SET foreign_key_checks=0"
                    "DELETE FROM parent"
                    "SET foreign_key_checks=1"
                    "UPDATE child SET payload=7"
                    "SHOW WARNINGS"
                    "SELECT * FROM child" ]
              "orphan-covering-True", Some(10, 1452, "23000"),
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,payload INT,KEY cover(n,payload),CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1,0)"
                    "SET foreign_key_checks=0"
                    "DELETE FROM parent"
                    "SET foreign_key_checks=1"
                    "UPDATE child SET payload=7"
                    "SHOW WARNINGS"
                    "SELECT * FROM child" ]
              "orphan-noop", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE parent(n INT PRIMARY KEY)"
                    "CREATE TABLE child(id INT PRIMARY KEY,n INT,CONSTRAINT Fk_Child FOREIGN KEY(n) REFERENCES parent(n))"
                    "INSERT INTO parent VALUES(1)"
                    "INSERT INTO child VALUES(1,1)"
                    "SET foreign_key_checks=0"
                    "DELETE FROM parent"
                    "SET foreign_key_checks=1"
                    "UPDATE child SET n=n"
                    "SHOW WARNINGS"
                    "SELECT * FROM child" ]
              "self-multi", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                    "INSERT INTO child VALUES(1,1),(2,1),(3,3)"
                    "SELECT * FROM child ORDER BY n" ]
              "self-ignore", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                    "INSERT IGNORE INTO child VALUES(1,2),(2,1),(3,3)"
                    "SHOW WARNINGS"
                    "SELECT * FROM child ORDER BY n" ]
              "self-replace", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                    "REPLACE INTO child VALUES(1,1)"
                    "SELECT * FROM child" ]
              "self-upsert", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                    "INSERT INTO child VALUES(1,1) ON DUPLICATE KEY UPDATE p=VALUES(p)"
                    "SELECT * FROM child" ]
              "self-update", None,
                  [ "SET foreign_key_checks=0"
                    "DROP TABLE IF EXISTS child,parent,`Odd.Child`,`Odd.Parent`"
                    "SET foreign_key_checks=1"
                    "CREATE TABLE child(n INT PRIMARY KEY,p INT,CONSTRAINT fk FOREIGN KEY(p) REFERENCES child(n))"
                    "INSERT INTO child VALUES(1,NULL)"
                    "UPDATE child SET n=2,p=2"
                    "SHOW WARNINGS"
                    "SELECT * FROM child" ]
            ]
        { Name = "foreign-key-row-validation"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "SET foreign_key_checks=0"; "DROP TABLE IF EXISTS child,parent"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:insert", [| "text-differential" |]; "statement:update", [| "text-differential" |]; "statement:replace", [| "text-differential" |] |] }

    let private alterDefaultBinlogSafety =
        let expressionCases =
            [
              "rand", "DOUBLE DEFAULT (RAND())", Some(1674, "HY000")
              "seeded-rand", "DOUBLE DEFAULT (RAND(1))", Some(1674, "HY000")
              "uuid", "VARCHAR(64) DEFAULT (UUID())", Some(1674, "HY000")
              "uuid-short", "BIGINT UNSIGNED DEFAULT (UUID_SHORT())", Some(1674, "HY000")
              "now", "DATETIME DEFAULT (NOW())", None
              "sysdate", "DATETIME DEFAULT (SYSDATE())", Some(1674, "HY000")
              "unix-time", "BIGINT DEFAULT (UNIX_TIMESTAMP())", None
              "connection", "BIGINT DEFAULT (CONNECTION_ID())", None
              "user", "VARCHAR(64) DEFAULT (USER())", Some(1674, "HY000")
              "database", "VARCHAR(64) DEFAULT (DATABASE())", None
              "absolute", "DOUBLE DEFAULT (ABS(-1))", None
              "unreached-rand", "DOUBLE DEFAULT (IF(0,RAND(),1))", Some(1674, "HY000")
            ]
        let cases =
            [
              for name, definition, error in expressionCases do
                  for populated in [ false; true ] do
                      let setup =
                          [ "DROP TABLE IF EXISTS target"
                            "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                            if populated then "INSERT INTO target VALUES(1,1),(2,2)" ]
                      let expectedError = error |> Option.map (fun (code, state) -> List.length setup, code, state)
                      yield sprintf "%s-%O" name populated, expectedError,
                          setup @ [ "ALTER TABLE target ADD COLUMN added " + definition
                                    "SHOW WARNINGS"
                                    "SHOW COLUMNS FROM target" ]
              yield "binlog-off", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "SET sql_log_bin=0"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "binlog-row", Some(5, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "SET binlog_format='ROW'"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "binlog-statement", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "SET binlog_format='STATEMENT'"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "copy", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND()), ALGORITHM=COPY"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "inplace", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND()), ALGORITHM=INPLACE"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "instant", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND()), ALGORITHM=INSTANT"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "lock-none", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND()), LOCK=NONE"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "modify", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target MODIFY v DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "set-default", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ALTER COLUMN v SET DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "set-default-copy", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ALTER COLUMN v SET DEFAULT (RAND()), ALGORITHM=COPY"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "create", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "CREATE TABLE created(id INT PRIMARY KEY,v DOUBLE DEFAULT (RAND()))"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "existing-rand-copy", None,
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ALTER COLUMN v SET DEFAULT (RAND())"
                    "ALTER TABLE target ADD COLUMN added INT, ALGORITHM=COPY"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "missing-table", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE missing ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "duplicate-column", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN v DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "invalid-default", Some(4, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "DROP TABLE IF EXISTS created"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1),(2,2)"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (SLEEP(0)+RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "random-bytes", Some(3, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (HEX(RANDOM_BYTES(4)))"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "version", Some(3, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (VERSION())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "current-user", Some(3, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (CURRENT_USER())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "session-user", Some(3, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (SESSION_USER())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "system-user", Some(3, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (SYSTEM_USER())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "found-rows", Some(3, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (FOUND_ROWS())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "row-count", Some(3, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (ROW_COUNT())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "last-id", Some(3, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN added VARCHAR(64) DEFAULT (LAST_INSERT_ID())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "mixed", Some(4, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "SET binlog_format='MIXED'"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "statement-duplicate", Some(4, 1060, "42S21"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "SET binlog_format='STATEMENT'"
                    "ALTER TABLE target ADD COLUMN v DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "statement-missing", Some(4, 1146, "42S02"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "SET binlog_format='STATEMENT'"
                    "ALTER TABLE missing ADD COLUMN added DOUBLE DEFAULT (RAND())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "statement-inplace", Some(4, 1845, "0A000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "SET binlog_format='STATEMENT'"
                    "ALTER TABLE target ADD COLUMN added DOUBLE DEFAULT (RAND()), ALGORITHM=INPLACE"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "multi-unsafe", Some(3, 1674, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN a DOUBLE DEFAULT (RAND()), ADD COLUMN b VARCHAR(64) DEFAULT (UUID())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "invalid-later", Some(3, 3770, "HY000"),
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "ALTER TABLE target ADD COLUMN a DOUBLE DEFAULT (RAND()), ADD COLUMN b DOUBLE DEFAULT (SLEEP(0))"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
              yield "statement-multi", None,
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DOUBLE)"
                    "INSERT INTO target VALUES(1,1)"
                    "SET binlog_format='STATEMENT'"
                    "ALTER TABLE target ADD COLUMN a DOUBLE DEFAULT (RAND()), ADD COLUMN b VARCHAR(64) DEFAULT (UUID())"
                    "SHOW WARNINGS"
                    "SHOW COLUMNS FROM target" ]
            ]
        { Name = "alter-default-binlog-safety"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "DROP TABLE IF EXISTS target"; "DROP TABLE IF EXISTS created" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private binlogSettings =
        let cases =
            [
              "binlog_format-'ROW'", None,
                  [ "SET binlog_format='ROW'"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-'STATEMENT'", None,
                  [ "SET binlog_format='STATEMENT'"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-'MIXED'", None,
                  [ "SET binlog_format='MIXED'"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-0", None,
                  [ "SET binlog_format=0"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-1", None,
                  [ "SET binlog_format=1"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-2", None,
                  [ "SET binlog_format=2"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-3", Some(0, 1231, "42000"),
                  [ "SET binlog_format=3"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-NULL", Some(0, 1231, "42000"),
                  [ "SET binlog_format=NULL"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "binlog_format-DEFAULT", None,
                  [ "SET binlog_format=DEFAULT"
                    "SHOW WARNINGS"
                    "SELECT @@binlog_format AS value" ]
              "sql_log_bin-0", None,
                  [ "SET sql_log_bin=0"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-1", None,
                  [ "SET sql_log_bin=1"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-OFF", None,
                  [ "SET sql_log_bin=OFF"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-ON", None,
                  [ "SET sql_log_bin=ON"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-TRUE", None,
                  [ "SET sql_log_bin=TRUE"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-FALSE", None,
                  [ "SET sql_log_bin=FALSE"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-2", Some(0, 1231, "42000"),
                  [ "SET sql_log_bin=2"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin--1", Some(0, 1231, "42000"),
                  [ "SET sql_log_bin=-1"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-NULL", Some(0, 1231, "42000"),
                  [ "SET sql_log_bin=NULL"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "sql_log_bin-DEFAULT", None,
                  [ "SET sql_log_bin=DEFAULT"
                    "SHOW WARNINGS"
                    "SELECT @@sql_log_bin AS value" ]
              "global-log", Some(0, 1228, "HY000"),
                  [ "SET GLOBAL sql_log_bin=0"
                    "SHOW WARNINGS" ]
              "transaction-log", Some(1, 1694, "HY000"),
                  [ "START TRANSACTION"
                    "SET sql_log_bin=0"
                    "SHOW WARNINGS" ]
              "transaction-format", Some(1, 1679, "HY000"),
                  [ "START TRANSACTION"
                    "SET binlog_format='STATEMENT'"
                    "SHOW WARNINGS" ]
            ]
        { Name = "binlog-settings"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [||]
          Coverage = [| "statement:set", [| "text-differential" |] |] }

    let private alterCopyCounts =
        let operations =
            [
              "add-column", "ADD COLUMN added INT", [ None; None; None; None ]
              "drop-column", "DROP COLUMN v", [ None; None; None; None ]
              "add-expression", "ADD COLUMN added DOUBLE DEFAULT (RAND())", [ Some 1674; Some 1674; Some 1674; Some 1674 ]
              "add-stored", "ADD COLUMN added INT GENERATED ALWAYS AS(n+1) STORED", [ None; None; Some 1845; Some 1845 ]
              "add-check", "ADD CONSTRAINT ck CHECK(n>0)", [ None; None; Some 1845; Some 1845 ]
              "add-index", "ADD INDEX ix(n)", [ None; None; None; Some 1845 ]
              "narrow-text", "MODIFY v VARCHAR(1)", [ None; None; Some 1846; Some 1846 ]
              "widen-text", "MODIFY v VARCHAR(30)", [ None; None; None; Some 1845 ]
              "widen-byte-boundary", "MODIFY v VARCHAR(300)", [ None; None; Some 1846; Some 1846 ]
              "change-type", "MODIFY n BIGINT", [ None; None; Some 1846; Some 1846 ]
              "same-type", "MODIFY n INT", [ None; None; None; None ]
              "nullability", "MODIFY n INT NOT NULL", [ None; None; None; Some 1845 ]
              "rename-column", "RENAME COLUMN v TO renamed_v", [ None; None; None; None ]
              "change-column", "CHANGE v renamed_v VARCHAR(20)", [ None; None; None; None ]
              "drop-primary", "DROP PRIMARY KEY", [ None; None; Some 1846; Some 1846 ]
              "add-foreign-key", "ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id)", [ None; None; Some 1846; Some 1846 ]
              "charset-same", "CONVERT TO CHARACTER SET utf8mb4", [ None; None; None; Some 1845 ]
              "charset-latin1", "CONVERT TO CHARACTER SET latin1", [ None; None; Some 1846; Some 1846 ]
              "engine", "ENGINE=InnoDB", [ None; None; None; Some 1845 ]
              "comment", "COMMENT='test'", [ None; None; None; Some 1845 ]
              "rename-table", "RENAME TO renamed", [ None; None; None; None ]
              "no-operation", "", [ None; None; None; None ]
            ]
        let defaultTable = "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),n INT)"
        let cases =
            [ for name, operation, errors in operations do
                  for algorithm, error in List.zip [ ""; "COPY"; "INPLACE"; "INSTANT" ] errors do
                      if operation <> "" || algorithm <> "" then
                          let suffix = if algorithm = "" then "" else (if operation = "" then "" else ", ") + "ALGORITHM=" + algorithm
                          yield name + "-" + algorithm, defaultTable, [], "ALTER TABLE target " + operation + suffix, error
              yield "foreign-key-checks-0-default", defaultTable, [ "SET foreign_key_checks=0" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id)", None
              yield "foreign-key-checks-0-COPY", defaultTable, [ "SET foreign_key_checks=0" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id), ALGORITHM=COPY", None
              yield "foreign-key-checks-0-INPLACE", defaultTable, [ "SET foreign_key_checks=0" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id), ALGORITHM=INPLACE", None
              yield "foreign-key-checks-1-default", defaultTable, [ "SET foreign_key_checks=1" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id)", None
              yield "foreign-key-checks-1-COPY", defaultTable, [ "SET foreign_key_checks=1" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id), ALGORITHM=COPY", None
              yield "foreign-key-checks-1-INPLACE", defaultTable, [ "SET foreign_key_checks=1" ], "ALTER TABLE target ADD CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(id), ALGORITHM=INPLACE", Some 1846
              yield "empty", defaultTable, [ "DELETE FROM target" ], "ALTER TABLE target MODIFY v VARCHAR(1)", None
              yield "tombstone", defaultTable, [ "DELETE FROM target WHERE id=2" ], "ALTER TABLE target MODIFY v VARCHAR(1)", None
              yield "mixed", defaultTable, [], "ALTER TABLE target ADD COLUMN added INT, MODIFY n BIGINT", None
              yield "copy-rename", defaultTable, [], "ALTER TABLE target MODIFY n BIGINT, RENAME TO renamed", None
              yield "boundary-utf8mb4-63-64-default", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(63),n INT) CHARACTER SET utf8mb4", [], "ALTER TABLE target MODIFY v VARCHAR(64)", None
              yield "boundary-utf8mb4-63-64-INPLACE", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(63),n INT) CHARACTER SET utf8mb4", [], "ALTER TABLE target MODIFY v VARCHAR(64), ALGORITHM=INPLACE", Some 1846
              yield "boundary-utf8mb4-64-65-default", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(64),n INT) CHARACTER SET utf8mb4", [], "ALTER TABLE target MODIFY v VARCHAR(65)", None
              yield "boundary-utf8mb4-64-65-INPLACE", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(64),n INT) CHARACTER SET utf8mb4", [], "ALTER TABLE target MODIFY v VARCHAR(65), ALGORITHM=INPLACE", None
              yield "boundary-latin1-255-256-default", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(255),n INT) CHARACTER SET latin1", [], "ALTER TABLE target MODIFY v VARCHAR(256)", None
              yield "boundary-latin1-255-256-INPLACE", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(255),n INT) CHARACTER SET latin1", [], "ALTER TABLE target MODIFY v VARCHAR(256), ALGORITHM=INPLACE", Some 1846
              yield "boundary-latin1-256-257-default", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(256),n INT) CHARACTER SET latin1", [], "ALTER TABLE target MODIFY v VARCHAR(257)", None
              yield "boundary-latin1-256-257-INPLACE", "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(256),n INT) CHARACTER SET latin1", [], "ALTER TABLE target MODIFY v VARCHAR(257), ALGORITHM=INPLACE", None
              yield "default-expression-default", defaultTable, [], "ALTER TABLE target ADD COLUMN added INT DEFAULT (ABS(-1))", None
              yield "default-expression-COPY", defaultTable, [], "ALTER TABLE target ADD COLUMN added INT DEFAULT (ABS(-1)), ALGORITHM=COPY", None
              yield "default-expression-INPLACE", defaultTable, [], "ALTER TABLE target ADD COLUMN added INT DEFAULT (ABS(-1)), ALGORITHM=INPLACE", Some 1845
              yield "default-expression-INSTANT", defaultTable, [], "ALTER TABLE target ADD COLUMN added INT DEFAULT (ABS(-1)), ALGORITHM=INSTANT", Some 1845
              yield "collation-default", defaultTable, [], "ALTER TABLE target CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_bin", None
              yield "collation-COPY", defaultTable, [], "ALTER TABLE target CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, ALGORITHM=COPY", None
              yield "collation-INPLACE", defaultTable, [], "ALTER TABLE target CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, ALGORITHM=INPLACE", None
              yield "collation-INSTANT", defaultTable, [], "ALTER TABLE target CONVERT TO CHARACTER SET utf8mb4 COLLATE utf8mb4_bin, ALGORITHM=INSTANT", Some 1845
              yield "modify-charset-default", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) CHARACTER SET latin1", None
              yield "modify-charset-COPY", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) CHARACTER SET latin1, ALGORITHM=COPY", None
              yield "modify-charset-INPLACE", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) CHARACTER SET latin1, ALGORITHM=INPLACE", Some 1846
              yield "modify-charset-INSTANT", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) CHARACTER SET latin1, ALGORITHM=INSTANT", Some 1846
              yield "modify-collation-default", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) COLLATE utf8mb4_bin", None
              yield "modify-collation-COPY", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) COLLATE utf8mb4_bin, ALGORITHM=COPY", None
              yield "modify-collation-INPLACE", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) COLLATE utf8mb4_bin, ALGORITHM=INPLACE", None
              yield "modify-collation-INSTANT", defaultTable, [], "ALTER TABLE target MODIFY v VARCHAR(20) COLLATE utf8mb4_bin, ALGORITHM=INSTANT", Some 1845
            ]
        { Name = "alter-copy-counts"
          Setup = [||]
          Steps =
            [| for name, definition, setup, statement, error in cases do
                   let initialize =
                       [ "SET foreign_key_checks=1"
                         "DROP TABLE IF EXISTS target"
                         "DROP TABLE IF EXISTS renamed"
                         "DROP TABLE IF EXISTS parent"
                         "CREATE TABLE parent(id INT PRIMARY KEY)"
                         "INSERT INTO parent VALUES(1),(2),(3)"
                         definition
                         "INSERT INTO target VALUES(1,'a',1),(2,'b',2),(3,'c',3)" ] @ setup
                   for index, sql in List.indexed initialize do
                       yield Contract.execute (sprintf "%s-setup-%d" name index) sql
                   let operation = Contract.execute name statement
                   yield
                       match error with
                       | Some 1674 -> operation |> Contract.fails 1674 "HY000"
                       | Some code -> operation |> Contract.fails code "0A000"
                       | None -> operation
                   yield Contract.query (name + "-row-count") "SELECT ROW_COUNT() AS affected"
                   let table = if error.IsNone && statement.Contains("RENAME TO renamed", StringComparison.Ordinal) then "renamed" else "target"
                   yield Contract.query (name + "-rows") ("SELECT id,n FROM " + table + " ORDER BY id") |]
          Cleanup = [| "DROP TABLE IF EXISTS target"; "DROP TABLE IF EXISTS renamed"; "DROP TABLE IF EXISTS parent"; "SET foreign_key_checks=1" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private quotedTableNames =
        let reset =
            [ "USE quoted_names_probe"
              "DROP VIEW IF EXISTS `Odd.View`"
              "DROP TABLE IF EXISTS `Odd.Table`, `New.Table`, `Odd``.Table`, plain" ]
        let cases =
            [
              "duplicate", Some(5, 1062, "23000"),
                  [ "CREATE TABLE `Odd.Table`(n INT, UNIQUE KEY `Odd.Key`(n))"
                    "INSERT INTO `Odd.Table` VALUES(1)"
                    "INSERT INTO `Odd.Table` VALUES(1)"
                    "SHOW WARNINGS" ]
              "qualified", None,
                  [ "CREATE TABLE quoted_names_probe.`Odd.Table`(n INT)"
                    "INSERT INTO `quoted_names_probe`.`Odd.Table` VALUES(1)"
                    "SELECT n FROM quoted_names_probe.`Odd.Table`"
                    "UPDATE quoted_names_probe.`Odd.Table` SET n=2"
                    "SELECT `Odd.Table`.n FROM `Odd.Table`"
                    "DELETE FROM quoted_names_probe.`Odd.Table` WHERE n=2"
                    "SELECT COUNT(*) AS n FROM `Odd.Table`" ]
              "escaped", None,
                  [ "CREATE TABLE `Odd``.Table`(n INT)"
                    "INSERT INTO `Odd``.Table` VALUES(1)"
                    "SELECT n FROM `Odd``.Table`"
                    "ALTER TABLE `Odd``.Table` ADD v INT DEFAULT 7"
                    "SELECT * FROM `Odd``.Table`"
                    "DESCRIBE `Odd``.Table`" ]
              "rename", None,
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "INSERT INTO `Odd.Table` VALUES(1)"
                    "RENAME TABLE `Odd.Table` TO `New.Table`"
                    "SELECT n FROM `New.Table`"
                    "ALTER TABLE `New.Table` RENAME TO `Odd.Table`"
                    "SELECT n FROM `Odd.Table`" ]
              "like", None,
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "CREATE TABLE `New.Table` LIKE `Odd.Table`"
                    "INSERT INTO `New.Table` VALUES(2)"
                    "SELECT n FROM `New.Table`" ]
              "view", None,
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "CREATE VIEW `Odd.View` AS SELECT n FROM `Odd.Table`"
                    "INSERT INTO `Odd.View` VALUES(3)"
                    "SELECT n FROM `Odd.View`"
                    "UPDATE `Odd.View` SET n=4"
                    "SELECT n FROM `Odd.Table`" ]
              "show", None,
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "SHOW COLUMNS FROM `Odd.Table`"
                    "SHOW INDEX FROM quoted_names_probe.`Odd.Table`"
                    "SHOW CREATE TABLE `Odd.Table`" ]
              "truncate", None,
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "INSERT INTO `Odd.Table` VALUES(1)"
                    "TRUNCATE TABLE `Odd.Table`"
                    "SELECT COUNT(*) AS n FROM `Odd.Table`"
                    "DROP TABLE `Odd.Table`" ]
              "foreign", None,
                  [ "CREATE TABLE `Odd.Table`(n INT PRIMARY KEY)"
                    "CREATE TABLE plain(n INT, CONSTRAINT fk FOREIGN KEY(n) REFERENCES `Odd.Table`(n))"
                    "INSERT INTO `Odd.Table` VALUES(1)"
                    "INSERT INTO plain VALUES(1)"
                    "SELECT n FROM plain"
                    "DROP TABLE plain" ]
              "missing-select", Some(3, 1146, "42S02"),
                  [ "SELECT * FROM `Missing.Table`"
                    "SHOW WARNINGS" ]
              "missing-insert", Some(3, 1146, "42S02"),
                  [ "INSERT INTO `Missing.Table` VALUES(1)"
                    "SHOW WARNINGS" ]
              "ansi", None,
                  [ "SET sql_mode='ANSI_QUOTES'"
                    "CREATE TABLE \"Odd.Table\"(n INT)"
                    "INSERT INTO \"Odd.Table\" VALUES(1)"
                    "SELECT n FROM \"Odd.Table\"" ]
              "backtick", None,
                  [ "CREATE TABLE `Odd``.Table`(n INT)"
                    "CREATE TABLE `New.Table` AS SELECT * FROM `Odd``.Table`"
                    "INSERT INTO `New.Table` VALUES(8)"
                    "SELECT n FROM `New.Table`" ]
              "view-check", Some(5, 1369, "HY000"),
                  [ "CREATE TABLE `Odd.Table`(n INT)"
                    "CREATE VIEW `Odd.View` AS SELECT n FROM `Odd.Table` WHERE n>0 WITH CHECK OPTION"
                    "INSERT INTO `Odd.View` VALUES(-1)"
                    "SHOW WARNINGS" ]
              "cross-schema", None,
                  [ "CREATE DATABASE IF NOT EXISTS quoted_names_other"
                    "DROP TABLE IF EXISTS quoted_names_other.`New.Table`"
                    "CREATE TABLE `Odd.Table`(n INT)"
                    "INSERT INTO `Odd.Table` VALUES(9)"
                    "ALTER TABLE `Odd.Table` RENAME TO quoted_names_other.`New.Table`"
                    "SELECT n FROM quoted_names_other.`New.Table`"
                    "RENAME TABLE quoted_names_other.`New.Table` TO quoted_names_probe.`Odd.Table`"
                    "SELECT n FROM `Odd.Table`" ]
              "distinct", None,
                  [ "CREATE DATABASE IF NOT EXISTS quoted_names_odd"
                    "DROP TABLE IF EXISTS quoted_names_odd.Target, `quoted_names_odd.Target`"
                    "CREATE TABLE quoted_names_odd.Target(n INT)"
                    "CREATE TABLE `quoted_names_odd.Target`(n INT)"
                    "INSERT INTO quoted_names_odd.Target VALUES(1)"
                    "INSERT INTO `quoted_names_odd.Target` VALUES(2)"
                    "SELECT n FROM quoted_names_odd.Target"
                    "SELECT n FROM `quoted_names_odd.Target`"
                    "DROP TABLE quoted_names_odd.Target, `quoted_names_odd.Target`" ]
            ]
        { Name = "quoted-table-names"
          Setup = [| "CREATE DATABASE quoted_names_probe" |]
          Steps = cases |> List.map (fun (name, error, statements) -> name, error, reset @ statements) |> isolatedScriptSteps
          Cleanup =
            [| "DROP DATABASE IF EXISTS quoted_names_probe"
               "DROP DATABASE IF EXISTS quoted_names_other"
               "DROP DATABASE IF EXISTS quoted_names_odd" |]
          Coverage = [| "statement:create-table", [| "text-differential" |] |] }

    let private expressionAssignmentWarnings =
        let cases =
            [
              "one", None,
                  [ "SELECT @a:=1 AS value"; "SHOW WARNINGS" ]
              "two", None,
                  [ "SELECT @a:=1 AS a,@b:=2 AS b"; "SHOW WARNINGS" ]
              "nested", None,
                  [ "SELECT @a:=(@b:=2) AS value"; "SHOW WARNINGS" ]
              "same", None,
                  [ "SELECT @a:=1 AS a,@a:=2 AS b"; "SHOW WARNINGS" ]
              "unchosen", None,
                  [ "SELECT IF(0,@a:=1,2) AS value"; "SHOW WARNINGS" ]
              "no-rows", None,
                  [ "SELECT @a:=1 AS value WHERE 0"; "SHOW WARNINGS" ]
              "rows", None,
                  [ "SELECT @a:=n AS value FROM (SELECT 1 AS n UNION ALL SELECT 2) t"; "SHOW WARNINGS" ]
              "set-equal", None,
                  [ "SET @a=1"; "SHOW WARNINGS" ]
              "set-colon", None,
                  [ "SET @a:=1"; "SHOW WARNINGS" ]
              "set-nested", None,
                  [ "SET @a=(@b:=1)"; "SHOW WARNINGS" ]
              "do", None,
                  [ "DO @a:=1"; "SHOW WARNINGS" ]
              "missing-column", Some(0, 1054, "42S22"),
                  [ "SELECT @a:=absent"; "SHOW WARNINGS" ]
              "into", None,
                  [ "SELECT 1 INTO @a"; "SHOW WARNINGS" ]
              "insert", None,
                  [ "DROP TABLE IF EXISTS target"; "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1)"; "INSERT INTO target VALUES(@a:=1)"; "SHOW WARNINGS"; "SELECT * FROM target" ]
              "update", None,
                  [ "DROP TABLE IF EXISTS target"; "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1)"; "UPDATE target SET n=(@a:=2)"; "SHOW WARNINGS"; "SELECT * FROM target" ]
              "delete", None,
                  [ "DROP TABLE IF EXISTS target"; "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1)"; "DELETE FROM target WHERE (@a:=n)=1"; "SHOW WARNINGS"; "SELECT * FROM target" ]
              "prepared", None,
                  [ "PREPARE p FROM 'SELECT @a:=1 AS value'"; "SHOW WARNINGS"; "EXECUTE p"; "SHOW WARNINGS"; "EXECUTE p"; "SHOW WARNINGS"; "DEALLOCATE PREPARE p" ]
              "charset", None,
                  [ "SELECT @a:=_utf8'x' AS value"; "SHOW WARNINGS" ]
              "national", None,
                  [ "SELECT @a:=N'x' AS value"; "SHOW WARNINGS" ]
              "nested-set", None,
                  [ "SET @a=(@b:=1),@c=(@d:=2)"; "SHOW WARNINGS" ]
            ]
        { Name = "expression-assignment-warnings"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "DROP TABLE IF EXISTS target" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private hexExpressionConversion =
        let expressions =
            [
              "1e30", None
              "-1e30", Some(1690, "22003")
              "1e19", None
              "-1e19", Some(1690, "22003")
              "9.223372036854776e18", None
              "-9.223372036854776e18", None
              "9.223372036854775e18", None
              "-9.223372036854775e18", None
              "CAST('1e30' AS DOUBLE)", Some(1690, "22003")
              "CAST('-1e30' AS DOUBLE)", Some(1690, "22003")
              "CAST('1e19' AS DOUBLE)", Some(1690, "22003")
              "1e30+0e0", Some(1690, "22003")
              "0e0-1e30", Some(1690, "22003")
              "ABS(1e30)", Some(1690, "22003")
              "ROUND(1e30)", Some(1690, "22003")
              "FLOOR(1e30)", Some(1690, "22003")
              "CEIL(1e30)", Some(1690, "22003")
              "COALESCE(1e30,0e0)", Some(1690, "22003")
              "IFNULL(1e30,0e0)", Some(1690, "22003")
              "IF(1,1e30,0e0)", None
              "CASE WHEN 1 THEN 1e30 ELSE 0e0 END", None
              "GREATEST(1e30,0e0)", Some(1690, "22003")
              "LEAST(1e30,1e31)", Some(1690, "22003")
              "(SELECT 1e30)", None
              "-(1e30+0e0)", Some(1690, "22003")
              "CAST('9.223372036854776e18' AS DOUBLE)", Some(1690, "22003")
              "CAST('-9.223372036854776e18' AS DOUBLE)", Some(1690, "22003")
              "CAST('9.223372036854775e18' AS DOUBLE)", None
              "2.5e0+0e0", None
              "ABS(-2.5e0)", None
              "COALESCE(2.5e0,0e0)", None
              "0e0-9.223372036854776e18", None
              "ABS(-9.223372036854776e18)", Some(1690, "22003")
              "COALESCE(-9.223372036854776e18,0e0)", None
              "-9.223372036854777e18", Some(1690, "22003")
              "CAST('-9.223372036854775e18' AS DOUBLE)", None
              "IF(1,CAST('1e30' AS DOUBLE),0e0)", Some(1690, "22003")
              "IF(0,CAST('1e30' AS DOUBLE),1e30)", None
              "CASE WHEN 1 THEN CAST('1e30' AS DOUBLE) ELSE 0e0 END", Some(1690, "22003")
              "CASE WHEN 0 THEN CAST('1e30' AS DOUBLE) ELSE 1e30 END", None
              "COALESCE(NULL,1e30)", Some(1690, "22003")
              "IFNULL(NULL,1e30)", Some(1690, "22003")
              "(SELECT CAST('1e30' AS DOUBLE))", Some(1690, "22003")
              "(SELECT IF(1,1e30,0e0))", None
              "(SELECT IF(1,CAST('1e30' AS DOUBLE),0e0))", Some(1690, "22003")
              "(SELECT CASE WHEN 1 THEN 1e30 ELSE 0e0 END)", None
              "(SELECT CASE WHEN 1 THEN CAST('1e30' AS DOUBLE) ELSE 0e0 END)", Some(1690, "22003")
            ]
        let cases =
            [ for expression, error in expressions do
                  yield expression, error |> Option.map (fun (code, state) -> 0, code, state),
                      [ sprintf "SELECT HEX(%s) AS value" expression; "SHOW WARNINGS" ]
              yield "variable", None, [ "SET @n=1e30"; "SELECT HEX(@n) AS value"; "SHOW WARNINGS" ]
              yield "column", None,
                  [ "CREATE TABLE target(n DOUBLE)"; "INSERT INTO target VALUES(1e30)"
                    "SELECT HEX(n) AS value FROM target"; "SHOW WARNINGS" ]
              yield "if-once", None,
                  [ "SET @calls=0"; "SELECT HEX(IF((@calls:=@calls+1),1e30,CAST('1e30' AS DOUBLE))) AS value"; "SHOW WARNINGS"; "SELECT @calls AS calls" ]
              yield "case-once", None,
                  [ "SET @calls=0"; "SELECT HEX(CASE (@calls:=@calls+1) WHEN 1 THEN 1e30 ELSE CAST('1e30' AS DOUBLE) END) AS value"; "SHOW WARNINGS"; "SELECT @calls AS calls" ]
              yield "mixed-scalar-subquery", None,
                  [ "CREATE TABLE hex_mixed_target(id INT PRIMARY KEY,n DOUBLE)"
                    "INSERT INTO hex_mixed_target VALUES(1,1e20)"
                    "SELECT HEX((SELECT IF(id=1,n,'x') FROM hex_mixed_target WHERE id=1)) AS value"
                    "SHOW WARNINGS" ]
              yield "binary-scalar-subquery", None,
                  [ "CREATE TABLE hex_binary_target(id INT PRIMARY KEY,n DOUBLE)"
                    "INSERT INTO hex_binary_target VALUES(1,1e20)"
                    "SELECT HEX((SELECT IF(id=1,n,X'78') FROM hex_binary_target WHERE id=1)) AS value"
                    "SHOW WARNINGS" ]
              yield "utf16-scalar-subquery", None,
                  [ "CREATE TABLE hex_utf16_target(id INT PRIMARY KEY,n DOUBLE)"
                    "INSERT INTO hex_utf16_target VALUES(1,1e20)"
                    "SELECT HEX((SELECT IF(id=1,n,_utf16 X'0078') FROM hex_utf16_target WHERE id=1)) AS value"
                    "SHOW WARNINGS" ]
            ]
        { Name = "hex-expression-conversion"
          Setup = [||]
          Steps = isolatedScriptSteps cases
          Cleanup = [| "DROP TABLE IF EXISTS target,hex_mixed_target,hex_binary_target,hex_utf16_target" |]
          Coverage = [| "function:HEX", [| "text-differential" |] |] }

    let private hexNumericConversion =
        let expressions =
            [
              "0.5"
              "1.5"
              "2.5"
              "-0.5"
              "-1.5"
              "-2.5"
              "2.49"
              "2.51"
              "2.5e0"
              "1.5e0"
              "-2.5e0"
              "-0.5e0"
              "9223372036854775807.0"
              "9223372036854775808.0"
              "18446744073709551615.0"
              "18446744073709551615.5"
              "18446744073709551616.0"
              "-9223372036854775808.0"
              "-9223372036854775809.0"
              "1e30"
              "'2.5'"
              "NULL"
              "2.51e0"
              "-2.51e0"
              "9223372036854775807.4"
              "9223372036854775807.5"
              "-9223372036854775808.5"
              "CAST(18446744073709551615 AS UNSIGNED)"
              "CAST(2.5 AS DECIMAL(10,1))"
            ]
        { Name = "hex-numeric-conversion"
          Setup = [||]
          Steps =
            [| for index, expression in List.indexed expressions do
                   yield Contract.query (sprintf "hex-%d" index) (sprintf "SELECT HEX(%s) AS value" expression)
                   yield Contract.query (sprintf "warnings-%d" index) "SHOW WARNINGS" |]
          Cleanup = [||]
          Coverage = [| "function:HEX", [| "text-differential" |] |] }

    let private alterRowOrder =
        let cases =
            [
              "primary-null",
                  "CREATE TABLE target(id INT,v VARCHAR(8))",
                  "INSERT INTO target VALUES(NULL,'aa')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), ADD PRIMARY KEY(id)", None, Some(1265, "01000")
              "not-null",
                  "CREATE TABLE target(id INT,v VARCHAR(8))",
                  "INSERT INTO target VALUES(NULL,'aa')",
                  "ALTER TABLE target MODIFY id INT NOT NULL, MODIFY v VARCHAR(1)", None, Some(1265, "01000")
              "forward",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY w VARCHAR(1)", None, Some(1265, "01000")
              "reverse",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY w VARCHAR(1), MODIFY v VARCHAR(1)", None, Some(1265, "01000")
              "position",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY w VARCHAR(1) FIRST, MODIFY v VARCHAR(1)", None, Some(1265, "01000")
              "later-failure",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'a','bb'),(2,'cc','d')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY w VARCHAR(1)", None, Some(1265, "01000")
              "unique",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8), UNIQUE KEY uq(v))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'ab','dd'),(3,'zz','ff')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY w VARCHAR(1)", Some(1062, "23000"), Some(1265, "01000")
              "rename",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target CHANGE w renamed VARCHAR(1), MODIFY v VARCHAR(1)", None, Some(1265, "01000")
              "add-between",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY w VARCHAR(1), ADD n INT DEFAULT 7 AFTER v, MODIFY v VARCHAR(1)", None, Some(1265, "01000")
              "drop-between",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY w VARCHAR(1), DROP v", None, Some(1265, "01000")
              "add-unique",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'ab','dd'),(3,'zz','ff')",
                  "ALTER TABLE target ADD UNIQUE KEY uq(v), MODIFY w VARCHAR(1), MODIFY v VARCHAR(1)", Some(1062, "23000"), Some(1265, "01000")
              "invalid-later",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY absent VARCHAR(1)", Some(1054, "42S22"), Some(1054, "42S22")
              "repeated-column",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aaa','bbb'),(2,'ccc','ddd')",
                  "ALTER TABLE target MODIFY v VARCHAR(2), MODIFY v VARCHAR(1)", Some(1054, "42S22"), Some(1054, "42S22")
              "mixed",
                  "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(8),w VARCHAR(8))",
                  "INSERT INTO target VALUES(1,'aa','x'),(2,'cc','200')",
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY w TINYINT", None, Some(1265, "01000")
            ]
        { Name = "alter-row-order"
          Setup = [||]
          Steps =
            [| for name, definition, insert, alter, normalError, strictError in cases do
                   for mode, error in [ "", normalError; "STRICT_ALL_TABLES", strictError ] do
                       let label = name + "-" + mode
                       yield Contract.execute (label + "-drop") "DROP TABLE IF EXISTS target"
                       yield Contract.execute (label + "-create") definition
                       yield Contract.execute (label + "-insert") insert
                       yield Contract.execute (label + "-mode") (sprintf "SET sql_mode='%s'" mode)
                       let operation = Contract.execute label alter
                       yield match error with Some(code, state) -> operation |> Contract.fails code state | None -> operation
                       yield Contract.query (label + "-warnings") "SHOW WARNINGS"
                       yield Contract.query (label + "-rows") "SELECT * FROM target ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS target"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private alterCoercion =
        let cases =
            [
              "multi",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),w VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd'),(3,'ee','ff')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1), MODIFY w VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT * FROM target ORDER BY id" ]
              "decimal--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DECIMAL(8,3))"
                    "INSERT INTO target VALUES(1,1.123),(2,2.456)"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v DECIMAL(4,1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'"; "SELECT v FROM target ORDER BY id" ]
              "decimal-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v DECIMAL(8,3))"
                    "INSERT INTO target VALUES(1,1.123),(2,2.456)"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v DECIMAL(4,1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'"; "SELECT v FROM target ORDER BY id" ]
              "varchar--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a'),(2,'bc'),(3,'de')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varchar--unique-True",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),UNIQUE KEY uq_v(v))"
                    "INSERT INTO target VALUES(1,'aa'),(2,'ab'),(3,'zz')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", Some(1062, "23000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "char-spaces--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a  '),(2,'b  ')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v CHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varchar-spaces--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a  '),(2,'b  ')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "unicode--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'é'),(2,'🙂x')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "binary--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARBINARY(20))"
                    "INSERT INTO target VALUES(1,X'41'),(2,X'4243'),(3,X'4445')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v BINARY(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varbinary--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARBINARY(20))"
                    "INSERT INTO target VALUES(1,X'41'),(2,X'4243'),(3,X'4445')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v VARBINARY(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "integer--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v INT)"
                    "INSERT INTO target VALUES(1,1),(2,200),(3,300)"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v TINYINT", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "numeric-text--unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'1'),(2,'200'),(3,'x')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target MODIFY v TINYINT", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varchar-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a'),(2,'bc'),(3,'de')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", Some(1265, "01000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varchar-STRICT_ALL_TABLES-unique-True",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),UNIQUE KEY uq_v(v))"
                    "INSERT INTO target VALUES(1,'aa'),(2,'ab'),(3,'zz')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", Some(1265, "01000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "char-spaces-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a  '),(2,'b  ')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v CHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varchar-spaces-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'a  '),(2,'b  ')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", Some(1265, "01000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "unicode-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'é'),(2,'🙂x')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", Some(1265, "01000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "binary-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARBINARY(20))"
                    "INSERT INTO target VALUES(1,X'41'),(2,X'4243'),(3,X'4445')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v BINARY(1)", Some(1406, "22001"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "varbinary-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARBINARY(20))"
                    "INSERT INTO target VALUES(1,X'41'),(2,X'4243'),(3,X'4445')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v VARBINARY(1)", Some(1265, "01000"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "integer-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v INT)"
                    "INSERT INTO target VALUES(1,1),(2,200),(3,300)"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v TINYINT", Some(1264, "22003"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "numeric-text-STRICT_ALL_TABLES-unique-False",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'1'),(2,'200'),(3,'x')"
                    "SET sql_mode='STRICT_ALL_TABLES'" ],
                  "ALTER TABLE target MODIFY v TINYINT", Some(1264, "22003"),
                  [ "SHOW WARNINGS"; "SELECT id,HEX(v) AS v FROM target ORDER BY id"; "SHOW COLUMNS FROM target LIKE 'v'" ]
              "change",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),w VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd'),(3,'ee','ff')"
                    "SET sql_mode=''" ],
                  "ALTER TABLE target CHANGE v renamed VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT * FROM target ORDER BY id" ]
              "tombstone",
                  [ "DROP TABLE IF EXISTS target"
                    "CREATE TABLE target(id INT PRIMARY KEY,v VARCHAR(20),w VARCHAR(20))"
                    "INSERT INTO target VALUES(1,'aa','bb'),(2,'cc','dd'),(3,'ee','ff')"
                    "SET sql_mode=''"
                    "DELETE FROM target WHERE id=1" ],
                  "ALTER TABLE target MODIFY v VARCHAR(1)", None,
                  [ "SHOW WARNINGS"; "SELECT * FROM target ORDER BY id" ]
            ]
        { Name = "alter-coercion"
          Setup = [||]
          Steps =
            [| for name, setup, statement, error, queries in cases do
                   for index, sql in List.indexed setup do
                       yield Contract.execute (sprintf "%s-setup-%d" name index) sql
                   let operation = Contract.execute name statement
                   yield match error with Some(code, state) -> operation |> Contract.fails code state | None -> operation
                   for index, sql in List.indexed queries do
                       yield Contract.query (sprintf "%s-result-%d" name index) sql |]
          Cleanup = [| "DROP TABLE IF EXISTS target"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:alter-table", [| "text-differential" |] |] }

    let private qualifiedDuplicateKeys =
        let keyed =
            [ "CREATE TABLE target(id INT PRIMARY KEY,n INT,UNIQUE KEY NamedKey(n))"
              "INSERT INTO target VALUES(1,10),(2,20)" ]
        let cases =
            [
              "insert-primary", keyed,
                  "INSERT INTO target VALUES(1,30)", true
              "insert-unique", keyed,
                  "INSERT INTO target VALUES(3,10)", true
              "ignore", keyed,
                  "INSERT IGNORE INTO target VALUES(1,30)", false
              "update-primary", keyed,
                  "UPDATE target SET id=1 WHERE id=2", true
              "update-unique", keyed,
                  "UPDATE target SET n=10 WHERE id=2", true
              "upsert", keyed,
                  "INSERT INTO target VALUES(1,20) ON DUPLICATE KEY UPDATE n=VALUES(n)", true
              "alias-update", keyed,
                  "UPDATE target AS t SET t.id=1 WHERE t.id=2", true
              "view", keyed @ [ "CREATE VIEW key_view AS SELECT * FROM target" ],
                  "INSERT INTO key_view VALUES(1,30)", true
              "add-unique", [ "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1),(1)" ],
                  "ALTER TABLE target ADD UNIQUE KEY NamedKey(n)", true
              "add-primary", [ "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1),(1)" ],
                  "ALTER TABLE target ADD PRIMARY KEY(n)", true
              "create-index", [ "CREATE TABLE target(n INT)"; "INSERT INTO target VALUES(1),(1)" ],
                  "CREATE UNIQUE INDEX NamedKey ON target(n)", true
              "rename", keyed @ [ "ALTER TABLE target RENAME TO renamed" ],
                  "INSERT INTO renamed VALUES(1,30)", true
              "mixed-case", [ "CREATE TABLE target(n INT, UNIQUE KEY NamedKey(n))"; "INSERT INTO target VALUES(1)" ],
                  "INSERT INTO TARGET VALUES(1)", true
            ]
        let cleanup = [ "DROP VIEW IF EXISTS key_view"; "DROP TABLE IF EXISTS target"; "DROP TABLE IF EXISTS renamed" ]
        { Name = "qualified-duplicate-keys"
          Setup = [| "SET sql_mode=DEFAULT" |]
          Steps =
            [| for name, setup, statement, fails in cases do
                   for index, sql in List.indexed (cleanup @ setup) do
                       yield Contract.execute (sprintf "%s-setup-%d" name index) sql
                   let operation = Contract.execute name statement
                   yield if fails then operation |> Contract.fails 1062 "23000" else operation
                   yield Contract.query (name + "-warnings") "SHOW WARNINGS" |]
          Cleanup = Array.ofList cleanup
          Coverage = [| "statement:insert", [| "text-differential" |]; "statement:update", [| "text-differential" |]; "statement:alter-table", [| "text-differential" |] |] }

    let private integerCastConditions =
        let inputs =
            [
                  "'x'"
                  "''"
                  "' '"
                  "'12x'"
                  "'1.9'"
                  "'-1.9'"
                  "'1e2'"
                  "'.5'"
                  "'+12'"
                  "'  12  '"
                  "'12\\tx'"
                  "'0x10'"
                  "'9223372036854775807'"
                  "'9223372036854775808'"
                  "'18446744073709551615'"
                  "'18446744073709551616'"
                  "'-9223372036854775808'"
                  "'-9223372036854775809'"
                  "'-18446744073709551615'"
                  "'-18446744073709551616'"
                  "'99999999999999999999999999999999999999'"
                  "NULL"
                  "1.9"
                  "-1.9"
                  "1e2"
                  "X'3132'"
                  "_binary'1x'"
                  "CAST('1x' AS BINARY)"
                  "_utf16 X'0031002E0035'"
                  "'１２'"
                  "'١٢'"
                  "'9223372036854775808x'"
                  "'-0.5'"
                  "'-1'"
                  "'+ 1'"
                  "' 12'"
                  "'12 '"
            ]
        let skippedCastConditions =
            [ "IF(0,CAST('x' AS SIGNED),1)"
              "IFNULL(1,CAST('x' AS SIGNED))"
              "COALESCE(1,CAST('x' AS SIGNED))" ]
        let conditions =
            skippedCastConditions @
            [ "IF(1,1,CAST('x' AS SIGNED))"
              "IF(NULL,CAST('x' AS SIGNED),1)"
              "IF('1x',1,CAST('x' AS SIGNED))"
              "IFNULL(NULL,CAST('x' AS SIGNED))"
              "COALESCE(NULL,CAST('x' AS SIGNED))" ]
        let observe name expression =
            [ Contract.query name ("SELECT " + expression + " AS n")
              Contract.query (name + "-warnings") "SHOW WARNINGS" ]
        let insert name mode ignore expression columnType expectedError =
            [ Contract.execute (name + "-drop") "DROP TABLE IF EXISTS cast_target"
              Contract.execute (name + "-create") ("CREATE TABLE cast_target(n " + columnType + ")")
              Contract.execute (name + "-mode") ("SET sql_mode='" + mode + "'")
              let step = Contract.execute name ("INSERT " + ignore + "INTO cast_target VALUES(" + expression + ")")
              match expectedError with
              | Some(code, state) -> step |> Contract.fails code state
              | None -> step
              Contract.query (name + "-warnings") "SHOW WARNINGS"
              Contract.query (name + "-rows") "SELECT n FROM cast_target" ]
        { Name = "integer-cast-conditions"
          Setup = [| "SET sql_mode=DEFAULT" |]
          Steps =
            [| for target in [ "SIGNED"; "UNSIGNED" ] do
                   for index, value in List.indexed inputs do
                       yield! observe (sprintf "%s-%d" target index) (sprintf "CAST(%s AS %s)" value target)
                   for mode in [ ""; "STRICT_ALL_TABLES" ] do
                       for ignore in [ ""; "IGNORE " ] do
                           for index, value in List.indexed [ "'x'"; "'1.9'" ] do
                               let error = if mode <> "" && ignore = "" then Some(1292, "22007") else None
                               yield! insert (sprintf "%s-%s-%s-%d" target mode ignore index)
                                   mode ignore (sprintf "CAST(%s AS %s)" value target) "BIGINT" error
                   for ignore in [ ""; "IGNORE " ] do
                       let value = if target = "SIGNED" then "'9223372036854775808'" else "'-1'"
                       yield! insert (sprintf "complement-%s-%s" target ignore) "STRICT_ALL_TABLES" ignore
                           (sprintf "CAST(%s AS %s)" value target) "DECIMAL(65,0)" None
               yield Contract.execute "reset-mode" "SET sql_mode=DEFAULT"
               for index, expression in List.indexed conditions do
                   yield! observe (sprintf "conditional-%d" index) expression
               yield Contract.query "unselected-reference" "SELECT COALESCE(1,missing) AS n" |> Contract.fails 1054 "42S22"
               yield Contract.query "reference-warnings" "SHOW WARNINGS"
               for index, expression in List.indexed skippedCastConditions do
                   yield! insert (sprintf "strict-conditional-%d" index) "STRICT_ALL_TABLES" "" expression "INT" None
               yield Contract.execute "prepare-cast" "PREPARE cast_statement FROM 'SELECT CAST(? AS SIGNED) AS n'"
               for index, value in List.indexed [ "'1.9'"; "'x'" ] do
                   yield Contract.execute (sprintf "bind-%d" index) ("SET @cast_parameter=" + value)
                   yield Contract.query (sprintf "execute-%d" index) "EXECUTE cast_statement USING @cast_parameter"
                   yield Contract.query (sprintf "prepared-warnings-%d" index) "SHOW WARNINGS"
               yield Contract.execute "deallocate-cast" "DEALLOCATE PREPARE cast_statement" |]
          Cleanup = [| "DROP TABLE IF EXISTS cast_target"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:select", [| "text-differential" |]; "statement:insert", [| "text-differential" |] |] }

    let private triggerWarningLifetimes =
        let setup =
            [ "DROP TABLE IF EXISTS child"
              "DROP TABLE IF EXISTS parent"
              "DROP TABLE IF EXISTS audit"
              "DROP PROCEDURE IF EXISTS warned_proc"
              "SET sql_mode=''"
              "SET @seen=NULL"
              "CREATE TABLE parent(id INT PRIMARY KEY)"
              "CREATE TABLE child(id INT PRIMARY KEY,pid INT,CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES parent(id))"
              "CREATE TABLE audit(n INT)"
              "INSERT INTO parent VALUES(1),(2),(3)"
              "INSERT INTO child VALUES(1,2)" ]
        let observations =
            [ "SHOW WARNINGS"; "SELECT id FROM parent ORDER BY id"
              "SELECT n FROM audit ORDER BY n"; "SELECT @seen AS seen" ]
        let cases =
            [
              "warning-in-failed-insert", Some(1062, "23000"),
                  [ "ALTER TABLE audit ADD PRIMARY KEY(n)"
                    "INSERT INTO audit VALUES(1)"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW INSERT INTO audit VALUES(CAST('x' AS SIGNED)),(1)"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-in-failed-update", Some(1062, "23000"),
                  [ "ALTER TABLE audit ADD PRIMARY KEY(n)"
                    "INSERT INTO audit VALUES(1),(2)"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW UPDATE audit SET n=CAST('x' AS SIGNED)"
                    "DELETE FROM parent WHERE id=1" ]
              "boolean-warning-in-failed-insert", Some(1062, "23000"),
                  [ "ALTER TABLE audit ADD PRIMARY KEY(n)"
                    "INSERT INTO audit VALUES(1)"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW INSERT INTO audit VALUES(IF('1x',0,0)),(1)"
                    "DELETE FROM parent WHERE id=1" ]
              "boolean-warning-in-failed-update", Some(1062, "23000"),
                  [ "ALTER TABLE audit ADD PRIMARY KEY(n)"
                    "INSERT INTO audit VALUES(1),(2)"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW UPDATE audit SET n=IF('1x',0,0)"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-then-missing-table", Some(1146, "42S02"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; INSERT INTO absent VALUES(1); END"
                    "DELETE FROM parent WHERE id=1" ]
              "before-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "DELETE FROM parent WHERE id=1" ]
              "before-warning-ignore", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "DELETE IGNORE FROM parent WHERE id=1" ]
              "after-warning", None,
                  [ "CREATE TRIGGER guard_parent AFTER DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-then-set", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; SET @seen=1; END"
                    "DELETE FROM parent WHERE id=1" ]
              "conversion-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW INSERT INTO audit VALUES(CAST('x' AS SIGNED))"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-then-fatal", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "conversion-then-fatal", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN INSERT INTO audit VALUES(CAST('x' AS SIGNED)); SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-prior-row", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN IF OLD.id=1 THEN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; ELSE SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END IF; END"
                    "DELETE IGNORE FROM parent ORDER BY id" ]
              "warning-get-diagnostics", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; GET DIAGNOSTICS CONDITION 1 @seen=MYSQL_ERRNO; END"
                    "DELETE FROM parent WHERE id=1" ]
              "handled-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE CONTINUE HANDLER FOR SQLWARNING SET @seen=1; SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "outer-conversion-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "DELETE IGNORE FROM parent WHERE id=1 AND '1x'" ]
              "outer-fk-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "DELETE IGNORE FROM parent ORDER BY id" ]
              "prior-trigger-warning", Some(1644, "45001"),
                  [ "CREATE TRIGGER first_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'"
                    "DELETE FROM parent WHERE id=1" ]
              "procedure-warning", None,
                  [ "CREATE PROCEDURE warned_proc() SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW CALL warned_proc()"
                    "DELETE FROM parent WHERE id=1" ]
              "conversion-and-error", Some(1054, "42S22"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW INSERT INTO audit VALUES(CAST('x' AS SIGNED)),(missing)"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-resignal-error", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLWARNING RESIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-resignal-unchanged", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLWARNING RESIGNAL; SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-resignal-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLWARNING RESIGNAL SQLSTATE '01001' SET MESSAGE_TEXT='changed'; SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "error-resignal-error", Some(1644, "45002"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION RESIGNAL SQLSTATE '45002' SET MESSAGE_TEXT='changed'; SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "error-resignal-unchanged", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION RESIGNAL; SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "error-resignal-message", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION RESIGNAL SET MESSAGE_TEXT='changed'; SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "error-resignal-same-state", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION RESIGNAL SQLSTATE '45001'; SIGNAL SQLSTATE '45001' SET MESSAGE_TEXT='fatal'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "resignal-keeps-custom-information", Some(1644, "45002"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLEXCEPTION RESIGNAL SQLSTATE '45002'; SIGNAL SQLSTATE '45001' SET MYSQL_ERRNO=60001,MESSAGE_TEXT='original'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "warning-resignal-default-message", Some(1644, "45001"),
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN DECLARE EXIT HANDLER FOR SQLWARNING RESIGNAL SQLSTATE '45001'; SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; END"
                    "DELETE FROM parent WHERE id=1" ]
              "insert-before-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE INSERT ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "INSERT INTO parent VALUES(4)" ]
              "insert-after-warning", None,
                  [ "CREATE TRIGGER guard_parent AFTER INSERT ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "INSERT INTO parent VALUES(4)" ]
              "update-before-warning", None,
                  [ "CREATE TRIGGER guard_parent BEFORE UPDATE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "UPDATE parent SET id=4 WHERE id=1" ]
              "update-after-warning", None,
                  [ "CREATE TRIGGER guard_parent AFTER UPDATE ON parent FOR EACH ROW SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'"
                    "UPDATE parent SET id=4 WHERE id=1" ]
              "warning-get-row-count", None,
                  [ "CREATE TRIGGER guard_parent BEFORE DELETE ON parent FOR EACH ROW BEGIN SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT='noticed'; GET DIAGNOSTICS @seen=ROW_COUNT; END"
                    "DELETE FROM parent WHERE id=1" ]
            ]
        { Name = "trigger-warning-lifetimes"
          Setup = [||]
          Steps =
            [| for name, error, statements in cases do
                   for index, statement in List.indexed (setup @ statements @ observations) do
                       let stepName = sprintf "%s-%d" name index
                       if statement.StartsWith("SHOW", StringComparison.Ordinal) || statement.StartsWith("SELECT", StringComparison.Ordinal) then
                           Contract.query stepName statement
                       else
                           let step = Contract.execute stepName statement
                           match error with
                           | Some(code, state) when statement.StartsWith("DELETE", StringComparison.Ordinal) -> step |> Contract.fails code state
                           | _ -> step |]
          Cleanup = [| "DROP TABLE IF EXISTS child"; "DROP TABLE IF EXISTS parent"; "DROP TABLE IF EXISTS audit"; "DROP PROCEDURE IF EXISTS warned_proc"; "SET sql_mode=DEFAULT" |]
          Coverage = [| "statement:delete", [| "text-differential" |]; "statement:insert", [| "text-differential" |]; "statement:update", [| "text-differential" |] |] }

    let private deleteIgnoreForeignKeys =
        let setup foreignKeyAction =
            [ "DROP TABLE IF EXISTS audit"; "DROP TABLE IF EXISTS child"; "DROP TABLE IF EXISTS parent"
              "CREATE TABLE parent(id INT PRIMARY KEY)"
              ("CREATE TABLE child(id INT PRIMARY KEY,pid INT,CONSTRAINT fk_parent FOREIGN KEY(pid) REFERENCES parent(id)" + foreignKeyAction + ")")
              "INSERT INTO parent VALUES(1),(2),(3)"; "INSERT INTO child VALUES(1,2)" ]
        let audit =
            [ "CREATE TABLE audit(seq INT AUTO_INCREMENT PRIMARY KEY,phase VARCHAR(10),id INT)"
              "CREATE TRIGGER before_parent BEFORE DELETE ON parent FOR EACH ROW INSERT INTO audit(phase,id) VALUES('before',OLD.id)"
              "CREATE TRIGGER after_parent AFTER DELETE ON parent FOR EACH ROW INSERT INTO audit(phase,id) VALUES('after',OLD.id)" ]
        let childAudit =
            [ "INSERT INTO child VALUES(2,2)"
              "CREATE TABLE audit(seq INT AUTO_INCREMENT PRIMARY KEY,phase VARCHAR(10),id INT,pid INT)"
              "CREATE TRIGGER before_child BEFORE DELETE ON child FOR EACH ROW INSERT INTO audit(phase,id,pid) VALUES('before',OLD.id,OLD.pid)"
              "CREATE TRIGGER after_child AFTER DELETE ON child FOR EACH ROW INSERT INTO audit(phase,id,pid) VALUES('after',OLD.id,OLD.pid)" ]
        let cases =
            [ "ordered", "", [], "DELETE IGNORE FROM parent ORDER BY id"
              "limit-one", "", [], "DELETE IGNORE FROM parent ORDER BY id LIMIT 1"
              "descending-limit", "", [], "DELETE IGNORE FROM parent ORDER BY id DESC LIMIT 2"
              "blocked", "", [], "DELETE IGNORE FROM parent WHERE id=2"
              "joined", "", [], "DELETE IGNORE p FROM parent p LEFT JOIN child c ON c.pid=p.id"
              "using", "", [], "DELETE IGNORE FROM p USING parent p LEFT JOIN child c ON c.pid=p.id"
              "multi-parent-first", "", [], "DELETE IGNORE p,c FROM parent p LEFT JOIN child c ON c.pid=p.id"
              "multi-child-first", "", [], "DELETE IGNORE c,p FROM parent p LEFT JOIN child c ON c.pid=p.id"
              "multi-child-first-two", "", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE c,p FROM parent p LEFT JOIN child c ON c.pid=p.id"
              "subset-restrict-parent-first", "", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "subset-restrict-child-first", "", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "subset-cascade-parent-first", " ON DELETE CASCADE", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "subset-cascade-child-first", " ON DELETE CASCADE", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "subset-set-null-parent-first", " ON DELETE SET NULL", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "subset-set-null-child-first", " ON DELETE SET NULL", [ "INSERT INTO child VALUES(2,2)" ], "DELETE IGNORE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-cascade", " ON DELETE CASCADE", childAudit, "DELETE IGNORE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-set-null", " ON DELETE SET NULL", childAudit, "DELETE IGNORE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-cascade-child-first", " ON DELETE CASCADE", childAudit, "DELETE IGNORE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-set-null-child-first", " ON DELETE SET NULL", childAudit, "DELETE IGNORE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-cascade-plain", " ON DELETE CASCADE", childAudit, "DELETE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-set-null-plain", " ON DELETE SET NULL", childAudit, "DELETE p,c FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-cascade-child-first-plain", " ON DELETE CASCADE", childAudit, "DELETE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "trigger-set-null-child-first-plain", " ON DELETE SET NULL", childAudit, "DELETE c,p FROM parent p JOIN child c ON c.pid=p.id WHERE c.id=1"
              "cascade", " ON DELETE CASCADE", [], "DELETE IGNORE FROM parent"
              "update-action", " ON UPDATE CASCADE", [], "DELETE IGNORE FROM parent"
              "audit", "", audit, "DELETE IGNORE FROM parent ORDER BY id"
              "audit-limit", "", audit, "DELETE IGNORE FROM parent ORDER BY id DESC LIMIT 2"
              "audit-joined", "", audit, "DELETE IGNORE p FROM parent p LEFT JOIN child c ON c.pid=p.id"
              "rollback", "", audit @ [ "BEGIN" ], "DELETE IGNORE FROM parent ORDER BY id"
              "fatal-after", "", audit @ [ "DROP TRIGGER after_parent"; "CREATE TRIGGER after_parent AFTER DELETE ON parent FOR EACH ROW BEGIN IF OLD.id=3 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='late failure'; END IF; INSERT INTO audit(phase,id) VALUES('after',OLD.id); END" ], "DELETE IGNORE FROM parent ORDER BY id" ]
        { Name = "delete-ignore-foreign-keys"
          Setup = [||]
          Steps =
            [| for name, foreignKeyAction, additions, sql in cases do
                   for index, statement in List.indexed (setup foreignKeyAction @ additions) do
                       Contract.execute (sprintf "%s-setup-%d" name index) statement
                   let deletion = Contract.execute (name + "-delete") sql
                   if name = "fatal-after" then deletion |> Contract.fails 1644 "45000" else deletion
                   Contract.query (name + "-warnings") "SHOW WARNINGS"
                   Contract.query (name + "-parents") "SELECT id FROM parent ORDER BY id"
                   Contract.query (name + "-children") "SELECT id,pid FROM child ORDER BY id"
                   if additions = audit || name = "rollback" || name = "fatal-after" then
                       let order = if name = "audit-joined" then "id,seq" else "seq"
                       Contract.query (name + "-audit") ("SELECT phase,id FROM audit ORDER BY " + order)
                   if additions = childAudit then
                       Contract.query (name + "-audit") "SELECT phase,id,pid FROM audit ORDER BY seq"
                   if name = "rollback" then
                       Contract.execute "rollback-transaction" "ROLLBACK"
                       Contract.query "rollback-warnings" "SHOW WARNINGS"
                       Contract.query "rollback-restored-parents" "SELECT id FROM parent ORDER BY id"
                       Contract.query "rollback-restored-audit" "SELECT phase,id FROM audit ORDER BY seq" |]
          Cleanup = [| "ROLLBACK"; "DROP TABLE IF EXISTS child"; "DROP TABLE IF EXISTS parent"; "DROP TABLE IF EXISTS audit" |]
          Coverage = [| "statement:delete", [| "text-differential" |] |] }

    let private bareTriggerConditions =
        { Name = "bare-trigger-conditions"
          Setup = [| "CREATE TABLE signal_parent(id INT PRIMARY KEY)"; "INSERT INTO signal_parent VALUES(1),(2)" |]
          Steps =
            [| Contract.execute "create-signal" "CREATE TRIGGER guard_parent BEFORE DELETE ON signal_parent FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='blocked'"
               Contract.execute "signal-delete" "DELETE IGNORE FROM signal_parent WHERE id=1" |> Contract.fails 1644 "45000"
               Contract.query "signal-warnings" "SHOW WARNINGS"
               Contract.query "signal-rows" "SELECT id FROM signal_parent ORDER BY id"
               Contract.execute "drop-signal" "DROP TRIGGER guard_parent"
               Contract.execute "create-resignal" "CREATE TRIGGER guard_parent BEFORE DELETE ON signal_parent FOR EACH ROW RESIGNAL"
               Contract.execute "resignal-delete" "DELETE IGNORE FROM signal_parent WHERE id=1" |> Contract.fails 1645 "0K000"
               Contract.query "resignal-warnings" "SHOW WARNINGS"
               Contract.query "resignal-rows" "SELECT id FROM signal_parent ORDER BY id" |]
          Cleanup = [| "DROP TABLE IF EXISTS signal_parent" |]
          Coverage = [| "statement:delete", [| "text-differential" |] |] }

    let private regexpPosixClasses =
        { Name = "regexp-posix-classes"
          Setup =
            [| "CREATE TABLE regex_display(n INT(4) ZEROFILL,s VARCHAR(4))"
               "INSERT INTO regex_display VALUES (7,'007')"
               "CREATE TABLE regex_dynamic(id INT PRIMARY KEY,s VARCHAR(10),p VARCHAR(10),f CHAR(1))"
               "INSERT INTO regex_dynamic VALUES (1,'abc','^a','c'),(2,'abc','^b','c'),(3,'Abc','^a','i'),(4,'Abc','^a','c')" |]
          Steps =
            [| Contract.query "letter-classes"
                   "SELECT REGEXP_LIKE('a','[[:lower:]]','c'),REGEXP_LIKE('A','[[:lower:]]','c'),REGEXP_LIKE('α','[[:lower:]]','c'),REGEXP_LIKE('Α','[[:lower:]]','i'),REGEXP_LIKE('a','[[:upper:]]','c'),REGEXP_LIKE('A','[[:upper:]]','c'),REGEXP_LIKE('Α','[[:upper:]]','c'),REGEXP_LIKE('α','[[:upper:]]','i')"
                   |> Contract.comparingValues
               Contract.query "digit-and-blank-classes"
                   "SELECT REGEXP_LIKE('５','[[:xdigit:]]','c'),REGEXP_LIKE('Ｇ','[[:xdigit:]]','c'),REGEXP_LIKE('f','[[:xdigit:]]','c'),REGEXP_LIKE('g','[[:xdigit:]]','c'),REGEXP_LIKE('\\t','[[:blank:]]','c'),REGEXP_LIKE('\\n','[[:blank:]]','c'),REGEXP_LIKE(' ','[[:blank:]]','c')"
                   |> Contract.comparingValues
               Contract.query "unicode-and-character-boundaries"
                   "SELECT REGEXP_LIKE('Ⅳ','[[:alpha:]]','c'),REGEXP_LIKE('Ⅳ','[[:alnum:]]','c'),REGEXP_LIKE('\u0301','[[:word:]]','c'),REGEXP_LIKE('\u200d','[[:word:]]','c'),REGEXP_LIKE('²','[[:word:]]','c'),REGEXP_LIKE('A','[[:ascii:]]','c'),REGEXP_LIKE('é','[[:ascii:]]','c'),REGEXP_LIKE('\\t','[[:cntrl:]]','c'),REGEXP_LIKE('_','[[:punct:]]','c'),REGEXP_LIKE('+','[[:punct:]]','c'),REGEXP_LIKE('\u200d','[[:graph:]]','c'),REGEXP_LIKE(' ','[[:graph:]]','c'),REGEXP_LIKE(' ','[[:print:]]','c'),REGEXP_LIKE('\\n','[[:print:]]','c')"
                   |> Contract.comparingValues
               Contract.query "negated-classes"
                   "SELECT REGEXP_LIKE('A','[[:^alpha:]]','c'),REGEXP_LIKE('1','[[:^alpha:]]','c'),REGEXP_LIKE('\\n','[[:^alpha:]]','c'),REGEXP_LIKE('\u0301','[[:^word:]]','c'),REGEXP_LIKE('!','[[:^word:]]','c'),REGEXP_LIKE(' ','[[:^graph:]]','c'),REGEXP_LIKE('\\n','[[:^graph:]]','c'),REGEXP_LIKE(' ','[[:^print:]]','c'),REGEXP_LIKE('\\n','[[:^print:]]','c')"
                   |> Contract.comparingValues
               Contract.query "combined-classes"
                   "SELECT REGEXP_LIKE('a','[a[:digit:]]','c'),REGEXP_LIKE('7','[a[:digit:]]','c'),REGEXP_LIKE('b','[a[:digit:]]','c'),REGEXP_LIKE('A','[[:alpha:][:digit:]]','c'),REGEXP_LIKE('7','[[:alpha:][:digit:]]','c'),REGEXP_LIKE('!','[[:alpha:][:digit:]]','c'),REGEXP_LIKE('a7','^[a[:digit:]]+$','c')"
                   |> Contract.comparingValues
               Contract.query "mixed-negated-members"
                   "SELECT REGEXP_LIKE('x','[x[:^alpha:]]','c'),REGEXP_LIKE('7','[x[:^alpha:]]','c'),REGEXP_LIKE('A','[x[:^alpha:]]','c'),REGEXP_LIKE('\\n','[x[:^alpha:]]','c'),REGEXP_LIKE('x7','^[x[:^alpha:]]+$','c'),REGEXP_LIKE('7','[[:^alpha:][:digit:]]','c'),REGEXP_LIKE('A','[[:^alpha:][:digit:]]','c'),REGEXP_LIKE('7','[^x[:^alpha:]]','c'),REGEXP_LIKE('A','[^x[:^alpha:]]','c')"
                   |> Contract.comparingValues
               Contract.query "whitespace-escapes"
                   "SELECT REGEXP_LIKE(' ','\\\\h','c'),REGEXP_LIKE('\\n','\\\\h','c'),REGEXP_LIKE(' ','\\\\H','c'),REGEXP_LIKE('\\n','\\\\v','c'),REGEXP_LIKE(' ','\\\\v','c'),REGEXP_LIKE('A','\\\\V','c'),REGEXP_LIKE('\\n','[a\\\\H]','c'),REGEXP_LIKE(' ','[a\\\\H]','c')"
                   |> Contract.comparingValues
               Contract.query "line-break-escape"
                   "SELECT REGEXP_SUBSTR('a\\r\\nb','\\\\R'),REGEXP_REPLACE('a\\r\\nb','\\\\R','_'),REGEXP_LIKE('\\r\\n','^\\\\R$','c')"
                   |> Contract.comparingValues
               Contract.query "quoted-regex-literals"
                   "SELECT REGEXP_LIKE('a+b','^\\\\Qa+b\\\\E$','c'),REGEXP_LIKE('aaab','^\\\\Qa+b\\\\E$','c'),REGEXP_LIKE('a+b','^\\\\Qa+b','c'),REGEXP_LIKE('Eabc','^\\\\Eabc$','c'),REGEXP_LIKE(']','^[\\\\Q]\\\\E]$','c'),REGEXP_LIKE('-','^[\\\\Qa-b\\\\E]$','c')"
                   |> Contract.comparingValues
               Contract.query "word-shorthand"
                   "SELECT REGEXP_LIKE('‌','\\\\w','c'),REGEXP_LIKE('‍','\\\\w','c'),REGEXP_LIKE('‍','\\\\W','c'),REGEXP_LIKE('!','\\\\W','c'),REGEXP_LIKE('‍','[\\\\w]','c'),REGEXP_LIKE('!','[a\\\\W]','c'),REGEXP_LIKE('‍','[a\\\\W]','c'),REGEXP_LIKE(']','[\\\\Q]\\\\E\\\\W]','c'),REGEXP_LIKE('‍','[\\\\Q]\\\\E\\\\W]','c')"
                   |> Contract.comparingValues
               Contract.query "word-boundary-and-bracket-escapes"
                   "SELECT REGEXP_LIKE('a‍b','a\\\\B‍b','c'),REGEXP_LIKE('a‍b','a\\\\b‍b','c'),REGEXP_LIKE('b','[\\\\b]','c'),REGEXP_LIKE('B','[\\\\B]','c'),REGEXP_LIKE('b','[\\\\W\\\\b]','c'),REGEXP_LIKE('a','[\\\\W\\\\b]','c')"
                   |> Contract.comparingValues
               Contract.query "quoted-intervals"
                   "SELECT REGEXP_LIKE('{3,2}','\\\\Q{3,2}\\\\E','c'),REGEXP_LIKE('{3,2}','\\\\Q{3,2}','c'),REGEXP_LIKE('{','\\\\Q{\\\\E','c')"
                   |> Contract.comparingValues
               Contract.query "zerofill-and-text-operands"
                   "SELECT REGEXP_LIKE(n,'^000'),n LIKE '000%',REGEXP_LIKE(s,'^00'),s LIKE '00%' FROM regex_display"
                   |> Contract.comparingValues
               Contract.query "row-varying-patterns-and-flags"
                   "SELECT id,REGEXP_LIKE(s,p,f),s REGEXP p FROM regex_dynamic ORDER BY id"
                   |> Contract.comparingValues |]
          Cleanup = [| "DROP TABLE IF EXISTS regex_dynamic"; "DROP TABLE IF EXISTS regex_display" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let private mixedTypeJoinIn =
        { Name = "mixed-type-join-in"
          Setup =
            [| "CREATE TABLE join_names(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,body TEXT,KEY(k),FULLTEXT(body))"
               "CREATE TABLE join_labels(k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci)"
               "INSERT INTO join_names VALUES(1,'①','needle'),(2,'1','needle'),(3,'other','ordinary')"
               "INSERT INTO join_labels VALUES('1')" |]
          Steps =
            [| for label, fromClause in
                   [ "names-first-straight", "join_names d STRAIGHT_JOIN join_labels o"
                     "labels-first-straight", "join_labels o STRAIGHT_JOIN join_names d"
                     "costed", "join_names d JOIN join_labels o" ] do
                   for owner in (if label = "costed" then [ "o" ] else [ "d"; "o" ]) do
                       let name = sprintf "%s-%s" label owner
                       let sql = sprintf "SELECT d.id FROM %s ON o.k=d.k WHERE %s.k IN (1,NULL) ORDER BY d.id" fromClause owner
                       Contract.query name sql
                   if label = "costed" then
                       for owner in [ "d"; "o" ] do
                           let name = sprintf "%s-%s-match" label owner
                           let sql =
                               sprintf
                                   "SELECT d.id FROM %s ON o.k=d.k WHERE %s.k IN (1,NULL) AND MATCH(d.body) AGAINST('needle') ORDER BY d.id"
                                   fromClause owner
                           Contract.query name sql
               Contract.query "single-value-in"
                   "SELECT d.id FROM join_labels o STRAIGHT_JOIN join_names d ON o.k=d.k WHERE d.k IN (1) ORDER BY d.id"
               Contract.query "mixed-value-in"
                   "SELECT d.id FROM join_names d STRAIGHT_JOIN join_labels o ON o.k=d.k WHERE o.k IN (1,'1') ORDER BY d.id"
               Contract.query "compound-on-names-first"
                   "SELECT d.id FROM join_names d STRAIGHT_JOIN join_labels o ON o.k=d.k AND d.id>0 WHERE o.k IN (1,NULL) ORDER BY d.id"
               Contract.query "compound-on-labels-first"
                   "SELECT d.id FROM join_labels o STRAIGHT_JOIN join_names d ON o.k=d.k AND d.id>0 WHERE d.k IN (1,NULL) ORDER BY d.id"
               Contract.execute "grow-filtered-source"
                   "INSERT INTO join_labels VALUES('other1'),('other2'),('other3'),('other4')"
               Contract.query "filtered-source-still-drives"
                   "SELECT d.id FROM join_names d JOIN join_labels o ON o.k=d.k WHERE o.k IN (1,NULL) ORDER BY d.id" |]
          Cleanup = [| "DROP TABLE IF EXISTS join_labels"; "DROP TABLE IF EXISTS join_names" |]
          Coverage = [| "statement:select", [| "text-differential" |] |] }

    let all =
        [| roundedFunctionalIndexes
           integralDoubleIndexProbes
           signFunctionalIndex
           isNullFunctionalIndex
           asciiFunctionalIndex
           ordFunctionalIndex
           unhexFunctionalIndex
           hexFunctionalIndex
           digestFunctionalIndexes
           constantNullFallbackIndexes
           tableStatisticsCache
           missingTableDiagnostics
           regexpPosixClasses
           mixedTypeJoinIn
           yearColumnValues
           enumSetForeignKeyBytes
           bitBinaryForeignKeyBytes
           decimalForeignKeyBytes
           yearByteForeignKeys
           timeDateForeignKeys
           qualifiedDuplicateKeys
           alterCoercion
           alterRowOrder
           hexNumericConversion
           hexExpressionConversion
           expressionAssignmentWarnings
           quotedTableNames
           foreignKeyRowValidation
           updateIgnoreConstraints
           routineUpdateWrites
           foreignKeyNames
           foreignKeyIndexesAndCollisions
           foreignKeyIndexLifecycle
           foreignKeyRenameCollisions
           foreignKeyAlterDefinitions
           foreignKeyColumnStorage
           foreignKeyGeneratedActions
           alterCopyCounts
           alterDefaultBinlogSafety
           binlogSettings
           integerCastConditions
           triggerWarningLifetimes
           deleteIgnoreForeignKeys
           bareTriggerConditions
           mutationConversion
           predicateConversion
           joinHintMerging
           joinHintLifecycle
           cteHintSyntax
           cteHintInstances
           mutationHintContext
           settingHintContext
           hintFamilyConflicts
           tableHintResolution
           mixedOptimizerHints
           routineAlterations
           routineTimeoutHints
           selectTimeoutHints
           selectTimeoutExecution
           selectTimeoutSettings
           joinCandidateTraversal
           comments
           orderAliases
           duplicateOrderAliases
           correlatedOrderAliases
           aggregateOwnership
           aggregateOrdering
           groupedAggregateInputs
           groupedAggregateFamilies
           groupConcatOrdering
           rollupEvaluation
           groupedProjectionReplay
           windowBindings
           sourceExpressionCollation
           introducedEncoding
           exactErrors
           noDirInCreate
           semanticErrors
           prepared
           preparedDml
           preparedProjectionNames
           preparedTypeHistory
           preparedUserVariables
           preparedUserAssignments
           preparedMixedAssignments
           preparedDecimalDivision
           divisionPrecisionIncrement
           approximateAggregateDescriptors
           numericAggregateConversion
           offsetRangeAggregates
           volatileWindowInputs
           storedFunctionSet
           ngramFullText
           naturalPhrases
           fullTextExpansionSeeds
           shortFullTextLookups
           naturalAggregates
           singleGroupOrdering
           temporalNumericConversion
           roundingPrecisionDescriptors
           integralRoundingDescriptors
           scientificLiteralDescriptors
           approximateExpressionDescriptors
           integerExpressionDescriptors
           divisionOperandDescriptors
           temporalArithmeticDescriptors
           calendarCasts
           componentDateTimeFractions
           binaryLiteralContexts
           unsignedNegation
           preparedSchemaChanges
           temporaryViewShadowing
           columnTypes
           generatedFunctionFamilies
           functionFamilies
           aggregateFunctions
           geographicSpatial
           namedTimeZones
           preparedInvalidation
           implicitCommit
           concurrentSessions
           contendedSchedule
           contendedDeleteAndInsert |]

    let coverage = all |> Array.collect _.Coverage

[<RequireQualifiedAccess>]
module CompatibilityRunner =
    type private PreparedContract =
        { Command: MySqlCommand
          Operation: ContractOperation }

    let private empty target status =
        { Target = target
          Status = status
          AffectedRows = 0
          Columns = [||]
          ColumnTypes = [||]
          Rows = [||]
          DataSha256 = ""
          ErrorCode = 0
          SqlState = ""
          Message = ""
          ElapsedMs = 0L }

    let private fromExecute (outcome: TargetOutcome) =
        { empty outcome.Target outcome.Status with
            AffectedRows = outcome.AffectedRows
            ErrorCode = outcome.ErrorCode
            SqlState = outcome.SqlState
            Message = outcome.Message
            ElapsedMs = outcome.ElapsedMs }

    let private fromQuery (outcome: ProbeOutcome) =
        { empty outcome.Target outcome.Status with
            Columns = outcome.Columns
            ColumnTypes = outcome.ColumnTypes
            Rows = outcome.Rows
            DataSha256 = outcome.DataSha256
            ErrorCode = outcome.ErrorCode
            SqlState = outcome.SqlState
            Message = outcome.Message
            ElapsedMs = outcome.ElapsedMs }

    let private runOperation target timeoutSeconds (connection: MySqlConnection) operation protocol sql parameters =
        task {
            match operation, protocol with
            | Execute, TextProtocol ->
                let! outcome = Database.execute target connection timeoutSeconds sql
                return fromExecute outcome
            | Execute, PreparedProtocol ->
                let! outcome = Database.executePreparedWith parameters target connection timeoutSeconds sql
                return fromExecute outcome
            | Query, TextProtocol ->
                let! outcome = Database.query target connection timeoutSeconds sql
                return fromQuery outcome
            | Query, PreparedProtocol ->
                let! outcome = Database.queryPreparedWith parameters target connection timeoutSeconds sql
                return fromQuery outcome
        }

    let private actionText =
        function
        | Run(_, TextProtocol, _, _) -> "text"
        | Run(_, PreparedProtocol, _, _) -> "prepared"
        | Send _ -> "send"
        | Reap _ -> "reap"
        | PrepareHandle _ -> "prepare"
        | InvokeHandle _ -> "execute-prepared"
        | CloseHandle _ -> "close-prepared"
        | AwaitPending _ -> "await-pending"

    let private actionSql =
        function
        | Run(_, _, sql, _)
        | Send(_, _, _, sql, _)
        | PrepareHandle(_, _, sql, _) -> sql
        | Reap _
        | InvokeHandle _
        | CloseHandle _ -> ""
        | AwaitPending _ -> ""

    let private stateQueries =
        [| "SELECT TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY TABLE_NAME"
           "SELECT TRIGGER_NAME, EVENT_MANIPULATION, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA = DATABASE() ORDER BY TRIGGER_NAME"
           "SELECT ROUTINE_NAME, ROUTINE_TYPE FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA = DATABASE() ORDER BY ROUTINE_NAME"
           "SELECT EVENT_NAME, STATUS FROM information_schema.EVENTS WHERE EVENT_SCHEMA = DATABASE() ORDER BY EVENT_NAME" |]

    let private stateFingerprint target timeoutSeconds connection =
        task {
            let parts = ResizeArray<string>()

            for sql in stateQueries do
                let! outcome = Database.query target connection timeoutSeconds sql

                if not (ProbeOutcome.succeeded outcome) then
                    failwithf "%s state probe failed: %s" target outcome.Message

                parts.Add outcome.DataSha256

            return Hashing.combine parts
        }

    let private runTarget target connectionString timeoutSeconds (case: ContractCase) =
        task {
            let builder = MySqlConnectionStringBuilder(connectionString)
            builder.AllowUserVariables <- true
            let connectionString = builder.ConnectionString
            let connections = Dictionary<string, MySqlConnection>(StringComparer.Ordinal)
            let pending = Dictionary<string, Task<ContractTargetOutcome>>(StringComparer.Ordinal)
            let prepared = Dictionary<string, PreparedContract>(StringComparer.Ordinal)

            let getConnection name =
                task {
                    match connections.TryGetValue name with
                    | true, connection -> return connection
                    | false, _ ->
                        let! connection = Database.openConnection connectionString
                        connections.Add(name, connection)
                        return connection
                }

            let outcomes = ResizeArray<ContractTargetOutcome>()

            try
                let! setup = getConnection "main"
                let! stateBefore = stateFingerprint target timeoutSeconds setup

                for sql in case.Setup do
                    let! outcome = Database.execute target setup timeoutSeconds sql

                    if not (TargetOutcome.succeeded outcome) then
                        failwithf "%s setup failed for %s: %s" target case.Name outcome.Message

                for step in case.Steps do
                    match step.Action with
                    | Run(operation, protocol, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        let! outcome = runOperation target timeoutSeconds connection operation protocol sql parameters
                        outcomes.Add outcome
                    | Send(name, operation, protocol, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        pending.Add(name, runOperation target timeoutSeconds connection operation protocol sql parameters)
                        outcomes.Add(empty target "sent")
                    | Reap name ->
                        match pending.TryGetValue name with
                        | true, operation ->
                            let! outcome = operation
                            pending.Remove name |> ignore
                            outcomes.Add outcome
                        | false, _ -> failwithf "contract %s reaps unknown operation %s" case.Name name
                    | PrepareHandle(name, operation, sql, parameters) ->
                        let! connection = getConnection step.Connection
                        let command = connection.CreateCommand()
                        command.CommandText <- sql
                        command.CommandTimeout <- timeoutSeconds

                        parameters
                        |> Array.iteri (fun index value -> command.Parameters.AddWithValue(sprintf "@p%d" index, value) |> ignore)

                        let stopwatch = Stopwatch.StartNew()

                        try
                            do! command.PrepareAsync()
                            stopwatch.Stop()
                            prepared.Add(name, { Command = command; Operation = operation })
                            outcomes.Add({ empty target "success" with ElapsedMs = stopwatch.ElapsedMilliseconds })
                        with
                        | :? MySqlException as error ->
                            stopwatch.Stop()
                            command.Dispose()

                            outcomes.Add
                                { empty target "server_error" with
                                    ErrorCode = int error.ErrorCode
                                    SqlState = error.SqlState |> Option.ofObj |> Option.defaultValue ""
                                    Message = error.Message
                                    ElapsedMs = stopwatch.ElapsedMilliseconds }
                        | error ->
                            stopwatch.Stop()
                            command.Dispose()
                            outcomes.Add { empty target "driver_error" with Message = error.ToString(); ElapsedMs = stopwatch.ElapsedMilliseconds }
                    | InvokeHandle(name, parameters) ->
                        match prepared.TryGetValue name with
                        | true, handle ->
                            parameters |> Option.iter (fun values ->
                                if values.Length <> handle.Command.Parameters.Count then
                                    invalidArg (nameof parameters) "prepared parameter count differs"
                                values |> Array.iteri (fun index value -> handle.Command.Parameters[index].Value <- value))
                            match handle.Operation with
                            | Execute ->
                                let! outcome = Database.executeCommand target timeoutSeconds handle.Command
                                outcomes.Add(fromExecute outcome)
                            | Query ->
                                let! outcome = Database.queryCommand target timeoutSeconds handle.Command
                                outcomes.Add(fromQuery outcome)
                        | false, _ -> failwithf "contract %s invokes unknown prepared handle %s" case.Name name
                    | CloseHandle name ->
                        match prepared.TryGetValue name with
                        | true, handle ->
                            handle.Command.Dispose()
                            prepared.Remove name |> ignore
                            outcomes.Add(empty target "success")
                        | false, _ -> failwithf "contract %s closes unknown prepared handle %s" case.Name name
                    | AwaitPending name ->
                        match pending.TryGetValue name with
                        | true, operation ->
                            do! Task.Delay 50

                            if operation.IsCompleted then
                                outcomes.Add
                                    { empty target "unexpected_completion" with
                                        Message = sprintf "operation %s completed before its lock owner released" name }
                            else
                                outcomes.Add(empty target "pending")
                        | false, _ -> failwithf "contract %s awaits unknown operation %s" case.Name name

                for operation in pending.Values do
                    let! _ = operation
                    ()

                let! cleanup = getConnection "main"

                for sql in case.Cleanup do
                    let! outcome = Database.execute target cleanup timeoutSeconds sql

                    if not (TargetOutcome.succeeded outcome) then
                        failwithf "%s cleanup failed for %s: %s" target case.Name outcome.Message

                let! stateAfter = stateFingerprint target timeoutSeconds cleanup
                return outcomes.ToArray(), stateBefore = stateAfter
            finally
                for handle in prepared.Values do
                    handle.Command.Dispose()

                for connection in connections.Values do
                    connection.Dispose()
        }

    let private expectationMatches expectation (outcome: ContractTargetOutcome) =
        match expectation with
        | OracleSuccess
        | OracleValueSuccessIgnoringLabels -> outcome.Status = "success"
        | OracleError(code, sqlState) -> outcome.Status = "server_error" && outcome.ErrorCode = code && outcome.SqlState = sqlState

    let private ignoresLabels = function
        | OracleValueSuccessIgnoringLabels -> true
        | _ -> false

    let private normalizeValueType = function
        | "TINYINT"
        | "SMALLINT"
        | "MEDIUMINT"
        | "INT"
        | "BIGINT"
        | "DECIMAL"
        | "FLOAT"
        | "DOUBLE" -> "NUMBER"
        | "CHAR"
        | "VARCHAR"
        | "TEXT" -> "TEXT"
        | columnType when columnType.StartsWith("CHAR(", StringComparison.Ordinal) -> "TEXT"
        | columnType -> columnType

    let private normalizeNumericRow (row: string) =
        JsonSerializer.Deserialize<string array>(row)
        |> Array.map (fun value ->
            let numericPayload =
                if value.StartsWith("integer:", StringComparison.Ordinal) then Some(value.Substring 8)
                elif value.StartsWith("decimal:", StringComparison.Ordinal) then Some(value.Substring 8)
                elif value.StartsWith("float:", StringComparison.Ordinal) then Some(value.Substring 6)
                else None

            match numericPayload with
            | Some payload ->
                match
                    Decimal.TryParse(
                        payload,
                        Globalization.NumberStyles.Float,
                        Globalization.CultureInfo.InvariantCulture
                    )
                with
                | true, number -> "number:" + number.ToString("G29", Globalization.CultureInfo.InvariantCulture)
                | false, _ -> "number:" + payload
            | None -> value)

    let private compare expectation (mysql: ContractTargetOutcome) (fsdb: ContractTargetOutcome) =
        if mysql.Status = "sent" && fsdb.Status = "sent" then
            "pass", "operation started on both targets"
        elif mysql.Status = "pending" && fsdb.Status = "pending" then
            "pass", "operation remained pending on both targets"
        elif not (expectationMatches expectation mysql) then
            "oracle_contract_drift", sprintf "oracle returned %s/%d/%s: %s" mysql.Status mysql.ErrorCode mysql.SqlState mysql.Message
        elif mysql.Status <> fsdb.Status then
            "status_mismatch", sprintf "mysql=%s fsdb=%s" mysql.Status fsdb.Status
        elif mysql.Status = "server_error" then
            if mysql.ErrorCode = fsdb.ErrorCode && mysql.SqlState = fsdb.SqlState then
                "pass", sprintf "error %d/%s matched" mysql.ErrorCode mysql.SqlState
            else
                "error_contract_mismatch", sprintf "mysql=%d/%s fsdb=%d/%s" mysql.ErrorCode mysql.SqlState fsdb.ErrorCode fsdb.SqlState
        elif mysql.Status <> "success" then
            "infrastructure", if mysql.Status <> "success" then mysql.Message else fsdb.Message
        elif not (ignoresLabels expectation) && mysql.Columns <> fsdb.Columns then
            "result_schema_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Columns fsdb.Columns
        elif expectation = OracleValueSuccessIgnoringLabels then
            let mysqlTypes = mysql.ColumnTypes |> Array.map normalizeValueType
            let fsdbTypes = fsdb.ColumnTypes |> Array.map normalizeValueType
            let mysqlRows = mysql.Rows |> Array.map normalizeNumericRow
            let fsdbRows = fsdb.Rows |> Array.map normalizeNumericRow

            if mysqlTypes <> fsdbTypes then
                "result_type_mismatch", sprintf "mysql=%A fsdb=%A" mysqlTypes fsdbTypes
            elif mysqlRows <> fsdbRows then
                "result_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Rows fsdb.Rows
            else
                "pass", "labels and compatible value type families ignored; values and remaining types matched"
        elif mysql.ColumnTypes.Length > 0 then
            let mysqlProbe =
                { ProbeOutcome.notRun "mysql" with
                    Status = mysql.Status
                    Columns = mysql.Columns
                    ColumnTypes = mysql.ColumnTypes
                    Rows = mysql.Rows
                    DataSha256 = mysql.DataSha256 }

            let fsdbProbe =
                { ProbeOutcome.notRun "fsdb" with
                    Status = fsdb.Status
                    Columns = fsdb.Columns
                    ColumnTypes = fsdb.ColumnTypes
                    Rows = fsdb.Rows
                    DataSha256 = fsdb.DataSha256 }

            match Runner.compareProbeTypes mysqlProbe fsdbProbe with
            | Some detail -> "result_type_mismatch", detail
            | None when
                (ignoresLabels expectation && mysql.Rows <> fsdb.Rows)
                || (not (ignoresLabels expectation) && mysql.DataSha256 <> fsdb.DataSha256)
                ->
                "result_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Rows fsdb.Rows
            | None -> "pass", "columns, types, and ordered rows matched"
        elif mysql.AffectedRows <> fsdb.AffectedRows then
            "affected_rows_mismatch", sprintf "mysql=%d fsdb=%d" mysql.AffectedRows fsdb.AffectedRows
        else
            "pass", sprintf "affected rows matched at %d" mysql.AffectedRows

    let private runCase mysqlConnection fsdbConnection timeoutSeconds case =
        task {
            let! mysql, mysqlStateRestored = runTarget "mysql" mysqlConnection timeoutSeconds case
            let! fsdb, fsdbStateRestored = runTarget "fsdb" fsdbConnection timeoutSeconds case

            let steps =
                Array.map3
                    (fun step mysqlOutcome fsdbOutcome ->
                        let classification, detail = compare step.Expectation mysqlOutcome fsdbOutcome

                        { Name = step.Name
                          Connection = step.Connection
                          Action = actionText step.Action
                          Sql = actionSql step.Action
                          MySql = mysqlOutcome
                          Fsdb = fsdbOutcome
                          Classification = classification
                          Detail = detail
                          Passed = classification = "pass" })
                    case.Steps
                    mysql
                    fsdb

            let stateDetail =
                match mysqlStateRestored, fsdbStateRestored with
                | true, true -> "database objects returned to their pre-case state"
                | false, true -> "MySQL contract cleanup leaked database objects"
                | true, false -> "fsdb contract cleanup leaked database objects"
                | false, false -> "both targets leaked database objects"

            return
                { Name = case.Name
                  Steps = steps
                  MySqlStateRestored = mysqlStateRestored
                  FsdbStateRestored = fsdbStateRestored
                  StateDetail = stateDetail
                  Passed = mysqlStateRestored && fsdbStateRestored && (steps |> Array.forall _.Passed) }
        }

    let run (options: CompatibilityOptions) =
        task {
            let started = DateTimeOffset.UtcNow
            let runId = Paths.uniqueRunId ()
            let directory = Path.Combine(options.ArtifactRoot, runId, "contracts")
            Directory.CreateDirectory directory |> ignore
            let databaseName = sprintf "fsdb_contract_%d_%s" Environment.ProcessId ((Hashing.text runId).Substring(0, 10))
            let! revision, dirty = Tooling.gitState ()
            let assemblyPath = typeof<Fsdb.Storage.Store>.Assembly.Location

            match! Database.createOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds with
            | Error error -> return Error error.Message
            | Ok oracleConnection ->
                Fsdb.Log.silence ()
                use subject = new FsdbSubject()
                let fsdbConnection = Runner.fsdbConnectionString subject.Port
                let mutable runResult = Error "compatibility run did not produce a result"

                try
                    use! subjectAdmin = Database.openConnection fsdbConnection
                    let! created = Database.execute "fsdb" subjectAdmin options.TimeoutSeconds (sprintf "CREATE DATABASE %s" (Database.quoteIdentifier databaseName))
                    if not (TargetOutcome.succeeded created) then failwith created.Message
                    let subjectConnection = MySqlConnectionStringBuilder(fsdbConnection)
                    subjectConnection.Database <- databaseName
                    let fsdbConnection = subjectConnection.ConnectionString
                    use! versionConnection = Database.openConnection oracleConnection
                    let! mysqlVersion = Database.scalarString versionConnection options.TimeoutSeconds "SELECT VERSION()"
                    let records = ResizeArray<ContractCaseRecord>()

                    for case in ContractCatalog.all do
                        let! record = runCase oracleConnection fsdbConnection options.TimeoutSeconds case
                        records.Add record

                    let cases = records.ToArray()
                    let firstStepFailure = cases |> Array.collect _.Steps |> Array.tryFind (fun step -> not step.Passed)
                    let firstStateFailure = cases |> Array.tryFind (fun case -> not case.MySqlStateRestored || not case.FsdbStateRestored)

                    let classification =
                        match firstStepFailure, firstStateFailure with
                        | Some failure, _ -> failure.Classification
                        | None, Some _ -> "state_leak"
                        | None, None -> "pass"

                    let signature =
                        match firstStepFailure, firstStateFailure with
                        | Some step, _ -> Hashing.combine [ step.Name; step.Classification; Hashing.text step.Sql; step.Detail ]
                        | None, Some case -> Hashing.combine [ case.Name; "state_leak"; case.StateDetail ]
                        | None, None -> ""

                    let manifest =
                        { SchemaVersion = 1
                          RunId = runId
                          StartedUtc = started.ToString("O")
                          FinishedUtc = DateTimeOffset.UtcNow.ToString("O")
                          FsdbRevision = revision
                          FsdbDirty = dirty
                          FsdbAssemblySha256 = Hashing.file assemblyPath
                          MySqlVersion = mysqlVersion
                          Cases = cases
                          Classification = classification
                          FailureSignature = signature
                          Passed = cases |> Array.forall _.Passed }

                    Json.write (Path.Combine(directory, "manifest.json")) manifest
                    runResult <- Ok(manifest, directory)
                with error ->
                    runResult <- Error error.Message

                let! dropped = Database.dropOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds

                if not (TargetOutcome.succeeded dropped) then
                    return Error("could not remove oracle database: " + dropped.Message)
                else
                    return runResult
        }
