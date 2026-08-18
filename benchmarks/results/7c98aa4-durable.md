<!--
sha: 7c98aa4
date: 2026-08-18T23:38:26Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
fsdb server mode: in-memory (no --data-dir, no WAL/fsync)
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-SIPFIV : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3  

```
| Method                    | Target        | Mean          | Error        | StdDev       | Gen0    | Gen1   | Allocated |
|-------------------------- |-------------- |--------------:|-------------:|-------------:|--------:|-------:|----------:|
| **PointSelectByPk**           | **fsdb**          |     **140.23 μs** |    **10.294 μs** |     **2.673 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit      | fsdb          |  11,855.00 μs |   927.453 μs |   240.856 μs |       - |      - |     581 B |
| InsertSingle              | fsdb          |     320.80 μs |    71.087 μs |    25.350 μs |       - |      - |    1240 B |
| InsertBatch100            | fsdb          |   5,088.31 μs |   916.244 μs |   326.741 μs |  7.8125 |      - |   96653 B |
| UpdateSingleRow           | fsdb          |      86.66 μs |     6.129 μs |     1.592 μs |       - |      - |     679 B |
| JoinUsersOrders           | fsdb          |   6,075.55 μs |   274.385 μs |    71.257 μs |       - |      - |     819 B |
| GroupByAggregate          | fsdb          |  59,299.80 μs | 2,074.970 μs |   538.863 μs |       - |      - |         - |
| JsonExtract               | fsdb          |     167.75 μs |     9.881 μs |     3.524 μs |       - |      - |     504 B |
| PreparedPointSelect       | fsdb          |      71.23 μs |     3.140 μs |     1.120 μs |       - |      - |     960 B |
| UpdateByNonIndexed        | fsdb          |   5,880.77 μs |   308.194 μs |   109.905 μs |       - |      - |     730 B |
| PointSelectAsLimitedUser  | fsdb          |     137.93 μs |     7.509 μs |     1.950 μs |       - |      - |     880 B |
| SelectCurrentUser         | fsdb          |      87.16 μs |     6.550 μs |     2.336 μs |       - |      - |     504 B |
| ShowGrants                | fsdb          |      63.06 μs |     1.624 μs |     0.422 μs |       - |      - |     488 B |
| ShowFullTables            | fsdb          |      62.56 μs |     2.603 μs |     0.928 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable | fsdb          |     261.87 μs |    11.629 μs |     4.147 μs |       - |      - |     569 B |
| InfoSchemaTablesScan      | fsdb          |     174.76 μs |    19.658 μs |     7.010 μs |       - |      - |     520 B |
| UserPrivilegesScan        | fsdb          |     188.50 μs |    22.355 μs |     7.972 μs |       - |      - |     520 B |
| CreateGrantDropUser       | fsdb          |     182.84 μs |     6.670 μs |     2.379 μs |       - |      - |    1992 B |
| **PointSelectByPk**           | **fsdb-wal**      |     **138.78 μs** |     **5.909 μs** |     **2.107 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit      | fsdb-wal      |  11,511.50 μs |   272.434 μs |    70.750 μs |       - |      - |     581 B |
| InsertSingle              | fsdb-wal      |     399.30 μs |    84.586 μs |    30.164 μs |       - |      - |    1241 B |
| InsertBatch100            | fsdb-wal      |   5,702.07 μs |   798.855 μs |   207.460 μs |  7.8125 |      - |   96659 B |
| UpdateSingleRow           | fsdb-wal      |     502.44 μs |    28.273 μs |     7.342 μs |       - |      - |     681 B |
| JoinUsersOrders           | fsdb-wal      |   6,305.52 μs |   192.682 μs |    50.039 μs |       - |      - |     819 B |
| GroupByAggregate          | fsdb-wal      |  61,531.35 μs | 3,989.029 μs | 1,422.526 μs |       - |      - |         - |
| JsonExtract               | fsdb-wal      |     179.31 μs |    22.193 μs |     7.914 μs |       - |      - |     504 B |
| PreparedPointSelect       | fsdb-wal      |      75.42 μs |     3.967 μs |     1.415 μs |       - |      - |     960 B |
| UpdateByNonIndexed        | fsdb-wal      |   6,817.41 μs |   861.957 μs |   223.848 μs |       - |      - |     730 B |
| PointSelectAsLimitedUser  | fsdb-wal      |     142.99 μs |    14.406 μs |     5.137 μs |       - |      - |     880 B |
| SelectCurrentUser         | fsdb-wal      |      84.86 μs |     0.758 μs |     0.197 μs |       - |      - |     504 B |
| ShowGrants                | fsdb-wal      |      58.76 μs |     0.992 μs |     0.354 μs |       - |      - |     488 B |
| ShowFullTables            | fsdb-wal      |      59.63 μs |     1.147 μs |     0.409 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable | fsdb-wal      |     239.85 μs |     4.401 μs |     1.569 μs |       - |      - |     568 B |
| InfoSchemaTablesScan      | fsdb-wal      |     167.00 μs |     3.534 μs |     0.918 μs |       - |      - |     520 B |
| UserPrivilegesScan        | fsdb-wal      |     173.27 μs |     5.898 μs |     1.532 μs |       - |      - |     520 B |
| CreateGrantDropUser       | fsdb-wal      |     387.78 μs |    59.473 μs |    21.208 μs |       - |      - |    1993 B |
| **PointSelectByPk**           | **mysql**         |      **41.07 μs** |     **2.207 μs** |     **0.573 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit      | mysql         |   1,924.25 μs |    33.072 μs |     8.589 μs |       - |      - |     642 B |
| InsertSingle              | mysql         |     109.96 μs |    14.151 μs |     5.046 μs |  0.1221 |      - |    1240 B |
| InsertBatch100            | mysql         |   1,019.52 μs |    15.945 μs |     4.141 μs |  9.7656 |      - |   96714 B |
| UpdateSingleRow           | mysql         |     110.41 μs |     7.687 μs |     1.996 μs |       - |      - |     743 B |
| JoinUsersOrders           | mysql         |     230.44 μs |     5.118 μs |     1.329 μs |       - |      - |     888 B |
| GroupByAggregate          | mysql         |  20,693.46 μs | 1,015.708 μs |   263.776 μs |       - |      - |     623 B |
| JsonExtract               | mysql         |      64.49 μs |     9.762 μs |     3.481 μs |       - |      - |     584 B |
| PreparedPointSelect       | mysql         |      33.94 μs |     1.396 μs |     0.498 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed        | mysql         | 219,035.22 μs | 3,890.544 μs | 1,010.362 μs |       - |      - |         - |
| PointSelectAsLimitedUser  | mysql         |      40.95 μs |     1.276 μs |     0.455 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser         | mysql         |      29.93 μs |     0.765 μs |     0.273 μs |  0.0305 |      - |     504 B |
| ShowGrants                | mysql         |      30.17 μs |     0.503 μs |     0.179 μs |  0.0305 |      - |     488 B |
| ShowFullTables            | mysql         |     201.42 μs |     6.868 μs |     2.449 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable | mysql         |     205.64 μs |     8.936 μs |     2.321 μs |       - |      - |     568 B |
| InfoSchemaTablesScan      | mysql         |   1,257.38 μs |    30.798 μs |     7.998 μs |       - |      - |     602 B |
| UserPrivilegesScan        | mysql         |      88.03 μs |     2.817 μs |     1.005 μs |       - |      - |     600 B |
| CreateGrantDropUser       | mysql         |   1,855.77 μs |    66.203 μs |    17.193 μs |       - |      - |    1994 B |
| **PointSelectByPk**           | **mysql-nofsync** |      **40.04 μs** |     **0.672 μs** |     **0.175 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit      | mysql-nofsync |   1,933.06 μs |    31.912 μs |    11.380 μs |       - |      - |     642 B |
| InsertSingle              | mysql-nofsync |      31.63 μs |     1.233 μs |     0.440 μs |  0.1221 |      - |    1264 B |
| InsertBatch100            | mysql-nofsync |     787.46 μs |    26.116 μs |     9.313 μs | 11.7188 | 0.9766 |   98313 B |
| UpdateSingleRow           | mysql-nofsync |      40.68 μs |     4.061 μs |     1.055 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders           | mysql-nofsync |     238.06 μs |    38.747 μs |    10.063 μs |       - |      - |     888 B |
| GroupByAggregate          | mysql-nofsync |  20,825.64 μs | 1,016.425 μs |   362.467 μs |       - |      - |     611 B |
| JsonExtract               | mysql-nofsync |      61.52 μs |     4.640 μs |     1.655 μs |       - |      - |     584 B |
| PreparedPointSelect       | mysql-nofsync |      34.26 μs |     1.484 μs |     0.529 μs |  0.0610 |      - |     960 B |
| UpdateByNonIndexed        | mysql-nofsync | 473,742.38 μs | 8,661.600 μs | 2,249.390 μs |       - |      - |         - |
| PointSelectAsLimitedUser  | mysql-nofsync |      43.30 μs |    10.428 μs |     3.719 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser         | mysql-nofsync |      30.21 μs |     2.945 μs |     1.050 μs |  0.0305 |      - |     504 B |
| ShowGrants                | mysql-nofsync |      29.65 μs |     1.239 μs |     0.442 μs |  0.0305 |      - |     488 B |
| ShowFullTables            | mysql-nofsync |     207.41 μs |     8.639 μs |     3.081 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable | mysql-nofsync |     204.94 μs |    13.540 μs |     3.516 μs |       - |      - |     568 B |
| InfoSchemaTablesScan      | mysql-nofsync |   1,214.38 μs |    35.623 μs |    12.703 μs |       - |      - |     522 B |
| UserPrivilegesScan        | mysql-nofsync |      88.51 μs |     7.820 μs |     2.789 μs |       - |      - |     600 B |
| CreateGrantDropUser       | mysql-nofsync |   1,600.29 μs |   116.276 μs |    41.465 μs |       - |      - |    1994 B |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-GHAZPR : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

InvocationCount=32  IterationCount=6  UnrollFactor=1  
WarmupCount=3  

```
| Method                   | Target        | Mean     | Error    | StdDev   | Allocated |
|------------------------- |-------------- |---------:|---------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**          | **250.1 μs** | **82.25 μs** | **29.33 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **fsdb-wal**      | **227.3 μs** | **22.22 μs** |  **5.77 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **mysql**         | **207.2 μs** | **22.01 μs** |  **7.85 μs** |  **33.63 KB** |
| **ConnectAuthenticateClose** | **mysql-nofsync** | **206.2 μs** | **23.78 μs** |  **8.48 μs** |  **33.63 KB** |
