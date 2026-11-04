<!--
sha: 6933dde4
date: 2026-11-04T11:44:01Z
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
| Method                      | Target | Mean      | Error       | StdDev     | Allocated |
|---------------------------- |------- |----------:|------------:|-----------:|----------:|
| **CorrelatedDerivedEquality**   | **fsdb**   | **738.73 μs** |   **837.33 μs** |  **45.897 μs** |     **777 B** |
| CorrelatedEqualityOuterOnly | fsdb   | 553.27 μs |   109.99 μs |   6.029 μs |     505 B |
| CorrelatedPhysicalEquality  | fsdb   | 710.25 μs | 1,071.60 μs |  58.738 μs |     729 B |
| **CorrelatedDerivedEquality**   | **mysql**  | **782.77 μs** | **9,002.07 μs** | **493.434 μs** |     **776 B** |
| CorrelatedEqualityOuterOnly | mysql  |  80.16 μs |    19.01 μs |   1.042 μs |     504 B |
| CorrelatedPhysicalEquality  | mysql  | 294.00 μs |   974.80 μs |  53.432 μs |     728 B |
