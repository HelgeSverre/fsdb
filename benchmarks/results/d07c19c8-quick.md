<!--
sha: d07c19c8
date: 2026-11-04T21:49:10Z
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
| Method                      | Target | Mean      | Error     | StdDev    | Allocated |
|---------------------------- |------- |----------:|----------:|----------:|----------:|
| **CorrelatedDerivedEquality**   | **fsdb**   | **733.29 μs** | **233.45 μs** | **12.796 μs** |     **777 B** |
| CorrelatedEqualityOuterOnly | fsdb   | 397.51 μs | 280.60 μs | 15.381 μs |     505 B |
| CorrelatedScalarNoSource    | fsdb   | 704.57 μs | 681.32 μs | 37.345 μs |     505 B |
| CorrelatedPhysicalExists    | fsdb   | 910.82 μs | 230.89 μs | 12.656 μs |     507 B |
| CorrelatedPhysicalEquality  | fsdb   | 733.62 μs | 263.98 μs | 14.470 μs |     729 B |
| **CorrelatedDerivedEquality**   | **mysql**  | **211.74 μs** |  **34.89 μs** |  **1.913 μs** |     **776 B** |
| CorrelatedEqualityOuterOnly | mysql  |  73.70 μs |  74.79 μs |  4.099 μs |     504 B |
| CorrelatedScalarNoSource    | mysql  |  80.65 μs |  42.48 μs |  2.328 μs |     504 B |
| CorrelatedPhysicalExists    | mysql  | 205.12 μs |  75.90 μs |  4.160 μs |     504 B |
| CorrelatedPhysicalEquality  | mysql  | 214.88 μs |  28.07 μs |  1.539 μs |     728 B |

The indexed `EXISTS` mean is above the earlier 600 μs ShortRun, but this is
not evidence of a code regression on its own. A same-head repeat of only
`CorrelatedEqualityOuterOnly` and `CorrelatedPhysicalExists` measured the
fsdb control at 423 μs and `EXISTS` at 1,691 μs; the three `EXISTS` measured
iterations ranged from roughly 991 to 2,515 μs. The repeat's MySQL `EXISTS`
mean was 189 μs. With this level of run-to-run variation, a planner change
needs a more stable profile and a matched before/after comparison.
