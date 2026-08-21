module Fsdb.Torture.Tests.Program

open System
open System.IO
open System.Text
open System.Text.Json
open Expecto
open Fsdb.Ast
open Fsdb.Storage
open Fsdb.Value
open Fsdb.Torture

let private successful target =
    { Target = target
      Status = "success"
      AffectedRows = 1
      ErrorCode = 0
      SqlState = ""
      Message = ""
      ElapsedMs = 1L }

let private successfulProbe target rows =
    { Target = target
      Status = "success"
      Columns = [| "value" |]
      ColumnTypes = [| "BIGINT" |]
      Rows = rows
      DataSha256 = Hashing.text (Json.serialize rows)
      ErrorCode = 0
      SqlState = ""
      Message = ""
      ElapsedMs = 1L }

let private column name primary unique autoIncrement =
    { Name = name
      Type = TInt false
      Nullable = not primary
      Default = None
      AutoIncrement = autoIncrement
      PrimaryKey = primary
      Unique = unique
      Generated = None
      Collation = None
      Charset = None
      OnUpdateCurrentTimestamp = false }

let private emptyComparison =
    { Equal = true
      Category = "pass"
      Detail = "ok"
      MySql = { Tables = [||] }
      Fsdb = { Tables = [||] } }

let tests =
    testList
        "FSDB torture harness"
        [ testList
              "SQL script scanner"
              [ testCase "splits only normal-state semicolons"
                <| fun _ ->
                    let script =
                        "-- before;\nCREATE TABLE `odd;name` (`v` varchar(20));\nINSERT INTO `odd;name` VALUES ('a;\\'b', \"c;d\"); # after;\nSELECT 1;"

                    match SqlScript.splitText script with
                    | Error error -> failtestf "unexpected scanner error: %A" error
                    | Ok statements ->
                        Expect.equal statements.Length 3 "three statements"
                        Expect.stringContains statements.[0].Text "CREATE TABLE" "first statement"
                        Expect.stringContains statements.[1].Text "a;\\'b" "escaped quote preserved"
                        Expect.stringContains statements.[2].Text "SELECT 1" "last statement"

                testCase "reports UTF-8 byte offsets"
                <| fun _ ->
                    let script = "SELECT 'æ';\n SELECT '🚀';"

                    match SqlScript.splitText script with
                    | Error error -> failtestf "unexpected scanner error: %A" error
                    | Ok statements ->
                        let expected = Encoding.UTF8.GetByteCount("SELECT 'æ';\n ")
                        Expect.equal statements.[1].StartByte expected "offset is measured in UTF-8 bytes"

                testCase "rejects DELIMITER scripts"
                <| fun _ ->
                    match SqlScript.splitText "DELIMITER //\nSELECT 1//" with
                    | Ok _ -> failtest "DELIMITER should be rejected"
                    | Error error -> Expect.stringContains error.Message "DELIMITER" "actionable error"

                testCase "does not mistake DELIMITER data or comments for a directive"
                <| fun _ ->
                    let script = "-- DELIMITER //\nSELECT 'DELIMITER $$';"

                    match SqlScript.splitText script with
                    | Error error -> failtestf "unexpected scanner error: %A" error
                    | Ok statements -> Expect.equal statements.Length 1 "ordinary data remains valid"

                testCase "reports an unterminated quote"
                <| fun _ ->
                    match SqlScript.splitText "SELECT 'broken" with
                    | Ok _ -> failtest "unterminated quote should fail"
                    | Error error -> Expect.equal error.ByteOffset 7 "quote byte offset"

                testCase "reports invalid UTF-8 as a scanner error"
                <| fun _ ->
                    match SqlScript.splitBytes [| byte 'S'; 0xffuy; byte ';' |] with
                    | Ok _ -> failtest "invalid UTF-8 should fail"
                    | Error error ->
                        Expect.equal error.ByteOffset 1 "invalid byte offset"
                        Expect.stringContains error.Message "UTF-8" "actionable error" ]

          testList
              "Canonicalization and comparison"
              [ testCase "keeps NULL, empty text, decimal, and binary distinct"
                <| fun _ ->
                    Expect.equal (Database.canonicalCell "varchar(20)" DBNull.Value) "null" "NULL"
                    Expect.equal (Database.canonicalCell "varchar(20)" "") "text:" "empty text"
                    Expect.equal (Database.canonicalCell "decimal(10,2)" 12.30M) "decimal:12.30" "decimal"
                    Expect.equal (Database.canonicalCell "blob" [| 0uy; 255uy |]) "bytes:00ff" "binary"

                testCase "normalizes connector booleans to their numeric SQL value"
                <| fun _ ->
                    Expect.equal (Database.canonicalCell "tinyint" true) "integer:1" "true"
                    Expect.equal (Database.canonicalCell "tinyint" false) "integer:0" "false"

                testCase "removes an implicit FK index and its matching MUL column marker together"
                <| fun _ ->
                    let column =
                        { Name = "parent_id"
                          Type = "bigint"
                          Nullable = false
                          Key = "MUL"
                          DefaultValue = "null"
                          Extra = "" }

                    let index =
                        { Name = "fk_parent"
                          Unique = false
                          Sequence = 1
                          Column = "parent_id" }

                    let foreignKey =
                        { Name = "fk_parent"
                          Sequence = 1
                          Column = "parent_id"
                          ReferencedTable = "parents"
                          ReferencedColumn = "id" }

                    let columns, indexes =
                        Database.normalizeImplementationMetadata [| column |] [| index |] [| foreignKey |]

                    Expect.equal indexes [||] "the InnoDB-only FK index is ignored"
                    Expect.equal columns.[0].Key "" "its SHOW COLUMNS marker is ignored with it"

                    let explicitIndex = { index with Name = "idx_parent" }

                    let explicitColumns, explicitIndexes =
                        Database.normalizeImplementationMetadata [| column |] [| explicitIndex |] [| foreignKey |]

                    Expect.equal explicitIndexes [| explicitIndex |] "an explicitly named index is retained"
                    Expect.equal explicitColumns.[0].Key "MUL" "its SHOW COLUMNS marker is retained"

                testCase "canonicalizes JSON structure rather than formatting"
                <| fun _ ->
                    let first = Database.canonicalCell "json" "{\"b\": [2, 1], \"a\": 1.0}"
                    let second = Database.canonicalCell "json" "{ \"a\" : 1, \"b\" : [2,1] }"
                    Expect.equal first second "object order, whitespace, and equivalent numbers do not diverge"

                testCase "row digest mismatch reports data category"
                <| fun _ ->
                    let table hash row =
                        { Name = "t"
                          Columns = [||]
                          Indexes = [||]
                          ForeignKeys = [||]
                          RowCount = 1
                          DataSha256 = hash
                          DataChunks =
                            [| { Index = 0
                                 StartRow = 0L
                                 RowCount = 1
                                 Sha256 = hash
                                 FirstRow = row
                                 LastRow = row } |]
                          Rows = [| row |] }

                    let result =
                        Comparison.compare
                            { Tables = [| table "left" "[1]" |] }
                            { Tables = [| table "right" "[2]" |] }

                    Expect.isFalse result.Equal "comparison differs"
                    Expect.equal result.Category "data_mismatch" "specific category"

                testCase "large row digests retain bounded evidence"
                <| fun _ ->
                    let rowCount, digest, chunks, samples =
                        seq { 1..100000 }
                        |> Seq.map (sprintf "row-%06d")
                        |> Database.digestRows

                    Expect.equal rowCount 100000L "all rows contribute"
                    Expect.equal digest.Length 64 "digest is SHA-256"
                    Expect.equal chunks.Length 25 "chunks localize mismatches without retaining every row"
                    Expect.equal samples.Length 16 "only edge samples are retained"
                    Expect.equal chunks.[0].StartRow 0L "first chunk offset"
                    Expect.equal chunks.[24].RowCount 1696 "short final chunk"

                testCase "result type comparison folds widths but preserves numeric families"
                <| fun _ ->
                    let outcome target typeName row =
                        { successfulProbe target [| Json.serialize [| row |] |] with
                            ColumnTypes = [| typeName |] }

                    Expect.isNone
                        (Runner.compareProbeTypes (outcome "mysql" "INT" "integer:1") (outcome "fsdb" "BIGINT" "integer:1"))
                        "integer widths are equivalent"

                    Expect.isNone
                        (Runner.compareProbeTypes (outcome "mysql" "CHAR(8)" "text:value") (outcome "fsdb" "VARCHAR" "text:value"))
                        "size-decorated textual types are equivalent"

                    Expect.isSome
                        (Runner.compareProbeTypes (outcome "mysql" "BIGINT" "integer:2") (outcome "fsdb" "DOUBLE" "integer:2"))
                        "integer and floating-point metadata remain distinct"

                    let missingType =
                        { outcome "fsdb" "BIGINT" "integer:1" with
                            ColumnTypes = [||] }

                    Expect.isSome
                        (Runner.compareProbeTypes (outcome "mysql" "BIGINT" "integer:1") missingType)
                        "type metadata cardinality remains observable"

                testCase "result type comparison ignores an entirely NULL column"
                <| fun _ ->
                    let outcome target typeName =
                        { successfulProbe target [| Json.serialize [| "null" |] |] with
                            ColumnTypes = [| typeName |] }

                    Expect.isNone
                        (Runner.compareProbeTypes (outcome "mysql" "DECIMAL") (outcome "fsdb" "VARCHAR"))
                        "NULL-only output has no value-level type distinction" ]

          testList
              "Classification"
              [ testCase "parser rejection is distinct from execution rejection"
                <| fun _ ->
                    let result = Runner.classifyStatement "error" false (successful "mysql") (TargetOutcome.notRun "fsdb") [||]
                    Expect.equal result "fsdb_parser_gap" "parser category"

                testCase "contained 1105 is its own category"
                <| fun _ ->
                    let failed =
                        { TargetOutcome.notRun "fsdb" with
                            Status = "server_error"
                            ErrorCode = 1105 }

                    let result = Runner.classifyStatement "ok" false (successful "mysql") failed [||]
                    Expect.equal result "contained_internal_error" "internal error category"

                testCase "oracle and FSDB timeouts remain distinguishable"
                <| fun _ ->
                    let timeout target =
                        { TargetOutcome.notRun target with
                            Status = "timeout" }

                    Expect.equal
                        (Runner.classifyStatement "ok" false (timeout "mysql") (TargetOutcome.notRun "fsdb") [||])
                        "oracle_timeout"
                        "oracle timeout"

                    Expect.equal
                        (Runner.classifyStatement "ok" false (successful "mysql") (timeout "fsdb") [||])
                        "fsdb_timeout"
                        "subject timeout"

                testCase "failure signatures are stable and evidence-sensitive"
                <| fun _ ->
                    let first = Runner.failureSignature "data_mismatch" "stmt" 0 0 "row 1"
                    let repeated = Runner.failureSignature "data_mismatch" "stmt" 0 0 "row 1"
                    let changed = Runner.failureSignature "data_mismatch" "stmt" 0 0 "row 2"
                    Expect.equal first repeated "same evidence"
                    Expect.notEqual first changed "changed evidence"

                testCase "probe row differences are semantic findings"
                <| fun _ ->
                    let mysql = successfulProbe "mysql" [| "[1]" |]
                    let fsdb = successfulProbe "fsdb" [| "[2]" |]
                    let result = Runner.classifyProbe "ok" mysql fsdb
                    Expect.equal result "probe_result_mismatch" "ordered query results differ"

                testCase "probe type differences have a distinct classification"
                <| fun _ ->
                    let mysql = { successfulProbe "mysql" [| "[1]" |] with ColumnTypes = [| "BIGINT" |] }
                    let fsdb = { successfulProbe "fsdb" [| "[1]" |] with ColumnTypes = [| "DOUBLE" |] }
                    Expect.equal (Runner.classifyProbe "ok" mysql fsdb) "probe_type_mismatch" "types differ despite equal values"

                testCase "affected rows are compared only when requested"
                <| fun _ ->
                    let mysql = { successful "mysql" with AffectedRows = 2 }
                    let fsdb = { successful "fsdb" with AffectedRows = 1 }

                    Expect.equal
                        (Runner.classifyStatement "ok" true mysql fsdb [||])
                        "statement_affected_rows_mismatch"
                        "generated mutations compare counts"

                    Expect.equal (Runner.classifyStatement "ok" false mysql fsdb [||]) "pass" "DDL ignores count conventions"
                    Expect.equal (DmlBattery.classify mysql fsdb) "dml_affected_rows_mismatch" "ordered DML compares counts" ]

          testList
              "Toolchain"
              [ testCase "pinned toolchain declarations agree"
                <| fun _ ->
                    let root = Paths.tortureRoot ()
                    use config = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "config", "toolchain.json")))
                    let version = config.RootElement.GetProperty("sql_splitter_version").GetString()
                    let image = config.RootElement.GetProperty("mysql_image").GetString()
                    Expect.equal version Tooling.RequiredSqlSplitterVersion "harness and config generator versions"

                    Expect.stringContains
                        (File.ReadAllText(Path.Combine(root, "scripts", "bootstrap.sh")))
                        ("--version " + version)
                        "bootstrap version"

                    Expect.stringContains
                        (File.ReadAllText(Path.Combine(root, "compose.yaml")))
                        image
                        "Compose image and digest"

                testCase "an invalid explicit SQL Splitter path does not fall back to PATH"
                <| fun _ ->
                    let missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "sql-splitter")

                    match Tooling.resolveSqlSplitter missing with
                    | Ok path -> failtestf "unexpected fallback to %s" path
                    | Error error -> Expect.stringContains error "does not exist" "explicit configuration is authoritative" ]

          testCase "the FSDB subject connection selects the default database"
          <| fun _ ->
              let builder = MySqlConnector.MySqlConnectionStringBuilder(Runner.fsdbConnectionString 3307)
              Expect.equal builder.Database defaultDatabase "DATABASE() must see the same selected schema as the oracle"

          testList
              "Concurrency workload"
              [ let options =
                    { Seed = 73UL
                      Workers = 4
                      OperationsPerWorker = 10
                      Accounts = 16
                      HotAccounts = 4
                      RollbackEvery = 5
                      TimeoutSeconds = 10
                      ArtifactRoot = "/tmp"
                      MySqlConnection = "unused" }

                testCase "plan is deterministic, unique, hot, and never transfers to itself"
                <| fun _ ->
                    let first = ConcurrencyWorkload.plan options
                    let repeated = ConcurrencyWorkload.plan options
                    Expect.equal first repeated "seed and coordinates fully determine the workload"
                    Expect.equal first.Length 40 "workers times operations"
                    Expect.equal (first |> Array.distinctBy _.OperationId |> Array.length) first.Length "operation ids are unique"
                    Expect.isTrue
                        (first
                         |> Array.forall (fun operation ->
                             operation.FromAccount >= 1
                             && operation.FromAccount <= options.HotAccounts
                             && operation.ToAccount >= 1
                             && operation.ToAccount <= options.HotAccounts
                             && operation.FromAccount <> operation.ToAccount))
                        "every transfer contends inside the hot set without becoming a no-op"

                testCase "expected state excludes rollbacks and conserves money"
                <| fun _ ->
                    let balances, versions, ledger = ConcurrencyWorkload.expected options
                    let committed = options.Workers * options.OperationsPerWorker - 8
                    Expect.equal ledger.Length committed "every fifth operation is absent from the committed ledger"
                    Expect.equal (Array.sum balances) (int64 options.Accounts * ConcurrencyWorkload.InitialBalance) "transfers conserve money"
                    Expect.equal (Array.sum versions) (int64 committed * 2L) "each committed transfer updates exactly two versions" ]

          testList
              "Catalog invariants"
              [ testCase "accepts a valid primary key and auto-id"
                <| fun _ ->
                    let store = Fsdb.Storage.create ()
                    let table =
                        { OriginalName = "items"
                          Columns = [ column "id" true false true ]
                          RowsArray = Fsdb.RowStore.ofSeq [ [| VInt 1L |]; [| VInt 2L |] ]
                          NextAutoId = 3L
                          Indexes = []
                          ForeignKeys = []
                          TableCharset = None
                          TableCollation = None
                          CreateTime = DateTime.UtcNow
                          UniqueIndex = Map.empty
                          SecondaryIndex = Map.empty }

                    store.Catalog <- Map.ofList [ defaultDatabase, Map.ofList [ "items", table ] ]
                    Expect.isEmpty (Invariants.validate store) "valid store"

                testCase "reports arity, duplicate key, and stale auto-id together"
                <| fun _ ->
                    let store = Fsdb.Storage.create ()
                    let table =
                        { OriginalName = "items"
                          Columns = [ column "id" true false true ]
                          RowsArray = Fsdb.RowStore.ofSeq [ [| VInt 1L |]; [| VInt 1L |]; [| VInt 2L; VInt 3L |] ]
                          NextAutoId = 1L
                          Indexes = []
                          ForeignKeys = []
                          TableCharset = None
                          TableCollation = None
                          CreateTime = DateTime.UtcNow
                          UniqueIndex = Map.empty
                          SecondaryIndex = Map.empty }

                    store.Catalog <- Map.ofList [ defaultDatabase, Map.ofList [ "items", table ] ]
                    let errors = Invariants.validate store
                    Expect.isTrue (errors |> Array.exists (fun error -> error.Contains "arity")) "arity"
                    Expect.isTrue (errors |> Array.exists (fun error -> error.Contains "duplicate")) "duplicate"
                    Expect.isTrue (errors |> Array.exists (fun error -> error.Contains "NextAutoId")) "auto-id" ]

          testCase "manifest JSON round-trips"
          <| fun _ ->
              let tool =
                  { Name = "sql-splitter"
                    Version = "1.21.0"
                    Path = "/tmp/sql-splitter"
                    Sha256 = "abc" }

              let manifest =
                  let dml =
                      { Mode = "changed_rows"
                        Index = 0
                        Name = "odku_changed"
                        Sql = "INSERT INTO t VALUES (1) ON DUPLICATE KEY UPDATE id = 2"
                        SqlSha256 = "sql"
                        MySql = { successful "mysql" with AffectedRows = 2 }
                        Fsdb = { successful "fsdb" with AffectedRows = 1 }
                        Classification = "dml_affected_rows_mismatch"
                        Equal = false
                        Detail = "affected rows differ" }

                  { SchemaVersion = 3
                    RunId = "run"
                    CaseId = "case"
                    Scenario = "scalar"
                    Seed = 1UL
                    Scale = 1UL
                    MaxRows = 8UL
                    BatchSize = 1UL
                    InvariantEvery = 1
                    TimeoutSeconds = 10
                    StartedUtc = "start"
                    FinishedUtc = "finish"
                    FsdbRevision = "rev"
                    FsdbDirty = false
                    FsdbAssemblySha256 = "assembly"
                    SqlSplitter = tool
                    MySqlVersion = "8.4"
                    ModelPath = "model"
                    ModelSha256 = "model-hash"
                    GeneratedSqlSha256 = "sql-hash"
                    GeneratedSqlBytes = 123L
                    GeneratorExitCode = 0
                    GeneratorElapsedMs = 12L
                    GeneratorTimedOut = false
                    GeneratorDiagnostics = "{}"
                    MySqlLoadElapsedMs = 10L
                    FsdbLoadElapsedMs = 11L
                    InvariantElapsedMs = 2L
                    MySqlSnapshotElapsedMs = 3L
                    FsdbSnapshotElapsedMs = 4L
                    PeakWorkingSetBytes = 1024L
                    Statements = [||]
                    Dml = [| dml |]
                    Probes = [||]
                    Comparison = emptyComparison
                    Classification = "pass"
                    ClassificationDetail = "all evidence matched"
                    FailureSignature = ""
                    KnownGap = false
                    Passed = true }

              let parsed = Json.deserialize<RunManifest>(Json.serialize manifest)
              Expect.equal parsed.CaseId manifest.CaseId "case id"
              Expect.equal parsed.SqlSplitter.Version "1.21.0" "nested record"
              Expect.equal parsed.Dml manifest.Dml "DML evidence" ]

[<EntryPoint>]
let main argv = Tests.runTestsWithCLIArgs [] argv tests
