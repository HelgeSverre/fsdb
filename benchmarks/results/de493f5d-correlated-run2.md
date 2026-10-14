<!--
sha: de493f5d
date: 2026-10-14T12:22:55Z
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
| Method                    | Target | Mean        | Error     | StdDev    | Gen0   | Allocated |
|-------------------------- |------- |------------:|----------:|----------:|-------:|----------:|
| **PointSelectByPk**           | **fsdb**   |   **408.91 μs** | **296.66 μs** | **16.261 μs** |      **-** |     **881 B** |
| **PointSelectByPk**           | **mysql**  |    **43.55 μs** |  **29.74 μs** |  **1.630 μs** | **0.0610** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   |   **908.39 μs** | **415.69 μs** | **22.785 μs** |      **-** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 1,003.46 μs | 379.17 μs | 20.784 μs |      - |     840 B |
| **CorrelatedDerivedEquality** | **mysql**  |   **219.63 μs** |  **19.46 μs** |  **1.067 μs** |      **-** |     **776 B** |
| CorrelatedCteEquality     | mysql  |   220.06 μs |  40.34 μs |  2.211 μs |      - |     840 B |
