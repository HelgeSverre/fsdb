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
        let environmentList name =
            match Environment.GetEnvironmentVariable name with
            | null -> [||]
            | value -> value.Split(',', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)

        let selectedMethods = environmentList "FSDB_BENCH_METHODS"

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
            let withCategoryFilter =
                let baseConfig = DefaultConfig.Instance.AddJob(job)

                match environmentList "FSDB_BENCH_CATEGORIES" with
                | [||] -> baseConfig
                | categories -> baseConfig.AddFilter(AnyCategoriesFilter(categories))

            match selectedMethods with
            | [||] -> withCategoryFilter
            | methods ->
                withCategoryFilter.AddFilter(
                    SimpleFilter(fun benchmark ->
                        methods
                        |> Array.exists (fun methodName ->
                            String.Equals(methodName, benchmark.Descriptor.WorkloadMethod.Name, StringComparison.OrdinalIgnoreCase)))
                )

        BenchmarkRunner.Run<ServerBenchmarks>(config) |> ignore
        // Separate class/run: the connect cycle needs its fixed small
        // invocation count (see its doc) which the shared job must not have.
        if
            Array.isEmpty selectedMethods
            || selectedMethods |> Array.exists (fun methodName -> methodName.Equals("Connect", StringComparison.OrdinalIgnoreCase))
        then
            BenchmarkRunner.Run<ConnectBenchmarks>(config) |> ignore

        0
