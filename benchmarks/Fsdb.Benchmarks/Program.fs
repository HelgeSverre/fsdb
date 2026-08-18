module Fsdb.Benchmarks.Program

open System
open BenchmarkDotNet.Configs
open BenchmarkDotNet.Jobs
open BenchmarkDotNet.Running
open Fsdb.Benchmarks.BenchServer
open Fsdb.Benchmarks.LoadBenchmarks
open Fsdb.Benchmarks.ServerBenchmarks

[<EntryPoint>]
let main argv =
    if argv |> Array.contains "--load" then
        LoadBenchmarks.run ()
    else
        // fsdb is seeded per benchmark case (see ServerBenchmarks) because it
        // restarts per case; the mysql servers are long-lived for the whole
        // run and only need seeding once each, here.
        BenchServer.resetAndSeed "mysql"
        if BenchServer.isDurableRun () then
            BenchServer.resetAndSeed "mysql-nofsync"

        // Full run: 3 warmup + 6 measured iterations per (target x workload) —
        // fixed counts instead of BenchmarkDotNet's open-ended pilot stage, to
        // keep the whole 2-target x 8-workload suite under ~10 minutes.
        // `--quick` (just bench-quick) swaps in BenchmarkDotNet's built-in
        // ShortRun job for fast local iteration.
        // DefaultConfig already ships a GitHub-flavored markdown exporter,
        // which is what `just bench` copies out of BenchmarkDotNet.Artifacts/results/.
        let job =
            if argv |> Array.contains "--quick" then
                Job.ShortRun
            else
                Job.Default.WithWarmupCount(3).WithIterationCount(6)

        let config = DefaultConfig.Instance.AddJob(job)

        BenchmarkRunner.Run<ServerBenchmarks>(config) |> ignore
        // Separate class/run: the connect cycle needs its fixed small
        // invocation count (see its doc) which the shared job must not have.
        BenchmarkRunner.Run<ConnectBenchmarks>(config) |> ignore
        0
