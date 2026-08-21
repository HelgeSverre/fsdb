/// N-writer throughput harness (`--load`): sustained ops/sec under
/// concurrency, which the latency suite (one connection, one op at a time)
/// structurally cannot see. Optimistic row-conflict merging lets disjoint
/// workers publish changes independently until each short commit section.
///
/// Disjoint and hot-row writes separate publication throughput from genuine
/// row contention. Point reads, inserts, upserts, REPLACE, explicit
/// transactions, and mixed traffic keep the comparison from depending on a
/// single favorable statement shape. Emits a markdown table to
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
    | PointRead
    | UpdateDistinct
    | UpdateHot
    | UpsertDistinct
    | Insert
    | ReplaceDistinct
    | TransactionDistinct
    | Mixed

    member this.Name =
        match this with
        | PointRead -> "point-read"
        | UpdateDistinct -> "update-distinct"
        | UpdateHot -> "update-hot"
        | UpsertDistinct -> "upsert-distinct"
        | Insert -> "insert"
        | ReplaceDistinct -> "replace-distinct"
        | TransactionDistinct -> "transaction-distinct"
        | Mixed -> "mixed"

    static member All = [ PointRead; UpdateDistinct; UpdateHot; UpsertDistinct; Insert; ReplaceDistinct; TransactionDistinct; Mixed ]

    static member TryParse(value: string) =
        Workload.All
        |> List.tryFind (fun workload -> String.Equals(workload.Name, value, StringComparison.OrdinalIgnoreCase))

