<!--
sha: ebc3fca
date: 2026-08-24T19:31:14Z
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
  Job-PQCRBH : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                      | Target        | Mean          | Error          | StdDev        | Gen0    | Gen1   | Allocated |
|---------------------------- |-------------- |--------------:|---------------:|--------------:|--------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**          |   **2,074.72 μs** |      **74.536 μs** |     **19.357 μs** |  **7.8125** |      **-** |   **96653 B** |
| ReplaceNewRow               | fsdb          |      84.97 μs |       8.525 μs |      3.040 μs |  0.1221 |      - |    1280 B |
| PreparedPointSelect         | fsdb          |      84.02 μs |       3.621 μs |      0.940 μs |       - |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb          |     174.51 μs |      15.159 μs |      5.406 μs |       - |      - |     880 B |
| SelectCurrentUser           | fsdb          |      99.49 μs |       1.286 μs |      0.334 μs |       - |      - |     504 B |
| ShowGrants                  | fsdb          |      67.00 μs |       2.025 μs |      0.526 μs |       - |      - |     488 B |
| ShowFullTables              | fsdb          |      75.81 μs |       2.129 μs |      0.759 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb          |     551.18 μs |      40.447 μs |     14.424 μs |       - |      - |     569 B |
| InfoSchemaTablesScan        | fsdb          |     218.58 μs |       4.573 μs |      1.188 μs |       - |      - |     520 B |
| UserPrivilegesScan          | fsdb          |     198.88 μs |       3.025 μs |      1.079 μs |       - |      - |     520 B |
| CreateGrantDropUser         | fsdb          |   2,026.37 μs |   1,311.378 μs |    467.650 μs |       - |      - |    1993 B |
| **InsertBatch100**              | **fsdb-wal**      |   **2,226.13 μs** |      **77.555 μs** |     **20.141 μs** |  **7.8125** |      **-** |   **96653 B** |
| ReplaceNewRow               | fsdb-wal      |     126.95 μs |      26.862 μs |      9.579 μs |       - |      - |    1280 B |
| PreparedPointSelect         | fsdb-wal      |      82.30 μs |       1.903 μs |      0.679 μs |       - |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb-wal      |     170.38 μs |       2.066 μs |      0.737 μs |       - |      - |     880 B |
| SelectCurrentUser           | fsdb-wal      |      97.22 μs |       1.548 μs |      0.402 μs |       - |      - |     504 B |
| ShowGrants                  | fsdb-wal      |      67.19 μs |       1.103 μs |      0.393 μs |       - |      - |     488 B |
| ShowFullTables              | fsdb-wal      |      74.72 μs |       0.676 μs |      0.241 μs |       - |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb-wal      |     599.52 μs |      13.835 μs |      4.934 μs |       - |      - |     569 B |
| InfoSchemaTablesScan        | fsdb-wal      |     221.72 μs |       7.832 μs |      2.793 μs |       - |      - |     521 B |
| UserPrivilegesScan          | fsdb-wal      |     203.95 μs |      11.858 μs |      4.229 μs |       - |      - |     521 B |
| CreateGrantDropUser         | fsdb-wal      |   1,267.81 μs |     598.686 μs |    213.497 μs |       - |      - |    1993 B |
| **InsertBatch100**              | **mysql**         |   **1,220.55 μs** |      **66.682 μs** |     **17.317 μs** |  **9.7656** |      **-** |   **96714 B** |
| ReplaceNewRow               | mysql         |      97.63 μs |       8.215 μs |      2.930 μs |  0.1221 |      - |    1280 B |
| PreparedPointSelect         | mysql         |      33.79 μs |       0.187 μs |      0.067 μs |  0.0610 |      - |     960 B |
| PointSelectAsLimitedUser    | mysql         |      39.74 μs |       0.566 μs |      0.202 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser           | mysql         |      28.99 μs |       0.651 μs |      0.232 μs |  0.0305 |      - |     504 B |
| ShowGrants                  | mysql         |      29.32 μs |       0.268 μs |      0.096 μs |  0.0305 |      - |     488 B |
| ShowFullTables              | mysql         |     256.93 μs |       4.121 μs |      1.469 μs |       - |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql         |     250.49 μs |       1.560 μs |      0.405 μs |       - |      - |     569 B |
| InfoSchemaTablesScan        | mysql         |   1,641.77 μs |       7.602 μs |      2.711 μs |       - |      - |     522 B |
| UserPrivilegesScan          | mysql         |      90.98 μs |       0.687 μs |      0.245 μs |       - |      - |     600 B |
| CreateGrantDropUser         | mysql         |   1,858.53 μs |      32.289 μs |      8.385 μs |       - |      - |    1993 B |
| **InsertBatch100**              | **mysql-nofsync** |     **967.39 μs** |      **23.367 μs** |      **8.333 μs** | **11.7188** | **0.9766** |   **98313 B** |
| ReplaceNewRow               | mysql-nofsync |      43.57 μs |       1.920 μs |      0.685 μs |  0.1221 |      - |    1296 B |
| PreparedPointSelect         | mysql-nofsync |      33.00 μs |       1.915 μs |      0.683 μs |  0.0610 |      - |     960 B |
| PointSelectAsLimitedUser    | mysql-nofsync |      39.53 μs |       0.122 μs |      0.032 μs |  0.0610 |      - |     880 B |
| SelectCurrentUser           | mysql-nofsync |      28.93 μs |       0.303 μs |      0.108 μs |  0.0305 |      - |     504 B |
| ShowGrants                  | mysql-nofsync |      26.60 μs |      10.585 μs |      3.775 μs |  0.0305 |      - |     488 B |
| ShowFullTables              | mysql-nofsync |     253.60 μs |       2.736 μs |      0.711 μs |       - |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql-nofsync |     241.81 μs |       5.886 μs |      2.099 μs |       - |      - |     568 B |
| InfoSchemaTablesScan        | mysql-nofsync |   1,108.51 μs |      11.495 μs |      4.099 μs |       - |      - |     602 B |
| UserPrivilegesScan          | mysql-nofsync |      83.98 μs |       1.383 μs |      0.359 μs |       - |      - |     600 B |
| CreateGrantDropUser         | mysql-nofsync |   1,608.78 μs |      35.956 μs |     12.822 μs |       - |      - |    1994 B |
| **UpsertExistingByPk**          | **fsdb**          |     **119.16 μs** |       **9.005 μs** |      **3.211 μs** |       **-** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | fsdb          |     339.02 μs |      39.461 μs |     14.072 μs |       - |      - |    2159 B |
| ComputedProjection          | fsdb          |     156.42 μs |       1.289 μs |      0.335 μs |       - |      - |     848 B |
| ViewFilterLimit             | fsdb          |  43,727.48 μs |   5,929.098 μs |  2,114.373 μs |       - |      - |         - |
| ViewAggregate               | fsdb          |  84,073.25 μs |   5,672.245 μs |  2,022.777 μs |       - |      - |         - |
| RecursiveCte100             | fsdb          |     564.24 μs |       9.730 μs |      2.527 μs |       - |      - |     507 B |
| CorrelatedJsonTable         | fsdb          |  18,077.02 μs |   1,409.643 μs |    366.080 μs |       - |      - |     546 B |
| InsertCheckedGenerated      | fsdb          |      80.28 μs |       1.079 μs |      0.280 μs |       - |      - |     704 B |
| InsertWithAfterTrigger      | fsdb          |      96.27 μs |       0.957 μs |      0.248 μs |       - |      - |     704 B |
| **UpsertExistingByPk**          | **fsdb-wal**      |   **1,419.98 μs** |      **44.510 μs** |     **11.559 μs** |       **-** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | fsdb-wal      |   3,009.62 μs |     243.513 μs |     63.239 μs |       - |      - |    2164 B |
| ComputedProjection          | fsdb-wal      |     157.95 μs |       3.881 μs |      1.384 μs |       - |      - |     848 B |
| ViewFilterLimit             | fsdb-wal      |  43,194.85 μs |   3,329.272 μs |  1,187.250 μs |       - |      - |         - |
| ViewAggregate               | fsdb-wal      |  79,479.62 μs |  12,281.330 μs |  4,379.641 μs |       - |      - |         - |
| RecursiveCte100             | fsdb-wal      |     574.12 μs |      13.560 μs |      4.835 μs |       - |      - |     505 B |
| CorrelatedJsonTable         | fsdb-wal      |  18,450.55 μs |   1,770.471 μs |    631.367 μs |       - |      - |     546 B |
| InsertCheckedGenerated      | fsdb-wal      |     121.20 μs |      18.631 μs |      6.644 μs |       - |      - |     704 B |
| InsertWithAfterTrigger      | fsdb-wal      |     141.41 μs |      15.042 μs |      5.364 μs |       - |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**         |     **118.08 μs** |       **2.625 μs** |      **0.936 μs** |  **0.1221** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql         |     216.11 μs |       4.670 μs |      1.665 μs |  0.2441 |      - |    2287 B |
| ComputedProjection          | mysql         |      39.83 μs |       0.535 μs |      0.191 μs |  0.0610 |      - |     848 B |
| ViewFilterLimit             | mysql         |      60.34 μs |       1.079 μs |      0.385 μs |  0.0610 |      - |     536 B |
| ViewAggregate               | mysql         |  20,079.63 μs |     221.439 μs |     78.967 μs |       - |      - |     634 B |
| RecursiveCte100             | mysql         |      54.02 μs |       0.684 μs |      0.244 μs |  0.0610 |      - |     584 B |
| CorrelatedJsonTable         | mysql         |     123.25 μs |       1.111 μs |      0.396 μs |       - |      - |     584 B |
| InsertCheckedGenerated      | mysql         |      93.77 μs |      15.183 μs |      5.415 μs |       - |      - |     704 B |
| InsertWithAfterTrigger      | mysql         |     107.17 μs |       2.045 μs |      0.729 μs |       - |      - |     704 B |
| **UpsertExistingByPk**          | **mysql-nofsync** |      **47.01 μs** |       **1.225 μs** |      **0.437 μs** |  **0.1221** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql-nofsync |     135.73 μs |       5.957 μs |      2.124 μs |  0.2441 |      - |    2286 B |
| ComputedProjection          | mysql-nofsync |      39.37 μs |       0.803 μs |      0.286 μs |  0.0610 |      - |     848 B |
| ViewFilterLimit             | mysql-nofsync |      59.59 μs |       0.595 μs |      0.212 μs |  0.0610 |      - |     536 B |
| ViewAggregate               | mysql-nofsync |  19,868.57 μs |     248.578 μs |     88.645 μs |       - |      - |     634 B |
| RecursiveCte100             | mysql-nofsync |      54.37 μs |       1.753 μs |      0.625 μs |  0.0610 |      - |     584 B |
| CorrelatedJsonTable         | mysql-nofsync |     123.41 μs |       1.401 μs |      0.500 μs |       - |      - |     584 B |
| InsertCheckedGenerated      | mysql-nofsync |      33.65 μs |       1.654 μs |      0.590 μs |  0.0610 |      - |     704 B |
| InsertWithAfterTrigger      | mysql-nofsync |      39.53 μs |       1.108 μs |      0.395 μs |  0.0610 |      - |     720 B |
| **WindowTopOrders**             | **fsdb**          | **348,046.08 μs** |  **53,761.220 μs** | **19,171.769 μs** |       **-** |      **-** |         **-** |
| **WindowTopOrders**             | **fsdb-wal**      | **345,251.33 μs** |  **22,679.606 μs** |  **5,889.822 μs** |       **-** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql**         |  **47,440.72 μs** |   **2,356.924 μs** |    **612.086 μs** |       **-** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql-nofsync** |  **48,141.11 μs** |   **5,481.243 μs** |  **1,954.664 μs** |       **-** |      **-** |         **-** |
| **FullTextNaturalSearch**       | **fsdb**          |  **55,199.70 μs** |   **4,419.400 μs** |  **1,576.001 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch       | fsdb          |  52,632.32 μs |   2,216.307 μs |    790.356 μs |       - |      - |         - |
| FullTextAccentSearch        | fsdb          |  52,637.34 μs |   2,176.855 μs |    776.287 μs |       - |      - |         - |
| FullTextBooleanPrefixSearch | fsdb          |  55,336.17 μs |   7,716.994 μs |  2,004.079 μs |       - |      - |         - |
| **FullTextNaturalSearch**       | **fsdb-wal**      |  **54,626.46 μs** |   **5,327.239 μs** |  **1,899.745 μs** |       **-** |      **-** |         **-** |
| FullTextBooleanSearch       | fsdb-wal      |  51,499.31 μs |   5,180.428 μs |  1,847.390 μs |       - |      - |         - |
| FullTextAccentSearch        | fsdb-wal      |  53,349.15 μs |   2,964.011 μs |  1,056.995 μs |       - |      - |         - |
| FullTextBooleanPrefixSearch | fsdb-wal      |  56,907.59 μs |     959.532 μs |    249.187 μs |       - |      - |         - |
| **FullTextNaturalSearch**       | **mysql**         |     **389.42 μs** |      **32.704 μs** |     **11.663 μs** |       **-** |      **-** |     **504 B** |
| FullTextBooleanSearch       | mysql         |   1,170.16 μs |      12.632 μs |      3.280 μs |       - |      - |     504 B |
| FullTextAccentSearch        | mysql         |     217.00 μs |      24.469 μs |      6.354 μs |       - |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql         |     341.62 μs |      26.066 μs |      9.295 μs |       - |      - |     505 B |
| **FullTextNaturalSearch**       | **mysql-nofsync** |     **420.77 μs** |      **48.398 μs** |     **12.569 μs** |       **-** |      **-** |     **504 B** |
| FullTextBooleanSearch       | mysql-nofsync |   1,136.59 μs |      26.796 μs |      9.556 μs |       - |      - |     506 B |
| FullTextAccentSearch        | mysql-nofsync |     237.27 μs |      32.259 μs |     11.504 μs |       - |      - |     505 B |
| FullTextBooleanPrefixSearch | mysql-nofsync |     327.78 μs |      10.389 μs |      3.705 μs |       - |      - |     505 B |
| **PointSelectByPk**             | **fsdb**          |     **194.85 μs** |       **2.866 μs** |      **0.744 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb          |  13,293.69 μs |     238.225 μs |     84.953 μs |       - |      - |     577 B |
| FilterBySecondaryEquality   | fsdb          |     523.76 μs |      11.063 μs |      3.945 μs |       - |      - |     521 B |
| InsertSingle                | fsdb          |      85.13 μs |       1.975 μs |      0.513 μs |  0.1221 |      - |    1240 B |
| ReplaceExistingByPk         | fsdb          |     100.21 μs |       4.259 μs |      1.519 μs |       - |      - |    1111 B |
| UpdateSingleRow             | fsdb          |     128.21 μs |      12.786 μs |      4.560 μs |       - |      - |     679 B |
| JoinUsersOrders             | fsdb          |   5,082.03 μs |     289.950 μs |    103.399 μs |       - |      - |     819 B |
| UncorrelatedInSubquery      | fsdb          | 189,520.08 μs | 199,042.971 μs | 70,980.641 μs |       - |      - |         - |
| GroupByAggregate            | fsdb          |  92,502.86 μs |  18,693.836 μs |  6,666.402 μs |       - |      - |         - |
| JsonExtract                 | fsdb          |     197.51 μs |       1.314 μs |      0.341 μs |       - |      - |     504 B |
| UpdateByNonIndexed          | fsdb          |   9,414.71 μs |     579.896 μs |    206.797 μs |       - |      - |     741 B |
| **PointSelectByPk**             | **fsdb-wal**      |     **268.14 μs** |     **180.563 μs** |     **64.390 μs** |       **-** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb-wal      |  13,497.67 μs |   1,133.067 μs |    294.254 μs |       - |      - |     577 B |
| FilterBySecondaryEquality   | fsdb-wal      |     574.53 μs |      73.996 μs |     26.388 μs |       - |      - |     520 B |
| InsertSingle                | fsdb-wal      |     142.12 μs |       7.619 μs |      2.717 μs |       - |      - |    1240 B |
| ReplaceExistingByPk         | fsdb-wal      |   1,133.07 μs |     188.590 μs |     67.253 μs |       - |      - |    1114 B |
| UpdateSingleRow             | fsdb-wal      |   1,490.65 μs |      66.249 μs |     17.205 μs |       - |      - |     679 B |
| JoinUsersOrders             | fsdb-wal      |   5,005.04 μs |     435.246 μs |    155.213 μs |       - |      - |     819 B |
| UncorrelatedInSubquery      | fsdb-wal      | 170,258.85 μs |  43,688.818 μs | 15,579.853 μs |       - |      - |         - |
| GroupByAggregate            | fsdb-wal      | 107,231.87 μs |  38,232.164 μs |  9,928.772 μs |       - |      - |         - |
| JsonExtract                 | fsdb-wal      |     203.35 μs |       1.630 μs |      0.423 μs |       - |      - |     504 B |
| UpdateByNonIndexed          | fsdb-wal      |  11,408.69 μs |     521.468 μs |    185.961 μs |       - |      - |     741 B |
| **PointSelectByPk**             | **mysql**         |      **40.88 μs** |       **2.883 μs** |      **1.028 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit        | mysql         |   1,961.77 μs |      23.919 μs |      8.530 μs |       - |      - |     644 B |
| FilterBySecondaryEquality   | mysql         |     175.55 μs |       1.409 μs |      0.502 μs |       - |      - |     520 B |
| InsertSingle                | mysql         |     113.76 μs |      24.380 μs |      6.331 μs |  0.1221 |      - |    1240 B |
| ReplaceExistingByPk         | mysql         |     121.81 μs |       3.836 μs |      0.996 μs |       - |      - |    1111 B |
| UpdateSingleRow             | mysql         |     115.99 μs |       5.795 μs |      2.066 μs |       - |      - |     743 B |
| JoinUsersOrders             | mysql         |     181.62 μs |       3.268 μs |      1.165 μs |       - |      - |     808 B |
| UncorrelatedInSubquery      | mysql         |     156.63 μs |       3.582 μs |      1.277 μs |       - |      - |     584 B |
| GroupByAggregate            | mysql         |  21,025.49 μs |   1,366.490 μs |    487.303 μs |       - |      - |     634 B |
| JsonExtract                 | mysql         |     109.91 μs |      28.165 μs |     10.044 μs |       - |      - |     584 B |
| UpdateByNonIndexed          | mysql         |   3,721.41 μs |     408.344 μs |    145.619 μs |       - |      - |     788 B |
| **PointSelectByPk**             | **mysql-nofsync** |      **39.48 μs** |       **0.518 μs** |      **0.134 μs** |  **0.0610** |      **-** |     **880 B** |
| FilterScanOrderLimit        | mysql-nofsync |   1,974.01 μs |      65.197 μs |     23.250 μs |       - |      - |     642 B |
| FilterBySecondaryEquality   | mysql-nofsync |     169.05 μs |       4.362 μs |      1.133 μs |       - |      - |     520 B |
| InsertSingle                | mysql-nofsync |      38.23 μs |      10.964 μs |      3.910 μs |  0.1221 |      - |    1264 B |
| ReplaceExistingByPk         | mysql-nofsync |      52.03 μs |       3.257 μs |      1.161 μs |  0.1221 |      - |    1111 B |
| UpdateSingleRow             | mysql-nofsync |      44.29 μs |       6.429 μs |      2.293 μs |  0.0610 |      - |     743 B |
| JoinUsersOrders             | mysql-nofsync |     186.64 μs |      17.759 μs |      6.333 μs |       - |      - |     808 B |
| UncorrelatedInSubquery      | mysql-nofsync |     161.29 μs |       8.243 μs |      2.940 μs |       - |      - |     584 B |
| GroupByAggregate            | mysql-nofsync |  20,607.64 μs |     175.149 μs |     45.486 μs |       - |      - |     634 B |
| JsonExtract                 | mysql-nofsync |      59.65 μs |       7.223 μs |      2.576 μs |  0.0610 |      - |     584 B |
| UpdateByNonIndexed          | mysql-nofsync |   3,545.13 μs |     102.566 μs |     36.576 μs |       - |      - |     788 B |
| **OrderBySecondaryRange**       | **fsdb**          |     **173.80 μs** |       **5.519 μs** |      **1.968 μs** |       **-** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **fsdb-wal**      |     **177.30 μs** |      **14.049 μs** |      **3.649 μs** |       **-** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**         |      **47.48 μs** |       **7.108 μs** |      **1.846 μs** |  **0.0610** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql-nofsync** |      **50.92 μs** |       **1.227 μs** |      **0.319 μs** |  **0.0610** |      **-** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**          |     **136.12 μs** |       **2.766 μs** |      **0.986 μs** |       **-** |      **-** |     **872 B** |
| **FilterBySecondaryRange**      | **fsdb-wal**      |     **137.64 μs** |      **16.144 μs** |      **5.757 μs** |       **-** |      **-** |     **872 B** |
| **FilterBySecondaryRange**      | **mysql**         |      **43.65 μs** |       **3.961 μs** |      **1.029 μs** |  **0.0610** |      **-** |     **872 B** |
| **FilterBySecondaryRange**      | **mysql-nofsync** |      **48.40 μs** |      **35.107 μs** |      **9.117 μs** |  **0.0610** |      **-** |     **872 B** |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-CAVYJV : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

InvocationCount=32  IterationCount=6  UnrollFactor=1
WarmupCount=3

```
| Method                   | Target        | Mean     | Error    | StdDev   | Allocated |
|------------------------- |-------------- |---------:|---------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**          | **297.3 μs** | **31.02 μs** | **11.06 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **fsdb-wal**      | **297.1 μs** | **54.41 μs** | **19.40 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **mysql**         | **225.3 μs** | **22.05 μs** |  **5.73 μs** |  **33.62 KB** |
| **ConnectAuthenticateClose** | **mysql-nofsync** | **226.2 μs** | **15.82 μs** |  **4.11 μs** |  **33.63 KB** |
