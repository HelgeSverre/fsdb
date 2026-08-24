/// Every workload side-by-side: BenchmarkDotNet's [<Params>] runs each
/// [<Benchmark>] once per Target (fsdb and MySQL on their configured ports), producing
/// one table with both servers under identical data and queries.
///
/// BenchmarkDotNet launches a fresh process per (Target x Benchmark method)
/// case, so GlobalSetup gives both targets the same freshly seeded database.
/// fsdb restarts because its catalog is in memory; MySQL keeps its server but
/// drops and recreates the benchmark database. Mutation cases therefore
/// cannot change the row count or values seen by a later case.
module Fsdb.Benchmarks.ServerBenchmarks

open System
open System.Diagnostics
open System.IO
open System.Threading
open BenchmarkDotNet.Attributes
open MySqlConnector
open Fsdb.Benchmarks.BenchServer
open Fsdb.Benchmarks.Schema

[<MemoryDiagnoser>]
type ServerBenchmarks() =

    let mutable conn : MySqlConnection = Unchecked.defaultof<_>
    // A second connection authenticated as the SELECT-only `bench_reader`
    // account (created in Setup) — statements on it exercise the db-level
    // privilege-check path instead of root's all-global fast path.
    let mutable limitedConn : MySqlConnection = Unchecked.defaultof<_>
    let mutable fsdbProcess : Process option = None
    let mutable dataDir : string option = None
    let mutable rng = Random(1234)
    let mutable insertCounter = 0

    // Draw from the seeded id range so every read hits a real row.
    let randomUserId () = rng.Next(1, Schema.userCount + 1)

    // The durability-matched run (`just bench-durable`) adds `fsdb-wal` and
    // `mysql-nofsync` so each engine is measured with and without the fsync
    // cost its writes actually pay.
    member this.Targets() : string[] =
        if BenchServer.isDurableRun () then
            [| "fsdb"; "fsdb-wal"; "mysql"; "mysql-nofsync" |]
        else
            [| "fsdb"; "mysql" |]

    [<ParamsSource("Targets")>]
    member val Target = "" with get, set

    [<GlobalSetup>]
    member this.Setup() =
        if this.Target = "fsdb" then
            fsdbProcess <- Some(BenchServer.startFsdb (BenchServer.benchBin ()) None)
        elif this.Target = "fsdb-wal" then
            let dir = BenchServer.tempDataDir ()
            dataDir <- Some dir
            fsdbProcess <- Some(BenchServer.startFsdb (BenchServer.benchBin ()) (Some dir))
        else
            BenchServer.resetAndSeed this.Target

        conn <- new MySqlConnection(Schema.connectionString this.Target)
        conn.Open()

        // The limited account the auth/enforcement benchmarks use — created
        // idempotently per case (fsdb restarts fresh; the long-lived mysql
        // just re-creates it).
        for sql in
            [ "DROP USER IF EXISTS 'bench_reader'@'%'"
              "CREATE USER 'bench_reader'@'%' IDENTIFIED BY 'benchpw'"
              "GRANT SELECT ON fsdb_bench.* TO 'bench_reader'@'%'" ] do
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            cmd.ExecuteNonQuery() |> ignore

        limitedConn <- new MySqlConnection(Schema.userConnectionString this.Target "bench_reader" "benchpw")
        limitedConn.Open()
        rng <- Random(1234)
        insertCounter <- 0

    [<GlobalCleanup>]
    member this.Cleanup() =
        limitedConn.Dispose()
        conn.Dispose()

        if this.Target = "fsdb" || this.Target = "fsdb-wal" then
            fsdbProcess |> Option.iter BenchServer.stopFsdb
            fsdbProcess <- None

            dataDir |> Option.iter (fun d ->
                (try
                    Directory.Delete(d, true)
                 with _ ->
                     ()))

            dataDir <- None

    member private this.Exec(sql: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    member private this.Query(sql: string) =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            ()

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.PointSelectByPk() =
        this.Query $"SELECT id, name, email, age, meta, created_at FROM users WHERE id = {randomUserId ()}"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.FilterScanOrderLimit() =
        this.Query
            "SELECT id, name, age, created_at FROM users WHERE age > 40 ORDER BY created_at DESC LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.FilterBySecondaryEquality() =
        this.Query "SELECT id, name, age FROM users WHERE age = 30"

    [<Benchmark>]
    [<BenchmarkCategory("Scale", "SecondaryRange")>]
    member this.FilterBySecondaryRange() =
        let lower = randomUserId () - 1
        this.Query $"SELECT id, name FROM users WHERE sort_key >= {lower} AND sort_key < {lower + 1}"

    [<Benchmark>]
    [<BenchmarkCategory("Scale", "SecondaryOrder")>]
    member this.OrderBySecondaryRange() =
        let lower = rng.Next(1, max 2 (Schema.userCount - 63))
        this.Query $"SELECT id, sort_key FROM users WHERE sort_key >= {lower} AND sort_key < {lower + 64} ORDER BY sort_key ASC LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.InsertSingle() =
        let i = Interlocked.Increment(&insertCounter)

        this.Exec(
            "INSERT INTO users (name, email, age, meta, created_at) VALUES "
            + $"('bench_ins_{i}','bench_ins_{i}@bench.test',30,'{{\"plan\":\"free\"}}','2024-01-01 00:00:00')"
        )

    [<Benchmark>]
    member this.InsertBatch100() =
        let baseId = Interlocked.Add(&insertCounter, 100) - 100

        let rows =
            [ for j in 0..99 ->
                let i = baseId + j
                $"('bench_batch_{i}','bench_batch_{i}@bench.test',30,'{{\"plan\":\"free\"}}','2024-01-01 00:00:00')" ]

        this.Exec("INSERT INTO users (name, email, age, meta, created_at) VALUES " + String.Join(",", rows))

    [<Benchmark>]
    member this.ReplaceNewRow() =
        let i = Interlocked.Increment(&insertCounter)

        this.Exec(
            "REPLACE INTO users (name, email, age, meta, created_at) VALUES "
            + $"('bench_replace_{i}','bench_replace_{i}@bench.test',30,'{{\"plan\":\"free\"}}','2024-01-01 00:00:00')"
        )

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.ReplaceExistingByPk() =
        let id = randomUserId ()
        let seedIndex = id - 1

        this.Exec(
            $"REPLACE INTO users (id, name, email, age, meta, created_at) VALUES ({id},'user_{seedIndex}','user_{seedIndex}@bench.test',30,'{{\"plan\":\"free\"}}','2024-01-01 00:00:00')"
        )

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.UpdateSingleRow() =
        this.Exec $"UPDATE users SET age = age + 1 WHERE id = {randomUserId ()}"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.JoinUsersOrders() =
        this.Query(
            "SELECT u.id, u.name, o.id, o.total, o.status "
            + "FROM users u JOIN orders o ON o.user_id = u.id "
            + "WHERE u.age > 30 LIMIT 50"
        )

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.UncorrelatedInSubquery() =
        this.Query "SELECT u.id, u.name FROM users u WHERE u.id IN (SELECT o.user_id FROM orders o WHERE o.id <= 100)"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.GroupByAggregate() =
        this.Query "SELECT status, COUNT(*), SUM(total) FROM orders GROUP BY status"

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.JsonExtract() =
        this.Query "SELECT id, name FROM users WHERE meta->>'$.plan' = 'pro' LIMIT 20"

    [<Benchmark>]
    member this.PreparedPointSelect() =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- "SELECT id, name, email, age FROM users WHERE id = @id"
        cmd.Parameters.AddWithValue("@id", randomUserId ()) |> ignore
        cmd.Prepare()
        use reader = cmd.ExecuteReader()

        while reader.Read() do
            ()

    [<Benchmark>]
    [<BenchmarkCategory("Scale")>]
    member this.UpdateByNonIndexed() =
        // `name` has no index, so the WHERE narrows to nothing and the UPDATE
        // pays the full-table scan — the O(n) write shape the PK-narrowed
        // `UpdateSingleRow` never exercises.
        this.Exec $"UPDATE users SET age = age + 1 WHERE name = 'user_{randomUserId () - 1}'"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.UpsertExistingByPk() =
        let id = randomUserId ()
        let seedIndex = id - 1

        this.Exec(
            $"INSERT INTO users (id, name, email, age, meta, created_at) VALUES ({id},'user_{seedIndex}','user_{seedIndex}@bench.test',30,'{{\"plan\":\"free\"}}','2024-01-01 00:00:00') "
            + "ON DUPLICATE KEY UPDATE age = age + 1"
        )

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.TransactionTwoPointUpdates() =
        this.Exec "START TRANSACTION"

        try
            this.Exec $"UPDATE users SET age = age + 1 WHERE id = {randomUserId ()}"
            this.Exec $"UPDATE users SET age = age + 1 WHERE id = {randomUserId ()}"
            this.Exec "COMMIT"
        with _ ->
            this.Exec "ROLLBACK"
            reraise ()

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.ComputedProjection() =
        this.Query $"SELECT id, age * 2 AS doubled, CONCAT(name, '/', email) AS label FROM users WHERE id = {randomUserId ()}"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.ViewFilterLimit() =
        this.Query "SELECT id, name, email, age FROM adult_users ORDER BY id LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.ViewAggregate() =
        this.Query "SELECT status, order_count, total FROM order_totals ORDER BY status"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.RecursiveCte100() =
        this.Query "WITH RECURSIVE numbers (n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 100) SELECT COUNT(*), SUM(n) FROM numbers"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    [<BenchmarkCategory("Scale")>]
    member this.WindowTopOrders() =
        this.Query "SELECT id, status, total, ROW_NUMBER() OVER (PARTITION BY status ORDER BY total DESC, id) AS rn FROM orders ORDER BY status, rn LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.CorrelatedJsonTable() =
        this.Query "SELECT u.id, jt.plan FROM users u JOIN JSON_TABLE(u.meta, '$' COLUMNS (plan VARCHAR(20) PATH '$.plan')) jt ON 1 WHERE u.id <= 100 ORDER BY u.id"

    [<Benchmark>]
    [<BenchmarkCategory("Feature", "Scale", "FullText")>]
    member this.FullTextNaturalSearch() =
        this.Query "SELECT id, title FROM articles WHERE MATCH(title, body) AGAINST ('database') LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature", "Scale", "FullText")>]
    member this.FullTextBooleanSearch() =
        this.Query "SELECT id, title FROM articles WHERE MATCH(title, body) AGAINST ('+database +concurrency' IN BOOLEAN MODE) LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature", "Scale", "FullText")>]
    member this.FullTextAccentSearch() =
        this.Query "SELECT id, title FROM articles WHERE MATCH(title, body) AGAINST ('resume') LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature", "Scale", "FullText")>]
    member this.FullTextBooleanPrefixSearch() =
        this.Query "SELECT id, title FROM articles WHERE MATCH(title, body) AGAINST ('+resu*' IN BOOLEAN MODE) LIMIT 20"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.InsertCheckedGenerated() =
        let i = Interlocked.Increment(&insertCounter)
        this.Exec $"INSERT INTO checked_values (value) VALUES ({i % 1000})"

    [<Benchmark>]
    [<BenchmarkCategory("Feature")>]
    member this.InsertWithAfterTrigger() =
        let i = Interlocked.Increment(&insertCounter)
        this.Exec $"INSERT INTO trigger_source (value) VALUES ({i})"

    // -----------------------------------------------------------------------
    // Auth / accounts / information_schema — the GUI-client and user-system
    // surfaces. Each has a real per-statement or per-connection cost now
    // (privilege checks read mysql.* rows, the handshake verifies
    // credentials), so they're measured against MySQL like everything else.
    // -----------------------------------------------------------------------

    [<Benchmark>]
    member this.PointSelectAsLimitedUser() =
        // Same query as PointSelectByPk but as the SELECT-only account:
        // measures the db-level privilege-check path (root short-circuits on
        // its all-global row).
        use cmd = limitedConn.CreateCommand()
        cmd.CommandText <- $"SELECT id, name, email, age, meta, created_at FROM users WHERE id = {randomUserId ()}"
        use reader = cmd.ExecuteReader()

        while reader.Read() do
            ()

    [<Benchmark>]
    member this.SelectCurrentUser() =
        this.Query "SELECT CURRENT_USER(), USER()"

    [<Benchmark>]
    member this.ShowGrants() =
        this.Query "SHOW GRANTS FOR 'bench_reader'@'%'"

    [<Benchmark>]
    member this.ShowFullTables() =
        this.Query "SHOW FULL TABLES"

    [<Benchmark>]
    member this.InfoSchemaColumnsForTable() =
        // TablePlus/phpMyAdmin's structure-pane query shape.
        this.Query
            "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = 'fsdb_bench' AND TABLE_NAME = 'users' ORDER BY ORDINAL_POSITION"

    [<Benchmark>]
    member this.InfoSchemaTablesScan() =
        this.Query "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_ROWS FROM information_schema.TABLES"

    [<Benchmark>]
    member this.UserPrivilegesScan() =
        this.Query "SELECT GRANTEE, PRIVILEGE_TYPE, IS_GRANTABLE FROM information_schema.USER_PRIVILEGES"

    [<Benchmark>]
    member this.CreateGrantDropUser() =
        // The account-DDL round trip: three statements mutating mysql.user /
        // mysql.db, unique name per invocation.
        let i = Interlocked.Increment(&insertCounter)
        this.Exec $"CREATE USER 'bench_u_{i}'@'%%' IDENTIFIED BY 'pw_{i}'"
        this.Exec $"GRANT SELECT, INSERT ON fsdb_bench.* TO 'bench_u_{i}'@'%%'"
        this.Exec $"DROP USER 'bench_u_{i}'@'%%'"

