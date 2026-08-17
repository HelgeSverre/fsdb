<!--
sha: 6ab7286
date: 2026-08-17T19:49:57Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.107
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.107
  [Host]     : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD
  Job-UXZVNA : .NET 10.0.7 (10.0.726.21808), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target        | Mean          | Error        | StdDev       | Gen0    | Gen1   | Allocated |
|--------------------- |-------------- |--------------:|-------------:|-------------:|--------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**          |     **109.86 μs** |     **3.544 μs** |     **0.920 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb          |   7,431.67 μs |   392.120 μs |   139.834 μs |       - |      - |     568 B |
| InsertSingle         | fsdb          |     281.11 μs |    69.705 μs |    24.857 μs |       - |      - |    1240 B |
| InsertBatch100       | fsdb          |   4,917.56 μs | 1,018.235 μs |   363.112 μs |  7.8125 |      - |   96653 B |
| UpdateSingleRow      | fsdb          |      82.44 μs |     5.421 μs |     1.933 μs |       - |      - |     679 B |
| JoinUsersOrders      | fsdb          |   9,420.30 μs |   404.845 μs |   144.372 μs |       - |      - |     829 B |
| GroupByAggregate     | fsdb          |  25,301.34 μs | 2,055.691 μs |   733.079 μs |       - |      - |     562 B |
| JsonExtract          | fsdb          |     214.51 μs |     2.628 μs |     0.682 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb          |     102.04 μs |     3.664 μs |     1.306 μs |       - |      - |     960 B |
| UpdateByNonIndexed   | fsdb          |   5,504.22 μs |   644.316 μs |   229.769 μs |       - |      - |     728 B |
| **PointSelectByPk**      | **fsdb-wal**      |     **135.86 μs** |    **52.436 μs** |    **18.699 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb-wal      |   7,528.81 μs |   504.856 μs |   180.037 μs |       - |      - |     560 B |
| InsertSingle         | fsdb-wal      |     302.08 μs |   100.230 μs |    35.743 μs |       - |      - |    1240 B |
| InsertBatch100       | fsdb-wal      |   5,142.66 μs |   591.622 μs |   153.642 μs |  7.8125 |      - |   96653 B |
| UpdateSingleRow      | fsdb-wal      |     123.01 μs |     2.524 μs |     0.655 μs |       - |      - |     679 B |
| JoinUsersOrders      | fsdb-wal      |   9,274.40 μs |   996.949 μs |   355.522 μs |       - |      - |     829 B |
| GroupByAggregate     | fsdb-wal      |  26,831.24 μs | 1,073.458 μs |   382.806 μs |       - |      - |     562 B |
| JsonExtract          | fsdb-wal      |     218.10 μs |    13.921 μs |     4.964 μs |       - |      - |     504 B |
| PreparedPointSelect  | fsdb-wal      |      99.89 μs |     4.961 μs |     1.769 μs |       - |      - |     960 B |
| UpdateByNonIndexed   | fsdb-wal      |   5,594.12 μs |   672.680 μs |   239.884 μs |       - |      - |     730 B |
| **PointSelectByPk**      | **mysql**         |      **39.98 μs** |     **5.888 μs** |     **1.529 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql         |   1,965.34 μs |    11.833 μs |     3.073 μs |       - |      - |     644 B |
| InsertSingle         | mysql         |     155.71 μs |    59.606 μs |    21.256 μs |       - |      - |    1241 B |
| InsertBatch100       | mysql         |   1,339.60 μs |   396.172 μs |   141.279 μs |  9.7656 |      - |   96714 B |
| UpdateSingleRow      | mysql         |     105.60 μs |    19.685 μs |     7.020 μs |       - |      - |     743 B |
| JoinUsersOrders      | mysql         |     258.32 μs |     3.591 μs |     0.933 μs |       - |      - |     888 B |
| GroupByAggregate     | mysql         |  19,765.80 μs |   272.205 μs |    97.071 μs |       - |      - |     634 B |
| JsonExtract          | mysql         |      60.88 μs |     7.290 μs |     2.600 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql         |      34.36 μs |     4.263 μs |     1.520 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed   | mysql         | 196,877.81 μs | 1,186.013 μs |   422.944 μs |       - |      - |         - |
| **PointSelectByPk**      | **mysql-nofsync** |      **39.22 μs** |     **1.463 μs** |     **0.522 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit | mysql-nofsync |   1,901.42 μs |    28.778 μs |    10.262 μs |       - |      - |     642 B |
| InsertSingle         | mysql-nofsync |      34.23 μs |     3.117 μs |     1.112 μs |  0.1221 |      - |    1264 B |
| InsertBatch100       | mysql-nofsync |     810.49 μs |    77.200 μs |    27.530 μs | 11.7188 | 0.9766 |   98313 B |
| UpdateSingleRow      | mysql-nofsync |      39.55 μs |     3.464 μs |     0.900 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders      | mysql-nofsync |     265.16 μs |    25.525 μs |     9.102 μs |       - |      - |     889 B |
| GroupByAggregate     | mysql-nofsync |  19,590.73 μs |   274.473 μs |    71.280 μs |       - |      - |     624 B |
| JsonExtract          | mysql-nofsync |      64.67 μs |     5.170 μs |     1.844 μs |       - |      - |     584 B |
| PreparedPointSelect  | mysql-nofsync |      33.91 μs |     0.628 μs |     0.163 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed   | mysql-nofsync | 465,499.97 μs | 6,557.793 μs | 2,338.572 μs |       - |      - |         - |
