<!--
sha: cbbdfb4
date: 2026-08-17T18:47:16Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-PLOTCP : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target        | Mean         | Error         | StdDev       | Gen0    | Gen1   | Allocated |
|--------------------- |-------------- |-------------:|--------------:|-------------:|--------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**          |    **116.38 μs** |     **12.586 μs** |     **4.488 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb          |  6,668.79 μs |    170.845 μs |    44.368 μs |       - |      - |     568 B |
| InsertSingle         | fsdb          |    273.89 μs |     69.010 μs |    24.610 μs |       - |      - |    1240 B |
| InsertBatch100       | fsdb          |  5,066.53 μs |  1,424.142 μs |   507.863 μs |  7.8125 |      - |   96653 B |
| UpdateSingleRow      | fsdb          |    373.00 μs |    802.986 μs |   286.353 μs |       - |      - |     679 B |
| JoinUsersOrders      | fsdb          |  9,300.28 μs |    832.571 μs |   216.216 μs |       - |      - |     829 B |
| GroupByAggregate     | fsdb          | 26,173.64 μs |  1,478.133 μs |   527.117 μs |       - |      - |     562 B |
| JsonExtract          | fsdb          |    226.69 μs |     12.396 μs |     4.421 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb          |    107.42 μs |      3.455 μs |     0.897 μs |       - |      - |     960 B |
| **PointSelectByPk**      | **fsdb-wal**      |    **130.86 μs** |      **2.839 μs** |     **1.012 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb-wal      |  7,105.70 μs |    314.804 μs |   112.262 μs |       - |      - |     568 B |
| InsertSingle         | fsdb-wal      |  7,750.59 μs |  3,162.993 μs | 1,127.954 μs |       - |      - |    1243 B |
| InsertBatch100       | fsdb-wal      | 11,963.93 μs | 10,566.723 μs | 2,744.144 μs |       - |      - |   94269 B |
| UpdateSingleRow      | fsdb-wal      |  5,635.53 μs |  1,649.414 μs |   588.197 μs |       - |      - |     687 B |
| JoinUsersOrders      | fsdb-wal      |  9,649.17 μs |    857.270 μs |   305.711 μs |       - |      - |     820 B |
| GroupByAggregate     | fsdb-wal      | 26,833.85 μs |    592.392 μs |   153.842 μs |       - |      - |     562 B |
| JsonExtract          | fsdb-wal      |    219.25 μs |     33.153 μs |     8.610 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb-wal      |     97.78 μs |      1.826 μs |     0.474 μs |       - |      - |     960 B |
| **PointSelectByPk**      | **mysql**         |     **40.32 μs** |      **0.534 μs** |     **0.190 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql         |  1,900.04 μs |     17.963 μs |     6.406 μs |       - |      - |     642 B |
| InsertSingle         | mysql         |    113.17 μs |      9.062 μs |     3.232 μs |  0.1221 |      - |    1240 B |
| InsertBatch100       | mysql         |  1,084.40 μs |     39.722 μs |    10.316 μs |  9.7656 |      - |   96714 B |
| UpdateSingleRow      | mysql         |    102.99 μs |      9.898 μs |     3.530 μs |       - |      - |     743 B |
| JoinUsersOrders      | mysql         |    255.63 μs |      4.356 μs |     1.131 μs |       - |      - |     889 B |
| GroupByAggregate     | mysql         | 19,654.43 μs |    116.044 μs |    30.136 μs |       - |      - |     634 B |
| JsonExtract          | mysql         |     61.84 μs |      0.508 μs |     0.181 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql         |     33.82 μs |      0.348 μs |     0.124 μs |  0.0610 |      - |     960 B |
| **PointSelectByPk**      | **mysql-nofsync** |     **39.78 μs** |      **0.622 μs** |     **0.222 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql-nofsync |  1,889.19 μs |      7.344 μs |     1.907 μs |       - |      - |     642 B |
| InsertSingle         | mysql-nofsync |     33.22 μs |      1.925 μs |     0.686 μs |  0.1221 |      - |    1264 B |
| InsertBatch100       | mysql-nofsync |    786.57 μs |     40.441 μs |    14.422 μs | 11.7188 | 0.9766 |   98313 B |
| UpdateSingleRow      | mysql-nofsync |     39.89 μs |      0.455 μs |     0.118 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders      | mysql-nofsync |    265.09 μs |     23.906 μs |     8.525 μs |       - |      - |     889 B |
| GroupByAggregate     | mysql-nofsync | 20,017.89 μs |    217.492 μs |    77.560 μs |       - |      - |     634 B |
| JsonExtract          | mysql-nofsync |     61.86 μs |      1.031 μs |     0.368 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql-nofsync |     33.44 μs |      2.048 μs |     0.532 μs |  0.0610 |      - |     960 B |
