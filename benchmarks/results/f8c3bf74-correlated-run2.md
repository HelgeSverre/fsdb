<!--
sha: f8c3bf74
date: 2026-10-21T00:27:01Z
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
| **PointSelectByPk**           | **fsdb**   |   **488.00 μs** | **2,348.19 μs** | **128.712 μs** |     **881 B** |
| **PointSelectByPk**           | **mysql**  |    **50.13 μs** |    **51.18 μs** |   **2.805 μs** |     **880 B** |
| **CorrelatedDerivedEquality** | **fsdb**   |   **987.23 μs** |   **565.01 μs** |  **30.970 μs** |     **777 B** |
| CorrelatedCteEquality     | fsdb   | 1,437.84 μs | 5,397.31 μs | 295.845 μs |     841 B |
| **CorrelatedDerivedEquality** | **mysql**  |   **996.51 μs** | **2,851.73 μs** | **156.313 μs** |     **776 B** |
| CorrelatedCteEquality     | mysql  |   389.31 μs | 2,124.13 μs | 116.431 μs |     840 B |
