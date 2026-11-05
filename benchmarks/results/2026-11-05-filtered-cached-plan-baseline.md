<!--
sha: d28e4646
date: 2026-11-05T09:01:30Z
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
| **CorrelatedEqualityOuterOnly**      | **fsdb**   |   **364.40 μs** | **142.455 μs** |  **7.808 μs** |     **505 B** |
| CorrelatedScalarNoSource         | fsdb   |   553.86 μs | 145.579 μs |  7.980 μs |     505 B |
| CorrelatedScalarNoSourceFiltered | fsdb   | 1,786.74 μs | 364.877 μs | 20.000 μs |     507 B |
| CorrelatedPhysicalExists         | fsdb   |   640.31 μs | 156.651 μs |  8.587 μs |     505 B |
| **CorrelatedEqualityOuterOnly**      | **mysql**  |    **75.54 μs** |   **5.879 μs** |  **0.322 μs** |     **504 B** |
| CorrelatedScalarNoSource         | mysql  |    81.17 μs |  52.472 μs |  2.876 μs |     504 B |
| CorrelatedScalarNoSourceFiltered | mysql  |    87.92 μs |  17.299 μs |  0.948 μs |     504 B |
| CorrelatedPhysicalExists         | mysql  |   174.14 μs |   4.918 μs |  0.270 μs |     504 B |