/// The full connect + authenticated-handshake + teardown cycle, in its own
/// class because it needs a *fixed, small* invocation count: each op leaves
/// a loopback socket in TIME_WAIT, and BenchmarkDotNet's auto-pilot would
/// otherwise scale to thousands of ops per iteration and exhaust the
/// ephemeral port range — which is exactly what poisoned every following
/// benchmark case on the first run of this suite.
[<MemoryDiagnoser>]
[<InvocationCount(32, 1)>]
type ConnectBenchmarks() =

    let mutable fsdbProcess : Process option = None
    let mutable dataDir : string option = None

    member this.Targets() : string[] =
        if BenchServer.isDurableRun () then
            [| "fsdb"; "fsdb-wal"; "mysql"; "mysql-nofsync" |]
        else
            [| "fsdb"; "mysql" |]

    [<ParamsSource("Targets")>]
    member val Target = "" with get, set

    [<GlobalSetup>]
    member this.Setup() =
        if this.Target = "fsdb" then
            fsdbProcess <- Some(BenchServer.startFsdb (BenchServer.benchBin ()) None)
        elif this.Target = "fsdb-wal" then
            let dir = BenchServer.tempDataDir ()
            dataDir <- Some dir
            fsdbProcess <- Some(BenchServer.startFsdb (BenchServer.benchBin ()) (Some dir))
        else
            BenchServer.resetAndSeed this.Target

        // The password-verified account the connect cycle authenticates as.
        use conn = new MySqlConnection(Schema.connectionString this.Target)
        conn.Open()

        for sql in
            [ "DROP USER IF EXISTS 'bench_reader'@'%'"
              "CREATE USER 'bench_reader'@'%' IDENTIFIED BY 'benchpw'"
              "GRANT SELECT ON fsdb_bench.* TO 'bench_reader'@'%'" ] do
            use cmd = conn.CreateCommand()
            cmd.CommandText <- sql
            cmd.ExecuteNonQuery() |> ignore

    [<GlobalCleanup>]
    member this.Cleanup() =
        if this.Target = "fsdb" || this.Target = "fsdb-wal" then
            fsdbProcess |> Option.iter BenchServer.stopFsdb
            fsdbProcess <- None

            dataDir |> Option.iter (fun dir ->
                try
                    Directory.Delete(dir, true)
                with _ ->
                    ())

            dataDir <- None

    [<Benchmark>]
    member this.ConnectAuthenticateClose() =
        // A fresh TCP connect + mysql_native_password/caching_sha2 handshake
        // as the password-verified account, then teardown (Pooling=false
        // makes each Open a real handshake).
        use c = new MySqlConnection(Schema.userConnectionString this.Target "bench_reader" "benchpw")
        c.Open()
