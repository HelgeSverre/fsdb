<!--
sha: 48ea75e
date: 2026-08-20T21:35:08Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: fsdb in-memory/WAL; MySQL durable/no-fsync
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-UPUMEP : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                     | Target        | Mean          | Error         | StdDev        | Gen0    | Gen1   | Allocated |
|--------------------------- |-------------- |--------------:|--------------:|--------------:|--------:|-------:|----------:|
| **InsertBatch100**             | **fsdb**          |   **1,328.11 μs** |    **222.886 μs** |     **79.483 μs** |  **9.7656** |      **-** |   **96651 B** |
| ReplaceNewRow              | fsdb          |      67.66 μs |      3.806 μs |      1.357 μs |  0.1221 |      - |    1280 B |
| PreparedPointSelect        | fsdb          |      78.69 μs |      2.257 μs |      0.805 μs |       - |      - |     960 B |
| PointSelectAsLimitedUser   | fsdb          |     144.05 μs |      4.033 μs |      1.047 μs |       - |      - |     880 B |
| SelectCurrentUser          | fsdb          |      86.02 μs |      1.451 μs |      0.377 μs |       - |      - |     504 B |
| ShowGrants                 | fsdb          |      57.70 μs |      0.567 μs |      0.202 μs |       - |      - |     488 B |
| ShowFullTables             | fsdb          |      63.55 μs |      1.687 μs |      0.438 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable  | fsdb          |     218.19 μs |      7.538 μs |      2.688 μs |       - |      - |     569 B |
| InfoSchemaTablesScan       | fsdb          |     200.44 μs |     14.860 μs |      3.859 μs |       - |      - |     520 B |
| UserPrivilegesScan         | fsdb          |     173.77 μs |      9.309 μs |      3.320 μs |       - |      - |     520 B |
| CreateGrantDropUser        | fsdb          |   1,815.73 μs |  1,137.726 μs |    405.724 μs |       - |      - |    1993 B |
| **InsertBatch100**             | **fsdb-wal**      |   **1,309.38 μs** |     **59.844 μs** |     **15.541 μs** |  **9.7656** |      **-** |   **96650 B** |
| ReplaceNewRow              | fsdb-wal      |     108.32 μs |      4.968 μs |      1.772 μs |  0.1221 |      - |    1280 B |
| PreparedPointSelect        | fsdb-wal      |      74.52 μs |      2.248 μs |      0.802 μs |       - |      - |     960 B |
| PointSelectAsLimitedUser   | fsdb-wal      |     142.20 μs |      5.161 μs |      1.841 μs |       - |      - |     880 B |
| SelectCurrentUser          | fsdb-wal      |      85.99 μs |      2.988 μs |      0.776 μs |       - |      - |     504 B |
| ShowGrants                 | fsdb-wal      |      58.51 μs |      2.303 μs |      0.821 μs |       - |      - |     488 B |
| ShowFullTables             | fsdb-wal      |      62.95 μs |      1.444 μs |      0.375 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable  | fsdb-wal      |     218.69 μs |     17.315 μs |      4.497 μs |       - |      - |     569 B |
| InfoSchemaTablesScan       | fsdb-wal      |     199.34 μs |      5.935 μs |      2.116 μs |       - |      - |     520 B |
| UserPrivilegesScan         | fsdb-wal      |     192.27 μs |      9.054 μs |      3.229 μs |       - |      - |     520 B |
| CreateGrantDropUser        | fsdb-wal      |   1,205.07 μs |    535.177 μs |    190.849 μs |       - |      - |    1992 B |
| **InsertBatch100**             | **mysql**         |   **1,271.21 μs** |    **476.717 μs** |    **170.002 μs** |  **9.7656** |      **-** |   **96714 B** |
| ReplaceNewRow              | mysql         |      95.47 μs |      5.195 μs |      1.853 μs |  0.1221 |      - |    1280 B |
| PreparedPointSelect        | mysql         |      37.60 μs |     16.874 μs |      6.017 μs |  0.0610 |      - |     960 B |
| PointSelectAsLimitedUser   | mysql         |      40.51 μs |      3.070 μs |      1.095 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser          | mysql         |      31.23 μs |      4.691 μs |      1.673 μs |  0.0305 |      - |     504 B |
| ShowGrants                 | mysql         |      31.43 μs |      3.841 μs |      1.370 μs |  0.0305 |      - |     488 B |
| ShowFullTables             | mysql         |     253.31 μs |     50.733 μs |     18.092 μs |       - |      - |     505 B |
| InfoSchemaColumnsForTable  | mysql         |     266.68 μs |     24.345 μs |      8.682 μs |       - |      - |     569 B |
| InfoSchemaTablesScan       | mysql         |   1,421.66 μs |     63.511 μs |     22.649 μs |       - |      - |     602 B |
| UserPrivilegesScan         | mysql         |      92.48 μs |      2.242 μs |      0.799 μs |       - |      - |     600 B |
| CreateGrantDropUser        | mysql         |   1,903.44 μs |     63.054 μs |     16.375 μs |       - |      - |    1994 B |
| **InsertBatch100**             | **mysql-nofsync** |     **801.13 μs** |     **55.806 μs** |     **14.493 μs** | **11.7188** | **0.9766** |   **98313 B** |
| ReplaceNewRow              | mysql-nofsync |      41.88 μs |      1.300 μs |      0.338 μs |  0.1221 |      - |    1296 B |
| PreparedPointSelect        | mysql-nofsync |      33.71 μs |      0.446 μs |      0.116 μs |  0.0610 |      - |     960 B |
| PointSelectAsLimitedUser   | mysql-nofsync |      39.61 μs |      0.399 μs |      0.104 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser          | mysql-nofsync |      29.29 μs |      1.468 μs |      0.381 μs |  0.0305 |      - |     504 B |
| ShowGrants                 | mysql-nofsync |      29.74 μs |      0.629 μs |      0.224 μs |       - |      - |     488 B |
| ShowFullTables             | mysql-nofsync |     247.53 μs |     35.391 μs |     12.621 μs |       - |      - |     505 B |
| InfoSchemaColumnsForTable  | mysql-nofsync |     234.46 μs |     30.879 μs |     11.012 μs |       - |      - |     568 B |
| InfoSchemaTablesScan       | mysql-nofsync |   1,191.52 μs |     97.538 μs |     34.783 μs |       - |      - |     602 B |
| UserPrivilegesScan         | mysql-nofsync |      90.03 μs |      3.366 μs |      0.874 μs |       - |      - |     600 B |
| CreateGrantDropUser        | mysql-nofsync |   1,644.44 μs |     16.594 μs |      4.309 μs |       - |      - |    1994 B |
| **UpsertExistingByPk**         | **fsdb**          |      **85.57 μs** |      **5.516 μs** |      **1.967 μs** |  **0.1221** |      **-** |    **1350 B** |
| TransactionTwoPointUpdates | fsdb          |   5,007.43 μs |    859.194 μs |    306.397 μs |       - |      - |    2168 B |
| ComputedProjection         | fsdb          |     139.48 μs |      8.225 μs |      2.933 μs |       - |      - |     848 B |
| ViewFilterLimit            | fsdb          |  13,542.14 μs |    349.064 μs |    124.479 μs |       - |      - |     557 B |
| ViewAggregate              | fsdb          |     113.98 μs |     23.196 μs |      8.272 μs |       - |      - |     520 B |
| RecursiveCte100            | fsdb          |     418.19 μs |     37.422 μs |     13.345 μs |       - |      - |     505 B |
| CorrelatedJsonTable        | fsdb          |  16,033.33 μs |  1,693.551 μs |    603.937 μs |       - |      - |     546 B |
| InsertCheckedGenerated     | fsdb          |      69.98 μs |      3.441 μs |      1.227 μs |       - |      - |     704 B |
| InsertWithAfterTrigger     | fsdb          |   3,963.37 μs |    388.202 μs |    138.437 μs |       - |      - |     715 B |
| **UpsertExistingByPk**         | **fsdb-wal**      |   **1,357.53 μs** |     **74.152 μs** |     **26.443 μs** |       **-** |      **-** |    **1353 B** |
| TransactionTwoPointUpdates | fsdb-wal      |   7,456.45 μs |    308.289 μs |    109.939 μs |       - |      - |    2169 B |
| ComputedProjection         | fsdb-wal      |     127.41 μs |      2.866 μs |      0.744 μs |       - |      - |     848 B |
| ViewFilterLimit            | fsdb-wal      |  12,821.86 μs |    430.646 μs |    153.573 μs |       - |      - |     553 B |
| ViewAggregate              | fsdb-wal      |     105.39 μs |      6.973 μs |      2.486 μs |       - |      - |     520 B |
| RecursiveCte100            | fsdb-wal      |     419.40 μs |      3.174 μs |      0.824 μs |       - |      - |     505 B |
| CorrelatedJsonTable        | fsdb-wal      |  15,892.18 μs |    579.072 μs |    150.383 μs |       - |      - |     546 B |
| InsertCheckedGenerated     | fsdb-wal      |      99.32 μs |     11.523 μs |      2.992 μs |       - |      - |     704 B |
| InsertWithAfterTrigger     | fsdb-wal      |   4,421.17 μs |    166.655 μs |     59.431 μs |       - |      - |     715 B |
| **UpsertExistingByPk**         | **mysql**         |     **121.19 μs** |      **6.853 μs** |      **1.780 μs** |  **0.1221** |      **-** |    **1350 B** |
| TransactionTwoPointUpdates | mysql         |     200.98 μs |     42.006 μs |     10.909 μs |  0.2441 |      - |    2287 B |
| ComputedProjection         | mysql         |      41.88 μs |      4.420 μs |      1.576 μs |  0.0610 |      - |     848 B |
| ViewFilterLimit            | mysql         |      58.30 μs |      2.492 μs |      0.647 μs |  0.0610 |      - |     536 B |
| ViewAggregate              | mysql         |  20,600.15 μs |    468.219 μs |    166.971 μs |       - |      - |     634 B |
| RecursiveCte100            | mysql         |      56.51 μs |      1.613 μs |      0.575 μs |  0.0610 |      - |     584 B |
| CorrelatedJsonTable        | mysql         |     125.20 μs |      4.748 μs |      1.693 μs |       - |      - |     584 B |
| InsertCheckedGenerated     | mysql         |     102.02 μs |      1.667 μs |      0.433 μs |       - |      - |     704 B |
| InsertWithAfterTrigger     | mysql         |     114.39 μs |      8.986 μs |      3.204 μs |       - |      - |     704 B |
| **UpsertExistingByPk**         | **mysql-nofsync** |      **40.06 μs** |     **13.428 μs** |      **4.789 μs** |  **0.1221** |      **-** |    **1350 B** |
| TransactionTwoPointUpdates | mysql-nofsync |     116.29 μs |     36.761 μs |     13.109 μs |  0.2441 |      - |    2287 B |
| ComputedProjection         | mysql-nofsync |      42.45 μs |      1.999 μs |      0.713 μs |  0.0610 |      - |     848 B |
| ViewFilterLimit            | mysql-nofsync |      59.82 μs |      2.781 μs |      0.992 μs |  0.0610 |      - |     536 B |
| ViewAggregate              | mysql-nofsync |  20,345.95 μs |    243.531 μs |     86.846 μs |       - |      - |     634 B |
| RecursiveCte100            | mysql-nofsync |      57.17 μs |      1.899 μs |      0.677 μs |  0.0610 |      - |     584 B |
| CorrelatedJsonTable        | mysql-nofsync |     129.18 μs |      5.915 μs |      2.109 μs |       - |      - |     584 B |
| InsertCheckedGenerated     | mysql-nofsync |      28.55 μs |      2.228 μs |      0.795 μs |  0.0610 |      - |     704 B |
| InsertWithAfterTrigger     | mysql-nofsync |      42.34 μs |      1.324 μs |      0.344 μs |  0.0610 |      - |     720 B |
| **WindowTopOrders**            | **fsdb**          | **379,253.46 μs** | **26,933.761 μs** |  **6,994.612 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch      | fsdb          |  18,998.43 μs |    760.714 μs |    197.555 μs |       - |      - |     546 B |
| **WindowTopOrders**            | **fsdb-wal**      | **377,658.78 μs** | **60,563.578 μs** | **21,597.555 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch      | fsdb-wal      |  19,451.40 μs |    472.777 μs |    122.779 μs |       - |      - |     546 B |
| **WindowTopOrders**            | **mysql**         |  **49,977.18 μs** |  **1,458.684 μs** |    **378.816 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch      | mysql         |   1,216.59 μs |     58.197 μs |     20.754 μs |       - |      - |     506 B |
| **WindowTopOrders**            | **mysql-nofsync** |  **50,257.35 μs** |  **2,740.280 μs** |    **977.210 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch      | mysql-nofsync |   1,193.31 μs |     22.863 μs |      8.153 μs |       - |      - |     506 B |
| **PointSelectByPk**            | **fsdb**          |     **150.26 μs** |      **5.082 μs** |      **1.812 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit       | fsdb          |  11,792.28 μs |    203.690 μs |     52.898 μs |       - |      - |     576 B |
| InsertSingle               | fsdb          |      69.34 μs |      2.185 μs |      0.567 μs |  0.1221 |      - |    1240 B |
| ReplaceExistingByPk        | fsdb          |      74.40 μs |      4.217 μs |      1.504 μs |  0.1221 |      - |    1031 B |
| UpdateSingleRow            | fsdb          |      95.35 μs |      6.699 μs |      2.389 μs |       - |      - |     679 B |
| JoinUsersOrders            | fsdb          |   8,015.99 μs |    659.642 μs |    235.235 μs |       - |      - |     829 B |
| GroupByAggregate           | fsdb          |  84,294.82 μs | 13,327.561 μs |  4,752.736 μs |       - |      - |         - |
| JsonExtract                | fsdb          |     185.52 μs |      7.381 μs |      1.917 μs |       - |      - |     504 B |
| UpdateByNonIndexed         | fsdb          |   8,047.58 μs |    423.231 μs |    150.928 μs |       - |      - |     741 B |
| **PointSelectByPk**            | **fsdb-wal**      |     **154.78 μs** |      **3.369 μs** |      **1.202 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit       | fsdb-wal      |  11,935.19 μs |    455.693 μs |    162.504 μs |       - |      - |     577 B |
| InsertSingle               | fsdb-wal      |     106.14 μs |     10.327 μs |      3.683 μs |  0.1221 |      - |    1240 B |
| ReplaceExistingByPk        | fsdb-wal      |     984.70 μs |    199.300 μs |     71.072 μs |       - |      - |    1034 B |
| UpdateSingleRow            | fsdb-wal      |   1,472.81 μs |     95.166 μs |     33.937 μs |       - |      - |     682 B |
| JoinUsersOrders            | fsdb-wal      |   7,897.46 μs |    236.120 μs |     84.203 μs |       - |      - |     829 B |
| GroupByAggregate           | fsdb-wal      |  83,338.27 μs |  8,580.667 μs |  3,059.949 μs |       - |      - |         - |
| JsonExtract                | fsdb-wal      |     183.55 μs |      3.464 μs |      1.235 μs |       - |      - |     505 B |
| UpdateByNonIndexed         | fsdb-wal      |  10,027.50 μs |    533.828 μs |    190.368 μs |       - |      - |     741 B |
| **PointSelectByPk**            | **mysql**         |      **41.61 μs** |      **1.428 μs** |      **0.509 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit       | mysql         |   2,108.46 μs |    503.016 μs |    179.380 μs |       - |      - |     644 B |
| InsertSingle               | mysql         |     120.17 μs |      4.132 μs |      1.473 μs |  0.1221 |      - |    1240 B |
| ReplaceExistingByPk        | mysql         |     144.27 μs |      4.792 μs |      1.709 μs |       - |      - |    1031 B |
| UpdateSingleRow            | mysql         |     113.08 μs |     14.759 μs |      5.263 μs |       - |      - |     743 B |
| JoinUsersOrders            | mysql         |     521.10 μs |    284.158 μs |    101.334 μs |       - |      - |     888 B |
| GroupByAggregate           | mysql         |  20,444.31 μs |    497.068 μs |    129.087 μs |       - |      - |     602 B |
| JsonExtract                | mysql         |      65.06 μs |      1.006 μs |      0.359 μs |       - |      - |     584 B |
| UpdateByNonIndexed         | mysql         |   3,575.48 μs |    163.332 μs |     58.246 μs |       - |      - |     787 B |
| **PointSelectByPk**            | **mysql-nofsync** |      **41.90 μs** |      **1.799 μs** |      **0.642 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit       | mysql-nofsync |   1,981.38 μs |     45.372 μs |     16.180 μs |       - |      - |     644 B |
| InsertSingle               | mysql-nofsync |      34.15 μs |      0.436 μs |      0.155 μs |  0.1221 |      - |    1264 B |
| ReplaceExistingByPk        | mysql-nofsync |      57.17 μs |      2.568 μs |      0.916 μs |  0.1221 |      - |    1031 B |
| UpdateSingleRow            | mysql-nofsync |      42.64 μs |      2.021 μs |      0.525 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders            | mysql-nofsync |     251.85 μs |     29.643 μs |     10.571 μs |       - |      - |     888 B |
| GroupByAggregate           | mysql-nofsync |  21,343.65 μs |    291.014 μs |     75.575 μs |       - |      - |     634 B |
| JsonExtract                | mysql-nofsync |      64.07 μs |      3.665 μs |      1.307 μs |       - |      - |     584 B |
| UpdateByNonIndexed         | mysql-nofsync |   3,456.99 μs |     42.869 μs |     11.133 μs |       - |      - |     787 B |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-KIDJUO : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

InvocationCount=32  IterationCount=6  UnrollFactor=1
WarmupCount=3

```
| Method                   | Target        | Mean     | Error    | StdDev   | Allocated |
|------------------------- |-------------- |---------:|---------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**          | **281.2 μs** | **68.06 μs** | **24.27 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **fsdb-wal**      | **279.1 μs** | **46.37 μs** | **12.04 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **mysql**         | **251.0 μs** | **59.99 μs** | **21.39 μs** |  **33.63 KB** |
| **ConnectAuthenticateClose** | **mysql-nofsync** | **240.7 μs** | **24.66 μs** |  **8.79 μs** |  **33.63 KB** |
