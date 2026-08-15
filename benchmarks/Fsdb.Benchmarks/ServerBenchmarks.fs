/// Every workload side-by-side: BenchmarkDotNet's [<Params>] runs each
/// [<Benchmark>] once per Target ("fsdb" @ 3307, "mysql" @ 3316), producing
/// one table with both servers under identical data and queries.
///
/// BenchmarkDotNet launches a fresh process per (Target x Benchmark method)
/// case, so a GlobalSetup that reseeded the ~60k-row dataset would redo that
/// work 9x per target. Program.fs seeds each target's database exactly once
/// before the runner starts; GlobalSetup here just opens a connection to the
/// already-seeded database.
module Fsdb.Benchmarks.ServerBenchmarks

open System
open System.Threading
open BenchmarkDotNet.Attributes
open MySqlConnector
open Fsdb.Benchmarks.Schema

[<MemoryDiagnoser>]
type ServerBenchmarks() =

    let mutable conn : MySqlConnection = Unchecked.defaultof<_>
    let mutable rng = Random(1234)
    let mutable insertCounter = 0

    // Draw from the seeded id range so every read hits a real row.
    let randomUserId () = rng.Next(1, Schema.userCount + 1)

    [<Params("fsdb", "mysql")>]
    member val Target = "" with get, set

    [<GlobalSetup>]
    member this.Setup() =
        conn <- new MySqlConnection(Schema.connectionString this.Target)
        conn.Open()
        rng <- Random(1234)
        insertCounter <- 0

    [<GlobalCleanup>]
    member this.Cleanup() = conn.Dispose()

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
    member this.PointSelectByPk() =
        this.Query $"SELECT id, name, email, age, meta, created_at FROM users WHERE id = {randomUserId ()}"

    [<Benchmark>]
    member this.FilterScanOrderLimit() =
        this.Query
            "SELECT id, name, age, created_at FROM users WHERE age > 40 ORDER BY created_at DESC LIMIT 20"

    [<Benchmark>]
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
    member this.UpdateSingleRow() =
        this.Exec $"UPDATE users SET age = age + 1 WHERE id = {randomUserId ()}"

    [<Benchmark>]
    member this.JoinUsersOrders() =
        this.Query(
            "SELECT u.id, u.name, o.id, o.total, o.status "
            + "FROM users u JOIN orders o ON o.user_id = u.id "
            + "WHERE u.age > 30 LIMIT 50"
        )

    [<Benchmark>]
    member this.GroupByAggregate() =
        this.Query "SELECT status, COUNT(*), SUM(total) FROM orders GROUP BY status"

    [<Benchmark>]
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
