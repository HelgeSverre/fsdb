<!--
sha: 9a3ed57
date: 2026-09-01T01:04:55Z
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
| Method                    | Target | Mean         | Error          | StdDev       | Allocated |
|-------------------------- |------- |-------------:|---------------:|-------------:|----------:|
| **InfoSchemaColumnsForTable** | **fsdb**   |    **191.06 μs** |      **10.761 μs** |     **0.590 μs** |     **568 B** |
| **InfoSchemaColumnsForTable** | **mysql**  |    **248.90 μs** |      **31.744 μs** |     **1.740 μs** |     **569 B** |
| **WindowTopOrders**           | **fsdb**   | **83,482.37 μs** | **182,401.598 μs** | **9,998.047 μs** |         **-** |
| WindowCumeDistPeers       | fsdb   | 11,037.01 μs |   3,388.664 μs |   185.744 μs |     525 B |
| **WindowTopOrders**           | **mysql**  | **50,359.72 μs** |  **52,366.507 μs** | **2,870.385 μs** |         **-** |
| WindowCumeDistPeers       | mysql  | 18,715.17 μs |   8,934.713 μs |   489.742 μs |     538 B |
| **FullTextNaturalSearch**     | **fsdb**   |    **874.09 μs** |      **45.497 μs** |     **2.494 μs** |     **505 B** |
| FullTextJoinUsers         | fsdb   |  1,777.78 μs |      77.459 μs |     4.246 μs |     523 B |
| **FullTextNaturalSearch**     | **mysql**  |    **411.80 μs** |      **47.946 μs** |     **2.628 μs** |     **505 B** |
| FullTextJoinUsers         | mysql  |    430.28 μs |      34.047 μs |     1.866 μs |     521 B |
| **FilterScanOrderLimit**      | **fsdb**   |  **5,311.48 μs** |     **539.899 μs** |    **29.594 μs** |     **571 B** |
| GroupByAggregate          | fsdb   | 24,117.70 μs |  21,236.175 μs | 1,164.026 μs |     585 B |
| JsonExtract               | fsdb   |    179.91 μs |      16.814 μs |     0.922 μs |     504 B |
| UpdateByNonIndexed        | fsdb   |  6,526.55 μs |     252.797 μs |    13.857 μs |     731 B |
| **FilterScanOrderLimit**      | **mysql**  |  **1,931.90 μs** |     **111.463 μs** |     **6.110 μs** |     **642 B** |
| GroupByAggregate          | mysql  | 19,799.22 μs |     704.637 μs |    38.624 μs |     634 B |
| JsonExtract               | mysql  |     62.19 μs |       3.004 μs |     0.165 μs |     584 B |
| UpdateByNonIndexed        | mysql  |  3,576.58 μs |      60.710 μs |     3.328 μs |     788 B |
| **CorrelatedOrderCount**      | **fsdb**   |  **1,033.95 μs** |     **260.149 μs** |    **14.260 μs** |     **507 B** |
| **CorrelatedOrderCount**      | **mysql**  |    **208.05 μs** |      **16.239 μs** |     **0.890 μs** |     **504 B** |
