<!--
sha: f5ff5a4
date: 2026-08-17T17:49:13Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-PBPDFI : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target | Mean         | Error        | StdDev     | Gen0   | Allocated |
|--------------------- |------- |-------------:|-------------:|-----------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**   |    **107.88 μs** |     **5.005 μs** |   **1.300 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb   |  6,366.22 μs |    55.788 μs |  19.895 μs |      - |     568 B |
| InsertSingle         | fsdb   |    271.87 μs |    65.455 μs |  23.342 μs |      - |    1240 B |
| InsertBatch100       | fsdb   |  4,349.84 μs |   620.923 μs | 161.252 μs | 7.8125 |   96652 B |
| UpdateSingleRow      | fsdb   |     81.26 μs |     4.618 μs |   1.199 μs |      - |     679 B |
| JoinUsersOrders      | fsdb   |  9,156.86 μs |   771.817 μs | 275.237 μs |      - |     825 B |
| GroupByAggregate     | fsdb   | 25,401.58 μs | 1,471.305 μs | 382.093 μs |      - |    1004 B |
| JsonExtract          | fsdb   |    204.89 μs |     4.579 μs |   1.633 μs |      - |     504 B |
| PreparedPointSelect  | fsdb   |     98.55 μs |    15.204 μs |   5.422 μs |      - |     960 B |
| **PointSelectByPk**      | **mysql**  |     **39.68 μs** |     **0.322 μs** |   **0.084 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql  |  1,912.53 μs |    43.094 μs |  15.368 μs |      - |     642 B |
| InsertSingle         | mysql  |    147.23 μs |    92.045 μs |  32.824 μs |      - |    1240 B |
| InsertBatch100       | mysql  |  1,257.74 μs |   837.538 μs | 298.674 μs | 9.7656 |   96714 B |
| UpdateSingleRow      | mysql  |    109.74 μs |     8.597 μs |   3.066 μs |      - |     743 B |
| JoinUsersOrders      | mysql  |    258.68 μs |     6.458 μs |   2.303 μs |      - |     889 B |
| GroupByAggregate     | mysql  | 19,875.28 μs |   164.736 μs |  42.781 μs |      - |     634 B |
| JsonExtract          | mysql  |     61.39 μs |     0.563 μs |   0.201 μs | 0.0610 |     584 B |
| PreparedPointSelect  | mysql  |     33.62 μs |     0.377 μs |   0.134 μs | 0.0610 |     960 B |
