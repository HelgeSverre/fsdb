<!--
sha: b9a6895
date: 2026-09-09T02:18:05Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                            | Target | Mean         | Error         | StdDev       | Allocated |
|---------------------------------- |------- |-------------:|--------------:|-------------:|----------:|
| **CorrelatedNestedDerivedEquality**   | **fsdb**   |     **683.7 μs** |     **166.90 μs** |      **9.15 μs** |     **977 B** |
| CorrelatedChainedCteEquality      | fsdb   |     689.8 μs |      28.86 μs |      1.58 μs |    1001 B |
| CorrelatedFilteredDerivedEquality | fsdb   |   1,166.7 μs |      30.61 μs |      1.68 μs |     851 B |
| CorrelatedFilteredCteEquality     | fsdb   |   1,139.0 μs |     101.56 μs |      5.57 μs |     875 B |
| CorrelatedDerivedRange            | fsdb   |  14,965.1 μs |     124.57 μs |      6.83 μs |     837 B |
| CorrelatedCteRange                | fsdb   |  14,162.2 μs |     705.05 μs |     38.65 μs |     861 B |
| **CorrelatedNestedDerivedEquality**   | **mysql**  |     **213.5 μs** |      **35.54 μs** |      **1.95 μs** |     **976 B** |
| CorrelatedChainedCteEquality      | mysql  |     213.8 μs |      15.32 μs |      0.84 μs |    1000 B |
| CorrelatedFilteredDerivedEquality | mysql  | 373,698.1 μs | 858,713.86 μs | 47,069.00 μs |         - |
| CorrelatedFilteredCteEquality     | mysql  | 295,374.6 μs |  52,518.03 μs |  2,878.69 μs |         - |
| CorrelatedDerivedRange            | mysql  | 451,617.7 μs |  53,759.90 μs |  2,946.76 μs |         - |
| CorrelatedCteRange                | mysql  | 454,329.8 μs | 137,315.84 μs |  7,526.74 μs |         - |
