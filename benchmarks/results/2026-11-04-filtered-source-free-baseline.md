<!--
sha: 1cad341f
date: 2026-11-04T16:27:30Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                           | Target | Mean        | Error      | StdDev    | Allocated |
|--------------------------------- |------- |------------:|-----------:|----------:|----------:|
| **CorrelatedEqualityOuterOnly**      | **fsdb**   |   **406.02 μs** | **445.720 μs** | **24.431 μs** |     **505 B** |
| CorrelatedScalarNoSourceFiltered | fsdb   | 1,556.35 μs | 870.338 μs | 47.706 μs |     507 B |
| **CorrelatedEqualityOuterOnly**      | **mysql**  |    **74.88 μs** |   **0.839 μs** |  **0.046 μs** |     **504 B** |
| CorrelatedScalarNoSourceFiltered | mysql  |    87.68 μs |   4.162 μs |  0.228 μs |     504 B |