let private envFloat (name: string) (fallback: float) : float =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | s ->
        match Double.TryParse(s, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
        | true, v when v > 0.0 -> v
        | _ -> fallback

let private envInts (name: string) (fallback: int list) : int list =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | s ->
        let parsed =
            s.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.choose (fun value ->
                match Int32.TryParse value with
                | true, workers when workers > 0 -> Some workers
                | _ -> None)
            |> Array.distinct
            |> Array.toList

        if parsed.IsEmpty then fallback else parsed

let private envInt (name: string) (fallback: int) : int =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | value ->
        match Int32.TryParse value with
        | true, parsed when parsed > 0 -> parsed
        | _ -> fallback

let private envWorkloads () =
    match Environment.GetEnvironmentVariable "FSDB_LOAD_WORKLOADS" with
    | null -> Workload.All
    | value ->
        let workloads =
            value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.choose Workload.TryParse
            |> Array.distinct
            |> Array.toList

        if workloads.IsEmpty then Workload.All else workloads

let private workerCounts = envInts "FSDB_LOAD_WORKERS" [ 8 ]
let private warmupSeconds = envFloat "FSDB_LOAD_WARMUP" 1.0
let private measureSeconds = envFloat "FSDB_LOAD_SECONDS" 5.0
let private trialCount = envInt "FSDB_LOAD_TRIALS" 1

/// One worker: its own connection, a disjoint id slice, ops until `seconds`.
/// `insertCounter` is process-wide so warmup and measure phases (and every
/// worker) mint distinct emails across the whole run — the `email` column is
/// UNIQUE.
let private runOneWorker (conn: MySqlConnection) (workload: Workload) (workerCount: int) (w: int) (seconds: float) (insertCounter: int64 ref) : int64 =
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
        | PointRead ->
            query $"SELECT id, name, age FROM users WHERE id = {1 + rng.Next(Schema.userCount)}"
        | UpdateDistinct ->
            exec $"UPDATE users SET age = age + 1 WHERE id = {sliceStart + rng.Next(sliceSize)}"
        | UpdateHot ->
            exec $"UPDATE users SET age = age + 1 WHERE id = {1 + rng.Next(min 16 Schema.userCount)}"
        | UpsertDistinct ->
            let id = sliceStart + rng.Next(sliceSize)
            let seedIndex = id - 1

            exec
                ($"INSERT INTO users (id, name, email, age, meta, created_at) VALUES ({id}, 'user_{seedIndex}', 'user_{seedIndex}@bench.test', 30, '{{\"plan\":\"free\"}}', '2024-01-01 00:00:00') "
                 + "ON DUPLICATE KEY UPDATE age = age + 1")
        | Insert ->
            let i = Interlocked.Increment(&insertCounter.contents)
            exec
                $"INSERT INTO users (name, email, age, meta, created_at) VALUES ('load_{i}', 'load_{i}@bench.test', 30, '{{\"plan\":\"free\"}}', '2024-01-01 00:00:00')"
        | ReplaceDistinct ->
            let id = sliceStart + rng.Next(sliceSize)
            let seedIndex = id - 1
            exec
                $"REPLACE INTO users (id, name, email, age, meta, created_at) VALUES ({id}, 'user_{seedIndex}', 'user_{seedIndex}@bench.test', 30, '{{\"plan\":\"free\"}}', '2024-01-01 00:00:00')"
        | TransactionDistinct ->
            exec "START TRANSACTION"

            try
                exec $"UPDATE users SET age = age + 1 WHERE id = {sliceStart + rng.Next(sliceSize)}"
                exec $"UPDATE users SET age = age + 1 WHERE id = {sliceStart + rng.Next(sliceSize)}"
                exec "COMMIT"
            with _ ->
                exec "ROLLBACK"
                reraise ()
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

let private runWorkers (target: string) (workload: Workload) (workerCount: int) (seconds: float) (insertCounter: int64 ref) : int64 * TimeSpan =
    let connections =
        Array.init workerCount (fun _ ->
            let conn = new MySqlConnection(Schema.connectionString target)
            conn.Open()
            conn)

    use start = new ManualResetEventSlim(false)
    let stopwatch = Stopwatch()

    try
        let tasks =
            connections
            |> Array.mapi (fun w conn ->
                Task.Run<int64>(fun () ->
                    start.Wait()
                    runOneWorker conn workload workerCount w seconds insertCounter))

        stopwatch.Start()
        start.Set()
        Task.WaitAll(tasks |> Array.map (fun task -> task :> Task))
        stopwatch.Stop()
        tasks |> Array.sumBy _.Result, stopwatch.Elapsed
    finally
        connections |> Array.iter _.Dispose()

/// Warmup then measure; returns measured ops/sec.
let private measure (target: string) (workload: Workload) (workerCount: int) : float =
    let insertCounter = ref 0L
    runWorkers target workload workerCount warmupSeconds insertCounter |> ignore
    let operations, elapsed = runWorkers target workload workerCount measureSeconds insertCounter
    float operations / elapsed.TotalSeconds

let private average (samples: float list) =
    List.average samples

let private relativeStandardDeviation (samples: float list) =
    match samples with
    | []
    | [ _ ] -> 0.0
    | _ ->
        let mean = average samples
        let variance = samples |> List.averageBy (fun sample -> pown (sample - mean) 2)
        sqrt variance / mean

let run () : int =
    let bin = BenchServer.benchBin ()
    let workloads = envWorkloads ()

    let measureFsdb workload workerCount =
        let proc = BenchServer.startFsdb bin None

        try
            measure "fsdb" workload workerCount
        finally
            BenchServer.stopFsdb proc

    let measureMysql workload workerCount =
        BenchServer.resetAndSeed "mysql"
        measure "mysql" workload workerCount

    let results =
        [ for workerIndex, workerCount in List.indexed workerCounts do
              for workloadIndex, workload in List.indexed workloads do
                  let samples =
                      [ for trial in 0 .. trialCount - 1 do
                            if (workerIndex + workloadIndex + trial) % 2 = 0 then
                                yield measureFsdb workload workerCount, measureMysql workload workerCount
                            else
                                let mysqlOps = measureMysql workload workerCount
                                yield measureFsdb workload workerCount, mysqlOps ]

                  let fsdbSamples = samples |> List.map fst
                  let mysqlSamples = samples |> List.map snd

                  yield
                      workerCount,
                      workload.Name,
                      average fsdbSamples,
                      relativeStandardDeviation fsdbSamples,
                      average mysqlSamples,
                      relativeStandardDeviation mysqlSamples ]

    let formatFloat (fmt: string) (v: float) =
        v.ToString(fmt, Globalization.CultureInfo.InvariantCulture)

    let measureLabel = formatFloat "0.0" measureSeconds
    let warmupLabel = formatFloat "0.0" warmupSeconds
    let workersLabel = workerCounts |> List.map string |> String.concat ", "

    let table =
        [ yield $"Workers: {workersLabel}. {trialCount} trial(s), {measureLabel}s measured after {warmupLabel}s warmup."
          yield ""
          yield "| Workers | Workload | fsdb ops/sec | fsdb RSD | MySQL ops/sec | MySQL RSD | fsdb/MySQL |"
          yield "|---:|---|---:|---:|---:|---:|---:|"
          for workers, name, fsdbOps, fsdbRsd, mysqlOps, mysqlRsd in results do
              let ratio = if mysqlOps = 0.0 then 0.0 else fsdbOps / mysqlOps
              let fsdbLabel = formatFloat "0" fsdbOps
              let mysqlLabel = formatFloat "0" mysqlOps
              let fsdbRsdLabel = formatFloat "0.0%" fsdbRsd
              let mysqlRsdLabel = formatFloat "0.0%" mysqlRsd
              let ratioLabel = formatFloat "0.00" ratio
              yield $"| {workers} | {name} | {fsdbLabel} | {fsdbRsdLabel} | {mysqlLabel} | {mysqlRsdLabel} | {ratioLabel}x |" ]
        |> String.concat "\n"

    let report = Path.Combine("benchmarks", "load-report.md")
    Directory.CreateDirectory "benchmarks" |> ignore
    File.WriteAllText(report, table + "\n")
    printfn "%s" table
    0
