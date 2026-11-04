<!--
sha: 127ecd62
date: 2026-11-04T16:54:06Z
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
| Method                         | Target | Mean      | Error     | StdDev    | Allocated |
|------------------------------- |------- |----------:|----------:|----------:|----------:|
| **CorrelatedEqualityOuterOnly**    | **fsdb**   | **386.49 μs** |  **26.85 μs** |  **1.472 μs** |     **505 B** |
| CorrelatedPhysicalExists       | fsdb   | 646.13 μs | 153.00 μs |  8.386 μs |     505 B |
| CorrelatedPhysicalExistsColumn | fsdb   | 719.75 μs | 303.04 μs | 16.611 μs |     505 B |
| **CorrelatedEqualityOuterOnly**    | **mysql**  |  **77.23 μs** |  **82.32 μs** |  **4.512 μs** |     **504 B** |
| CorrelatedPhysicalExists       | mysql  | 170.93 μs |  14.67 μs |  0.804 μs |     504 B |
| CorrelatedPhysicalExistsColumn | mysql  | 176.02 μs |  13.95 μs |  0.764 μs |     504 B |
