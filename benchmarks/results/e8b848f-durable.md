<!--
sha: e8b848f
date: 2026-08-17T20:15:49Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-GNKPQH : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target        | Mean          | Error        | StdDev       | Gen0    | Gen1   | Allocated |
|--------------------- |-------------- |--------------:|-------------:|-------------:|--------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**          |     **113.58 μs** |     **1.452 μs** |     **0.518 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb          |   7,280.33 μs |   196.319 μs |    70.009 μs |       - |      - |     560 B |
| InsertSingle         | fsdb          |     276.40 μs |    92.119 μs |    32.851 μs |       - |      - |    1241 B |
| InsertBatch100       | fsdb          |   4,841.00 μs |   901.795 μs |   321.589 μs |  7.8125 |      - |   96653 B |
| UpdateSingleRow      | fsdb          |      82.18 μs |     3.623 μs |     1.292 μs |       - |      - |     679 B |
| JoinUsersOrders      | fsdb          |   9,156.88 μs |   329.259 μs |    85.507 μs |       - |      - |     829 B |
| GroupByAggregate     | fsdb          |  26,535.25 μs | 1,655.002 μs |   590.190 μs |       - |      - |     562 B |
| JsonExtract          | fsdb          |     209.99 μs |     6.565 μs |     2.341 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb          |     107.15 μs |    24.711 μs |     8.812 μs |       - |      - |     960 B |
| UpdateByNonIndexed   | fsdb          |   5,667.70 μs |   364.060 μs |   129.827 μs |       - |      - |     728 B |
| **PointSelectByPk**      | **fsdb-wal**      |     **107.60 μs** |     **1.917 μs** |     **0.498 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb-wal      |   7,394.67 μs |   485.850 μs |   173.259 μs |       - |      - |     571 B |
| InsertSingle         | fsdb-wal      |     302.19 μs |    78.946 μs |    28.153 μs |       - |      - |    1240 B |
| InsertBatch100       | fsdb-wal      |   4,972.25 μs |   982.620 μs |   350.412 μs |  7.8125 |      - |   96652 B |
| UpdateSingleRow      | fsdb-wal      |     122.37 μs |     4.910 μs |     1.275 μs |       - |      - |     679 B |
| JoinUsersOrders      | fsdb-wal      |   9,023.94 μs |   339.265 μs |   120.985 μs |       - |      - |     829 B |
| GroupByAggregate     | fsdb-wal      |  25,762.09 μs | 1,465.597 μs |   380.611 μs |       - |      - |     562 B |
| JsonExtract          | fsdb-wal      |     206.93 μs |     9.464 μs |     3.375 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb-wal      |     100.28 μs |    10.373 μs |     3.699 μs |       - |      - |     960 B |
| UpdateByNonIndexed   | fsdb-wal      |   5,547.24 μs |   290.916 μs |    75.550 μs |       - |      - |     730 B |
| **PointSelectByPk**      | **mysql**         |      **40.79 μs** |     **1.305 μs** |     **0.339 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql         |   1,907.95 μs |     5.492 μs |     1.426 μs |       - |      - |     642 B |
| InsertSingle         | mysql         |     115.62 μs |     8.344 μs |     2.976 μs |  0.1221 |      - |    1240 B |
| InsertBatch100       | mysql         |   1,046.64 μs |    48.785 μs |    12.669 μs |  9.7656 |      - |   96714 B |
| UpdateSingleRow      | mysql         |     116.79 μs |     7.363 μs |     2.626 μs |       - |      - |     743 B |
| JoinUsersOrders      | mysql         |     259.17 μs |     7.053 μs |     2.515 μs |       - |      - |     889 B |
| GroupByAggregate     | mysql         |  20,382.84 μs |   338.989 μs |   120.887 μs |       - |      - |     634 B |
| JsonExtract          | mysql         |      62.99 μs |     0.791 μs |     0.282 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql         |      32.89 μs |    10.993 μs |     2.855 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed   | mysql         | 218,402.16 μs | 4,381.441 μs | 1,137.846 μs |       - |      - |         - |
| **PointSelectByPk**      | **mysql-nofsync** |      **40.59 μs** |     **0.706 μs** |     **0.252 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql-nofsync |   1,912.78 μs |    24.160 μs |     6.274 μs |       - |      - |     642 B |
| InsertSingle         | mysql-nofsync |      33.19 μs |     1.077 μs |     0.280 μs |  0.1221 |      - |    1264 B |
| InsertBatch100       | mysql-nofsync |     795.70 μs |    46.049 μs |    16.422 μs | 11.7188 | 0.9766 |   98313 B |
| UpdateSingleRow      | mysql-nofsync |      40.31 μs |     0.459 μs |     0.119 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders      | mysql-nofsync |     236.67 μs |    12.793 μs |     4.562 μs |       - |      - |     888 B |
| GroupByAggregate     | mysql-nofsync |  20,412.87 μs |   807.474 μs |   287.953 μs |       - |      - |     634 B |
| JsonExtract          | mysql-nofsync |      62.46 μs |     2.163 μs |     0.771 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql-nofsync |      34.24 μs |     0.699 μs |     0.249 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed   | mysql-nofsync | 462,992.08 μs | 9,005.681 μs | 3,211.513 μs |       - |      - |         - |
