<!--
sha: 6749ae53
date: 2026-11-04T15:41:52Z
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
| Method                      | Target | Mean        | Error        | StdDev     | Allocated |
|---------------------------- |------- |------------:|-------------:|-----------:|----------:|
| **CorrelatedDerivedEquality**   | **fsdb**   |   **635.77 μs** |     **52.90 μs** |   **2.900 μs** |     **777 B** |
| CorrelatedEqualityOuterOnly | fsdb   |   336.61 μs |     48.70 μs |   2.669 μs |     505 B |
| CorrelatedScalarNoSource    | fsdb   | 1,130.90 μs |    128.71 μs |   7.055 μs |     507 B |
| CorrelatedPhysicalExists    | fsdb   | 8,925.64 μs | 16,617.01 μs | 910.834 μs |     525 B |
| CorrelatedPhysicalEquality  | fsdb   |   644.70 μs |     48.35 μs |   2.650 μs |     729 B |
| **CorrelatedDerivedEquality**   | **mysql**  |   **214.05 μs** |     **20.03 μs** |   **1.098 μs** |     **776 B** |
| CorrelatedEqualityOuterOnly | mysql  |    74.04 μs |     24.67 μs |   1.352 μs |     504 B |
| CorrelatedScalarNoSource    | mysql  |    76.98 μs |     11.91 μs |   0.653 μs |     504 B |
| CorrelatedPhysicalExists    | mysql  |   169.62 μs |     12.26 μs |   0.672 μs |     504 B |
| CorrelatedPhysicalEquality  | mysql  |   210.28 μs |     59.45 μs |   3.259 μs |     728 B |
