<!--
sha: 4c5cf01
date: 2026-08-17T21:48:29Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-LQDQJI : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method               | Target        | Mean          | Error         | StdDev        | Median        | Gen0   | Allocated |
|--------------------- |-------------- |--------------:|--------------:|--------------:|--------------:|-------:|----------:|
| **PointSelectByPk**      | **fsdb**          |     **118.40 μs** |      **8.891 μs** |      **2.309 μs** |     **118.91 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb          |  10,989.75 μs |    609.288 μs |    217.278 μs |  11,044.36 μs |      - |     581 B |
| InsertSingle         | fsdb          |     268.76 μs |     78.752 μs |     20.452 μs |     273.86 μs |      - |    1240 B |
| InsertBatch100       | fsdb          |   9,097.29 μs |  3,371.691 μs |  1,202.377 μs |   8,694.42 μs |      - |   94249 B |
| UpdateSingleRow      | fsdb          |   1,554.16 μs |    955.424 μs |    340.714 μs |   1,458.13 μs |      - |     681 B |
| JoinUsersOrders      | fsdb          |  11,129.70 μs |    969.349 μs |    345.679 μs |  11,117.70 μs |      - |     809 B |
| GroupByAggregate     | fsdb          |  70,509.57 μs | 15,784.976 μs |  5,629.075 μs |  67,278.72 μs |      - |         - |
| JsonExtract          | fsdb          |     230.94 μs |      0.937 μs |      0.243 μs |     230.99 μs |      - |     504 B |
| PreparedPointSelect  | fsdb          |     122.78 μs |     62.768 μs |     22.384 μs |     113.75 μs |      - |     960 B |
| UpdateByNonIndexed   | fsdb          |   5,795.90 μs |    417.537 μs |    108.433 μs |   5,768.24 μs |      - |     730 B |
| **PointSelectByPk**      | **fsdb-wal**      |     **122.71 μs** |     **13.561 μs** |      **4.836 μs** |     **123.38 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit | fsdb-wal      |  20,973.55 μs |  3,842.898 μs |    997.988 μs |  20,847.34 μs |      - |     592 B |
| InsertSingle         | fsdb-wal      |   1,424.68 μs |    587.969 μs |    209.676 μs |   1,297.68 μs |      - |    1235 B |
| InsertBatch100       | fsdb-wal      |   7,604.89 μs |  3,124.544 μs |  1,114.242 μs |   7,906.53 μs | 7.8125 |   96656 B |
| UpdateSingleRow      | fsdb-wal      |     168.65 μs |     15.949 μs |      4.142 μs |     170.39 μs |      - |     679 B |
| JoinUsersOrders      | fsdb-wal      |  15,075.59 μs |  7,200.852 μs |  2,567.893 μs |  15,462.87 μs |      - |     809 B |
| GroupByAggregate     | fsdb-wal      |  91,019.54 μs | 57,774.315 μs | 20,602.877 μs |  82,408.31 μs |      - |         - |
| JsonExtract          | fsdb-wal      |   1,305.02 μs |  1,467.709 μs |    381.159 μs |   1,375.42 μs |      - |     505 B |
| PreparedPointSelect  | fsdb-wal      |     111.19 μs |      2.220 μs |      0.576 μs |     111.09 μs |      - |     960 B |
| UpdateByNonIndexed   | fsdb-wal      |   6,212.41 μs |    186.415 μs |     66.477 μs |   6,228.24 μs |      - |     730 B |
| **PointSelectByPk**      | **mysql**         |      **37.85 μs** |      **0.775 μs** |      **0.201 μs** |      **37.99 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql         |   2,000.65 μs |     96.298 μs |     34.341 μs |   1,995.80 μs |      - |     644 B |
| InsertSingle         | mysql         |     126.58 μs |     13.416 μs |      4.784 μs |     127.24 μs |      - |    1240 B |
| InsertBatch100       | mysql         |   1,414.68 μs |    500.702 μs |    130.031 μs |   1,469.48 μs | 9.7656 |   96712 B |
| UpdateSingleRow      | mysql         |     132.70 μs |     26.583 μs |      9.480 μs |     128.00 μs |      - |     743 B |
| JoinUsersOrders      | mysql         |     280.71 μs |     12.752 μs |      4.547 μs |     280.58 μs |      - |     889 B |
| GroupByAggregate     | mysql         |  21,071.63 μs |  1,480.743 μs |    528.047 μs |  21,003.34 μs |      - |     634 B |
| JsonExtract          | mysql         |      62.05 μs |      6.020 μs |      2.147 μs |      62.31 μs | 0.0610 |     584 B |
| PreparedPointSelect  | mysql         |      31.25 μs |      4.435 μs |      1.581 μs |      31.81 μs | 0.0610 |     960 B |
| UpdateByNonIndexed   | mysql         | 209,669.33 μs |  9,982.187 μs |  3,559.744 μs | 209,263.12 μs |      - |         - |
| **PointSelectByPk**      | **mysql-nofsync** |      **40.73 μs** |      **2.950 μs** |      **1.052 μs** |      **40.84 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit | mysql-nofsync |   2,069.78 μs |    640.296 μs |    166.283 μs |   1,985.16 μs |      - |     644 B |
| InsertSingle         | mysql-nofsync |     205.51 μs |    572.194 μs |    204.050 μs |      83.07 μs |      - |    1240 B |
| InsertBatch100       | mysql-nofsync |   2,725.51 μs |    418.240 μs |    149.148 μs |   2,739.66 μs | 7.8125 |   96715 B |
| UpdateSingleRow      | mysql-nofsync |      66.62 μs |     72.298 μs |     18.776 μs |      62.11 μs | 0.0610 |     743 B |
| JoinUsersOrders      | mysql-nofsync |     238.45 μs |     42.277 μs |     15.077 μs |     237.08 μs |      - |     888 B |
| GroupByAggregate     | mysql-nofsync |  20,450.69 μs |    182.383 μs |     65.040 μs |  20,468.24 μs |      - |     634 B |
| JsonExtract          | mysql-nofsync |      62.24 μs |      2.222 μs |      0.792 μs |      62.52 μs |      - |     584 B |
| PreparedPointSelect  | mysql-nofsync |      34.86 μs |      5.083 μs |      1.813 μs |      33.96 μs | 0.0610 |     960 B |
| UpdateByNonIndexed   | mysql-nofsync | 110,230.41 μs |  1,346.150 μs |    349.591 μs | 110,178.98 μs |      - |         - |
