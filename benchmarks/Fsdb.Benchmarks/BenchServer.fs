/// Shared bench-server lifecycle: start/stop a throwaway fsdb and reseed a
/// target's `fsdb_bench` database. Both the latency suite (ServerBenchmarks)
/// and the load harness (LoadBenchmarks) need this, and `startFsdb`'s
/// `--data-dir` support (the durable variant) is written here once.
module Fsdb.Benchmarks.BenchServer

open System
open System.Diagnostics
open System.IO
open System.Threading
open MySqlConnector
open Fsdb.Benchmarks.Schema

/// The path to the prebuilt Fsdb.dll under test, from the justfile recipe.
let benchBin () =
    Environment.GetEnvironmentVariable "FSDB_BENCH_BIN"
    |> Option.ofObj
    |> Option.defaultWith (fun () -> failwith "FSDB_BENCH_BIN not set — run via `just bench`/`just bench-load`")

/// The durability-matched run (`just bench-durable`) adds `fsdb-wal` and
/// `mysql-nofsync` to the target list via this env flag.
let isDurableRun () =
    Environment.GetEnvironmentVariable "FSDB_BENCH_TARGETS" = "durable"

/// A fresh throwaway data dir for the `fsdb-wal` (durable) variant.
let tempDataDir () =
    Path.Combine(Path.GetTempPath(), "fsdb-bench-" + Guid.NewGuid().ToString("N"))

/// Kills anything already bound to `port` — defensive cleanup in case a
/// previous case's cleanup didn't run (the host was killed mid-benchmark)
/// and an orphan fsdb still holds the port.
let killPort (port: int) =
    use killer = Process.Start("/bin/sh", $"-c \"lsof -ti tcp:{port} | xargs -r kill -9\"")
    killer.WaitForExit(5000) |> ignore

/// Resets and reseeds `fsdb_bench` on `target`. mysql is one long-lived
/// server and needs this at most once per run; fsdb reseeds on every start
/// because it restarts per workload.
let resetAndSeed (target: string) =
    printfn $"Seeding {target}: {Schema.userCount} users, {Schema.orderCount} orders..."
    Schema.resetDatabase target
    use conn = new MySqlConnection(Schema.connectionString target)
    conn.Open()
    Schema.createSchema conn
    Schema.seed conn

/// Starts a fresh fsdb — in-memory unless `dataDir` is `Some` (the durable
/// WAL variant) — blocks until it accepts connections, reseeds it, and
/// returns the process for the caller to tear down.
let startFsdb (bin: string) (dataDir: string option) : Process =
    let port = Schema.portFor "fsdb"
    killPort port

    let dataDirArg = dataDir |> Option.map (fun d -> $" --data-dir {d}") |> Option.defaultValue ""
    let psi = ProcessStartInfo("dotnet", $"exec \"{bin}\" --port {port}{dataDirArg}")
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    let proc = Process.Start psi

    // Drain fsdb's redirected streams: a busy run that ever hits an error
    // path could otherwise fill the pipe buffer and block the child.
    proc.StandardOutput.ReadToEndAsync() |> ignore
    proc.StandardError.ReadToEndAsync() |> ignore

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
    resetAndSeed "fsdb"
    proc

/// Kills `proc`'s whole tree (`dotnet exec` plus the fsdb child).
let stopFsdb (proc: Process) =
    (try
        proc.Kill(entireProcessTree = true)
     with _ ->
         ())

    proc.WaitForExit(5000) |> ignore
    proc.Dispose()
