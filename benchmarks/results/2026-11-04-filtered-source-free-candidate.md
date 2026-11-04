<!--
sha: 1cad341f
date: 2026-11-04T16:25:16Z
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
| Method                           | Target | Mean        | Error     | StdDev    | Allocated |
|--------------------------------- |------- |------------:|----------:|----------:|----------:|
| **CorrelatedEqualityOuterOnly**      | **fsdb**   |   **363.72 μs** | **178.17 μs** |  **9.766 μs** |     **505 B** |
| CorrelatedScalarNoSourceFiltered | fsdb   | 2,053.01 μs | 500.56 μs | 27.437 μs |     509 B |
| **CorrelatedEqualityOuterOnly**      | **mysql**  |    **75.20 μs** |  **12.20 μs** |  **0.669 μs** |     **504 B** |
| CorrelatedScalarNoSourceFiltered | mysql  |    88.33 μs |  13.56 μs |  0.743 μs |     504 B |
