module Fsdb.Benchmarks.Program

open System
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open MySqlConnector
open Fsdb.Benchmarks.Schema
open Fsdb.Benchmarks.ServerBenchmarks

/// Reset, create, and seed fsdb_bench on `target` exactly once. See the
/// comment on ServerBenchmarks for why this doesn't live in GlobalSetup.
let private seedTarget (target: string) =
    printfn $"Seeding {target}: {Schema.userCount} users, {Schema.orderCount} orders..."
    Schema.resetDatabase target
    use conn = new MySqlConnection(Schema.connectionString target)
    conn.Open()
    Schema.createSchema conn
    Schema.seed conn

[<EntryPoint>]
let main argv =
    for target in [ "fsdb"; "mysql" ] do
        seedTarget target

    // Full run: 3 warmup + 6 measured iterations per (target x workload) —
    // fixed counts instead of BenchmarkDotNet's open-ended pilot stage, to
    // keep the whole 2-target x 8-workload suite under ~10 minutes.
    // `--quick` (just bench-quick) swaps in BenchmarkDotNet's built-in
    // ShortRun job for fast local iteration.
    // DefaultConfig already ships a GitHub-flavored markdown exporter, which
    // is what `just bench` copies out of BenchmarkDotNet.Artifacts/results/.
    let job =
        if argv |> Array.contains "--quick" then
            Job.ShortRun
        else
            Job.Default.WithWarmupCount(3).WithIterationCount(6)

    let config = DefaultConfig.Instance.AddJob(job)

    BenchmarkRunner.Run<ServerBenchmarks>(config) |> ignore
    0
