namespace Fsdb.Torture

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
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
    | OracleError of code: int * sqlState: string

type ContractAction =
    | Run of operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Send of pending: string * operation: ContractOperation * protocol: ContractProtocol * sql: string * parameters: obj array
    | Reap of pending: string
    | PrepareHandle of handle: string * operation: ContractOperation * sql: string * parameters: obj array
    | InvokeHandle of handle: string
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
          Action = InvokeHandle handle
          Expectation = expectation }

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

    let private exactErrors =
        { Name = "syntax-error-contracts"
          Setup = [||]
          Steps =
            [| Contract.query "unterminated-expression" "SELECT (1" |> Contract.fails 1064 "42000"
               Contract.query "missing-table-reference" "SELECT 1 FROM" |> Contract.fails 1064 "42000"
               Contract.query "dash-is-an-operator" "SELECT 1--missing" |> Contract.fails 1054 "42S22" |]
          Cleanup = [||]
          Coverage = [| "statement:select", [| "error-contract" |]; "syntax:comments", [| "error-contract" |] |] }

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
                   [| box 1 |] |]
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
                   "SELECT c_tiny, c_bool, c_small, c_medium, c_int, c_big, c_bit, c_decimal, c_double, c_float, c_year FROM contract_types WHERE id = ?"
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-text-binary-row"
                   "SELECT c_char, c_varchar, c_tinytext, c_text, c_mediumtext, c_longtext, c_binary, c_varbinary, c_tinyblob, c_blob, c_mediumblob, c_longblob, c_enum, c_set, c_json FROM contract_types WHERE id = ?"
                   [| box 1 |]
               Contract.preparedQuery
                   "prepared-temporal-row"
                   "SELECT c_date, c_datetime, c_timestamp, c_time FROM contract_types WHERE id = ?"
                   [| box 1 |]
               Contract.preparedQuery "prepared-geometry-row" "SELECT c_geometry FROM contract_types WHERE id = ?" [| box 1 |] |]
          Cleanup = [| "DROP TABLE IF EXISTS contract_types" |]
          Coverage =
            TypeMatrix.capabilities
            |> Array.map (fun name -> "column-type:" + name, [| "parser"; "text-differential"; "prepared-protocol" |]) }

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

    let all =
        [| comments
           exactErrors
           semanticErrors
           prepared
           preparedDml
           columnTypes
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
                    | InvokeHandle name ->
                        match prepared.TryGetValue name with
                        | true, handle ->
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
        | OracleSuccess -> outcome.Status = "success"
        | OracleError(code, sqlState) -> outcome.Status = "server_error" && outcome.ErrorCode = code && outcome.SqlState = sqlState

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
        elif mysql.Columns <> fsdb.Columns then
            "result_schema_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Columns fsdb.Columns
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
            | None when mysql.DataSha256 <> fsdb.DataSha256 -> "result_mismatch", sprintf "mysql=%A fsdb=%A" mysql.Rows fsdb.Rows
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
                let! dropped = Database.dropOracleDatabase options.MySqlConnection databaseName options.TimeoutSeconds

                if not (TargetOutcome.succeeded dropped) then
                    return Error("could not remove oracle database: " + dropped.Message)
                else
                    return Ok(manifest, directory)
        }
