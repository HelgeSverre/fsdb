<!--
sha: de493f5d
date: 2026-10-14T12:21:12Z
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
| Method                    | Target | Mean        | Error        | StdDev    | Gen0   | Allocated |
|-------------------------- |------- |------------:|-------------:|----------:|-------:|----------:|
| **PointSelectByPk**           | **fsdb**   |   **464.03 μs** |   **274.135 μs** | **15.026 μs** |      **-** |     **881 B** |
| **PointSelectByPk**           | **mysql**  |    **58.78 μs** |     **5.642 μs** |  **0.309 μs** | **0.0610** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   | **1,301.07 μs** | **1,215.743 μs** | **66.639 μs** |      **-** |     **776 B** |
| CorrelatedCteEquality     | fsdb   | 1,142.87 μs | 1,429.546 μs | 78.358 μs |      - |     843 B |
| **CorrelatedDerivedEquality** | **mysql**  |   **222.84 μs** |   **122.340 μs** |  **6.706 μs** |      **-** |     **776 B** |
| CorrelatedCteEquality     | mysql  |   243.19 μs |   160.647 μs |  8.806 μs |      - |     840 B |
