<!--
sha: 6749ae53
date: 2026-11-04T15:46:37Z
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
| Method                      | Target | Mean        | Error      | StdDev    | Allocated |
|---------------------------- |------- |------------:|-----------:|----------:|----------:|
| **CorrelatedDerivedEquality**   | **fsdb**   |   **593.19 μs** | **373.141 μs** | **20.453 μs** |     **777 B** |
| CorrelatedEqualityOuterOnly | fsdb   |   377.60 μs | 326.524 μs | 17.898 μs |     505 B |
| CorrelatedScalarNoSource    | fsdb   | 1,268.98 μs | 251.066 μs | 13.762 μs |     507 B |
| CorrelatedPhysicalExists    | fsdb   |   599.87 μs | 224.023 μs | 12.279 μs |     505 B |
| CorrelatedPhysicalEquality  | fsdb   |   705.49 μs | 158.966 μs |  8.713 μs |     729 B |
| **CorrelatedDerivedEquality**   | **mysql**  |   **209.59 μs** |  **55.849 μs** |  **3.061 μs** |     **776 B** |
| CorrelatedEqualityOuterOnly | mysql  |    74.29 μs |   3.604 μs |  0.198 μs |     504 B |
| CorrelatedScalarNoSource    | mysql  |    78.77 μs |  43.927 μs |  2.408 μs |     504 B |
| CorrelatedPhysicalExists    | mysql  |   170.76 μs |  24.256 μs |  1.330 μs |     504 B |
| CorrelatedPhysicalEquality  | mysql  |   206.64 μs |  31.392 μs |  1.721 μs |     728 B |
