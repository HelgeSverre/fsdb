module Fsdb.Benchmarks.Program

open System
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Filters
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open Fsdb.Benchmarks.LoadBenchmarks
open Fsdb.Benchmarks.ServerBenchmarks

[<EntryPoint>]
let main argv =
    if argv |> Array.contains "--load" then
        LoadBenchmarks.run ()
    else
        // Full run: 3 warmup + 6 measured iterations per (target x workload) —
        // fixed counts instead of BenchmarkDotNet's open-ended pilot stage, to
        // bound the runtime of the full target/workload matrix.
        // `--quick` (just bench-quick) swaps in BenchmarkDotNet's built-in
        // ShortRun job for fast local iteration.
        // DefaultConfig already ships a GitHub-flavored markdown exporter,
        // which is what `just bench` copies out of BenchmarkDotNet.Artifacts/results/.
        let job =
            if argv |> Array.contains "--quick" then
                Job.ShortRun
            else
                Job.Default.WithWarmupCount(3).WithIterationCount(6)

        let config =
            let baseConfig = DefaultConfig.Instance.AddJob(job)

            match Environment.GetEnvironmentVariable "FSDB_BENCH_CATEGORIES" with
            | null -> baseConfig
            | value ->
                let categories =
                    value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

                baseConfig.AddFilter(AnyCategoriesFilter(categories))

        BenchmarkRunner.Run<ServerBenchmarks>(config) |> ignore
        // Separate class/run: the connect cycle needs its fixed small
        // invocation count (see its doc) which the shared job must not have.
        BenchmarkRunner.Run<ConnectBenchmarks>(config) |> ignore
        0
