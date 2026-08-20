/// N-writer throughput harness (`--load`): sustained ops/sec under
/// concurrency, which the latency suite (one connection, one op at a time)
/// structurally cannot see. fsdb's whole write path sits behind a
/// per-database `SemaphoreSlim(1,1)` (see `Storage.enterTransactionGate`),
/// so this is the number that shows it buckling: fsdb's ops/sec stays flat
/// as worker count grows, while MySQL's scales.
///
/// Writes are spread over disjoint id slices per worker, so MySQL sees no
/// cross-worker row contention — the only material difference between the
/// two servers is fsdb's coarse gate. Emits a markdown table to
/// `benchmarks/load-report.md` for `just bench-load` and echoes it to stdout.
module Fsdb.Benchmarks.LoadBenchmarks

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open MySqlConnector
open Fsdb.Benchmarks.Schema
open Fsdb.Benchmarks.BenchServer

type private Workload =
    | UpdateDistinct
    | Insert
    | ReplaceDistinct
    | Mixed

    member this.Name =
        match this with
        | UpdateDistinct -> "update-distinct"
        | Insert -> "insert"
        | ReplaceDistinct -> "replace-distinct"
        | Mixed -> "mixed"

let private envFloat (name: string) (fallback: float) : float =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | s ->
        match Double.TryParse s with
        | true, v when v > 0.0 -> v
        | _ -> fallback

let private envInt (name: string) (fallback: int) : int =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | s ->
        match Int32.TryParse s with
        | true, v when v > 0 -> v
        | _ -> fallback

let private workerCount = envInt "FSDB_LOAD_WORKERS" 8
let private warmupSeconds = envFloat "FSDB_LOAD_WARMUP" 1.0
let private measureSeconds = envFloat "FSDB_LOAD_SECONDS" 5.0

/// One worker: its own connection, a disjoint id slice, ops until `seconds`.
/// `insertCounter` is process-wide so warmup and measure phases (and every
/// worker) mint distinct emails across the whole run — the `email` column is
/// UNIQUE.
let private runOneWorker (target: string) (workload: Workload) (w: int) (seconds: float) (insertCounter: int64 ref) : int64 =
    use conn = new MySqlConnection(Schema.connectionString target)
    conn.Open()

    let rng = Random(1234 + w)
    let sliceSize = max 1 (Schema.userCount / workerCount)
    let sliceStart = 1 + w * sliceSize

    let exec sql =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        cmd.ExecuteNonQuery() |> ignore

    let query sql =
        use cmd = conn.CreateCommand()
        cmd.CommandText <- sql
        use reader = cmd.ExecuteReader()
        while reader.Read() do
            ()

    let oneOp () =
        match workload with
        | UpdateDistinct ->
            exec $"UPDATE users SET age = age + 1 WHERE id = {sliceStart + rng.Next(sliceSize)}"
        | Insert ->
            let i = Interlocked.Increment(&insertCounter.contents)
            exec
                $"INSERT INTO users (name, email, age, meta, created_at) VALUES ('load_{i}', 'load_{i}@bench.test', 30, '{{\"plan\":\"free\"}}', '2024-01-01 00:00:00')"
        | ReplaceDistinct ->
            let id = sliceStart + rng.Next(sliceSize)
            let seedIndex = id - 1
            exec
                $"REPLACE INTO users VALUES ({id}, 'user_{seedIndex}', 'user_{seedIndex}@bench.test', 30, '{{\"plan\":\"free\"}}', '2024-01-01 00:00:00')"
        | Mixed ->
            if rng.Next(2) = 0 then
                query $"SELECT id, name FROM users WHERE id = {1 + rng.Next(Schema.userCount)}"
            else
                exec $"UPDATE users SET age = age + 1 WHERE id = {sliceStart + rng.Next(sliceSize)}"

    let sw = Stopwatch.StartNew()
    let mutable ops = 0L
    while sw.Elapsed.TotalSeconds < seconds do
        oneOp ()
        ops <- ops + 1L

    ops

let private runWorkers (target: string) (workload: Workload) (seconds: float) (insertCounter: int64 ref) : int64 =
    let tasks =
        [| for w in 0 .. workerCount - 1 ->
               Task.Run<int64>(fun () -> runOneWorker target workload w seconds insertCounter) |]

    Task.WaitAll(tasks |> Array.map (fun t -> t :> Task))
    tasks |> Array.sumBy (fun t -> t.Result)

/// Warmup then measure; returns measured ops/sec.
let private measure (target: string) (workload: Workload) : float =
    let insertCounter = ref 0L
    runWorkers target workload warmupSeconds insertCounter |> ignore
    float (runWorkers target workload measureSeconds insertCounter) / measureSeconds

let run () : int =
    let bin = BenchServer.benchBin ()

    let results =
        [ for workload in [ UpdateDistinct; Insert; ReplaceDistinct; Mixed ] do
              for target in [ "fsdb"; "mysql" ] do
                  let opsPerSec =
                      if target = "fsdb" then
                          let proc = BenchServer.startFsdb bin None
                          try
                              measure target workload
                          finally
                              BenchServer.stopFsdb proc
                      else
                          // Reseed mysql per workload so every workload sees
                          // the identical dataset fsdb's fresh start does.
                          BenchServer.resetAndSeed "mysql"
                          measure target workload

                  yield workload.Name, target, opsPerSec ]

    let formatFloat (fmt: string) (v: float) =
        v.ToString(fmt, Globalization.CultureInfo.InvariantCulture)

    let measureLabel = formatFloat "0.0" measureSeconds
    let warmupLabel = formatFloat "0.0" warmupSeconds

    let table =
        [ yield $"{workerCount} workers, {measureLabel}s measured after {warmupLabel}s warmup."
          yield ""
          yield "| Workload | Target | ops/sec |"
          yield "|---|---|---:|"
          for name, target, ops in results do
              let opsLabel = formatFloat "0" ops
              yield $"| {name} | {target} | {opsLabel} |" ]
        |> String.concat "\n"

    let report = Path.Combine("benchmarks", "load-report.md")
    Directory.CreateDirectory "benchmarks" |> ignore
    File.WriteAllText(report, table + "\n")
    printfn "%s" table
    0
