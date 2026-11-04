<!--
sha: 127ecd62
date: 2026-11-04T16:52:12Z
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
| Method                         | Target | Mean        | Error      | StdDev   | Allocated |
|------------------------------- |------- |------------:|-----------:|---------:|----------:|
| **CorrelatedEqualityOuterOnly**    | **fsdb**   |   **361.27 μs** |  **39.573 μs** | **2.169 μs** |     **505 B** |
| CorrelatedPhysicalExists       | fsdb   |   675.00 μs | 142.213 μs | 7.795 μs |     505 B |
| CorrelatedPhysicalExistsColumn | fsdb   | 3,999.82 μs |  74.510 μs | 4.084 μs |     515 B |
| **CorrelatedEqualityOuterOnly**    | **mysql**  |    **73.85 μs** |   **7.345 μs** | **0.403 μs** |     **504 B** |
| CorrelatedPhysicalExists       | mysql  |   170.50 μs |  18.445 μs | 1.011 μs |     504 B |
| CorrelatedPhysicalExistsColumn | mysql  |   177.78 μs |  24.943 μs | 1.367 μs |     504 B |
