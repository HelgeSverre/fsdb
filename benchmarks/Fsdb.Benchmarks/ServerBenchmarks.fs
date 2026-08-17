/// Every workload side-by-side: BenchmarkDotNet's [<Params>] runs each
/// [<Benchmark>] once per Target ("fsdb" @ 3307, "mysql" @ 3316), producing
/// one table with both servers under identical data and queries.
///
/// BenchmarkDotNet launches a fresh process per (Target x Benchmark method)
/// case, so GlobalSetup/GlobalCleanup below run once per case, not once per
/// suite. That's the granularity M9-4 needs: fsdb has a real bug (a timed-out
/// JOIN keeps building its cross product server-side after the client gives
/// up, see performance-design.md 1.1/1.6) where one case's leftover work
/// inflates every later case's numbers 5-8x. The fix chosen here is
/// "restart the fsdb server per case", not "reseed in place" or "run every
/// case on its own port": a restart is the only thing that actually kills
/// the leftover work, it needs just one well-known port (nothing else is
/// listening on it once the case that had it exits), and BenchmarkDotNet
/// already hands us fresh-process granularity for free. mysql has no such
/// bug, so it keeps the cheaper "start once, seed once" lifecycle owned by
/// the justfile — restarting a real mysqld per case would only add time
/// for no correctness gain.
module Fsdb.Benchmarks.ServerBenchmarks

open System
open System.Diagnostics
open System.Threading
open BenchmarkDotNet.Attributes
open MySqlConnector
open Fsdb.Benchmarks.Schema

[<MemoryDiagnoser>]
type ServerBenchmarks() =

    let mutable conn : MySqlConnection = Unchecked.defaultof<_>
    let mutable fsdbProcess : Process option = None
    let mutable rng = Random(1234)
    let mutable insertCounter = 0

    // Draw from the seeded id range so every read hits a real row.
    let randomUserId () = rng.Next(1, Schema.userCount + 1)

    // Kill anything already bound to `port` — defensive cleanup in case a
    // previous case's GlobalCleanup didn't run (e.g. the process was killed
    // mid-benchmark).
    let killPort (port: int) =
        use killer =
            Process.Start("/bin/sh", $"-c \"lsof -ti tcp:{port} | xargs -r kill -9\"")

        killer.WaitForExit(5000) |> ignore

    // Start a fresh in-memory fsdb (no --data-dir, matching the design doc's
    // measurement environment) and block until it accepts connections.
    let startFsdb () =
        let bin =
            Environment.GetEnvironmentVariable "FSDB_BENCH_BIN"
            |> Option.ofObj
            |> Option.defaultWith (fun () ->
                failwith "FSDB_BENCH_BIN not set — run benchmarks via `just bench`/`just bench-quick`")

        let port = Schema.portFor "fsdb"
        killPort port

        let psi = ProcessStartInfo("dotnet", $"exec \"{bin}\" --port {port}")
        psi.UseShellExecute <- false
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        let proc = Process.Start psi
        fsdbProcess <- Some proc

        let deadline = DateTime.UtcNow.AddSeconds 30.0

        let rec waitReady () =
            try
                use probe = new MySqlConnection(Schema.rootConnectionString "fsdb")
                probe.Open()
            with _ ->
                if DateTime.UtcNow > deadline then
                    failwith "fsdb did not become ready within 30s"

                Thread.Sleep 200
                waitReady ()

        waitReady ()

        Schema.resetDatabase "fsdb"
        use seedConn = new MySqlConnection(Schema.connectionString "fsdb")
        seedConn.Open()
        Schema.createSchema seedConn
        Schema.seed seedConn

    let stopFsdb () =
        match fsdbProcess with
        | Some proc ->
            (try
                proc.Kill(entireProcessTree = true)
             with _ ->
                 ())

            proc.WaitForExit(5000) |> ignore
            proc.Dispose()
            fsdbProcess <- None
        | None -> ()

    [<Params("fsdb", "mysql")>]
    member val Target = "" with get, set

    [<GlobalSetup>]
    member this.Setup() =
        if this.Target = "fsdb" then
            startFsdb ()

        conn <- new MySqlConnection(Schema.connectionString this.Target)
        conn.Open()
        rng <- Random(1234)
        insertCounter <- 0

    [<GlobalCleanup>]
    member this.Cleanup() =
        conn.Dispose()

        if this.Target = "fsdb" then
            stopFsdb ()

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
