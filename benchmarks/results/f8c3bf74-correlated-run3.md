<!--
sha: f8c3bf74
date: 2026-10-21T00:29:09Z
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
| Method                    | Target | Mean        | Error       | StdDev     | Allocated |
|-------------------------- |------- |------------:|------------:|-----------:|----------:|
| **PointSelectByPk**           | **fsdb**   |   **428.95 μs** |   **235.04 μs** |  **12.884 μs** |     **881 B** |
| **PointSelectByPk**           | **mysql**  |    **59.52 μs** |    **97.66 μs** |   **5.353 μs** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   | **1,003.59 μs** |   **486.65 μs** |  **26.675 μs** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 1,067.46 μs |   949.31 μs |  52.035 μs |     840 B |
| **CorrelatedDerivedEquality** | **mysql**  |   **321.47 μs** |   **477.28 μs** |  **26.161 μs** |     **777 B** |
| CorrelatedCteEquality     | mysql  |   636.60 μs | 7,150.00 μs | 391.916 μs |     841 B |
