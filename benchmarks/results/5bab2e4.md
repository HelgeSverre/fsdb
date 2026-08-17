<!--
sha: 5bab2e4
date: 2026-08-17T14:36:10Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-RPZBRD : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean         | Error        | StdDev     | Gen0   | Allocated |
|--------------------- |------- |-------------:|-------------:|-----------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |    **107.57 μs** |     **1.866 μs** |   **0.484 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   |  6,120.67 μs |   181.631 μs |  64.771 μs |      - |     568 B |
| InsertSingle         | fsdb   |    277.44 μs |    66.533 μs |  23.726 μs |      - |    1240 B |
| InsertBatch100       | fsdb   |  4,612.44 μs |   758.885 μs | 270.626 μs | 7.8125 |   96710 B |
| UpdateSingleRow      | fsdb   |     79.28 μs |     1.237 μs |   0.321 μs |      - |     679 B |
| JoinUsersOrders      | fsdb   | 11,457.11 μs | 1,147.182 μs | 409.096 μs |      - |     820 B |
| GroupByAggregate     | fsdb   | 25,735.01 μs |   923.164 μs | 239.743 μs |      - |    1004 B |
| JsonExtract          | fsdb   |    285.51 μs |     8.652 μs |   3.086 μs |      - |     505 B |
| PreparedPointSelect  | fsdb   |    101.35 μs |    13.186 μs |   4.702 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |     **40.24 μs** |     **1.210 μs** |   **0.314 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |  1,933.53 μs |    72.307 μs |  18.778 μs |      - |     640 B |
| InsertSingle         | mysql  |    102.13 μs |     4.300 μs |   1.533 μs | 0.1221 |    1240 B |
| InsertBatch100       | mysql  |  1,049.93 μs |    66.604 μs |  17.297 μs | 9.7656 |   96712 B |
| UpdateSingleRow      | mysql  |    103.10 μs |     2.754 μs |   0.715 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |    277.44 μs |    51.609 μs |  18.404 μs |      - |     889 B |
| GroupByAggregate     | mysql  | 20,713.59 μs |   261.987 μs |  93.427 μs |      - |     634 B |
| JsonExtract          | mysql  |     63.98 μs |     4.336 μs |   1.546 μs |      - |     584 B |
| PreparedPointSelect  | mysql  |     34.83 μs |     3.254 μs |   1.160 μs | 0.0610 |     960 B |
