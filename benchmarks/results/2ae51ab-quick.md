<!--
sha: 2ae51ab
date: 2026-08-31T22:41:56Z
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
| Method                      | Target | Mean          | Error          | StdDev       | Gen0   | Allocated |
|---------------------------- |------- |--------------:|---------------:|-------------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**   |   **6,424.44 μs** |     **817.800 μs** |    **44.826 μs** | **7.8125** |   **96659 B** |
| ReplaceNewRow               | fsdb   |     260.28 μs |      22.265 μs |     1.220 μs |      - |    1281 B |
| PreparedPointSelect         | fsdb   |     116.04 μs |      23.211 μs |     1.272 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     217.10 μs |     130.598 μs |     7.159 μs |      - |     881 B |
| SelectCurrentUser           | fsdb   |      99.81 μs |      13.456 μs |     0.738 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      79.40 μs |      11.363 μs |     0.623 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      83.00 μs |      19.005 μs |     1.042 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |   1,631.15 μs |     459.957 μs |    25.212 μs |      - |     571 B |
| InfoSchemaTablesScan        | fsdb   |     233.51 μs |      19.077 μs |     1.046 μs |      - |     521 B |
| UserPrivilegesScan          | fsdb   |     210.49 μs |      44.379 μs |     2.433 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |     432.49 μs |     214.238 μs |    11.743 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |   **1,857.51 μs** |  **12,425.204 μs** |   **681.067 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     110.73 μs |      58.072 μs |     3.183 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | mysql  |      34.27 μs |       7.490 μs |     0.411 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      40.24 μs |       7.583 μs |     0.416 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      29.54 μs |       5.204 μs |     0.285 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |      26.44 μs |      21.522 μs |     1.180 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |     232.72 μs |     240.582 μs |    13.187 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |     213.12 μs |      22.066 μs |     1.210 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   1,192.19 μs |     336.495 μs |    18.444 μs |      - |     602 B |
| UserPrivilegesScan          | mysql  |      88.87 μs |      32.226 μs |     1.766 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   1,899.23 μs |     335.365 μs |    18.382 μs |      - |    1996 B |
| **ConcurrentPointUpdateBurst**  | **fsdb**   |   **2,854.18 μs** |     **627.726 μs** |    **34.408 μs** | **3.9063** |   **39988 B** |
| ConcurrentInsertBurst       | fsdb   |   3,969.85 μs |  10,488.364 μs |   574.903 μs | 3.9063 |   47637 B |
| **ConcurrentPointUpdateBurst**  | **mysql**  |     **877.14 μs** |   **2,700.049 μs** |   **147.999 μs** | **3.9063** |   **41010 B** |
| ConcurrentInsertBurst       | mysql  |     852.11 μs |   1,718.780 μs |    94.212 μs | 4.8828 |   48017 B |
| **UpsertExistingByPk**          | **fsdb**   |     **295.56 μs** |     **211.889 μs** |    **11.614 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |     701.44 μs |   1,167.545 μs |    63.997 μs |      - |    2160 B |
| ComputedProjection          | fsdb   |     201.10 μs |     141.342 μs |     7.747 μs |      - |     849 B |
| ViewFilterLimit             | fsdb   |     387.81 μs |      40.329 μs |     2.211 μs |      - |     537 B |
| ViewAggregate               | fsdb   |  39,077.07 μs |   5,821.589 μs |   319.101 μs |      - |     617 B |
| RecursiveCte100             | fsdb   |     888.50 μs |     139.152 μs |     7.627 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |     422.22 μs |      48.643 μs |     2.666 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |     200.25 μs |      54.013 μs |     2.961 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |     372.94 μs |      19.672 μs |     1.078 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **128.12 μs** |      **28.562 μs** |     **1.566 μs** | **0.1221** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     219.40 μs |      22.079 μs |     1.210 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      40.08 μs |       9.449 μs |     0.518 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      59.57 μs |       0.570 μs |     0.031 μs |      - |     536 B |
| ViewAggregate               | mysql  |  20,386.07 μs |   6,596.120 μs |   361.556 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      53.81 μs |      19.613 μs |     1.075 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     125.07 μs |      40.186 μs |     2.203 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |     101.65 μs |      63.401 μs |     3.475 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |     116.34 μs |      68.435 μs |     3.751 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **140,339.65 μs** | **115,638.392 μs** | **6,338.531 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   | 101,674.68 μs |  77,584.730 μs | 4,252.681 μs |      - |         - |
| **WindowTopOrders**             | **mysql**  |  **48,293.51 μs** |  **15,809.297 μs** |   **866.561 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  |  18,663.24 μs |  10,370.849 μs |   568.461 μs |      - |     538 B |
| **FullTextNaturalSearch**       | **fsdb**   |   **1,324.78 μs** |      **66.985 μs** |     **3.672 μs** |      **-** |     **507 B** |
| FullTextBooleanSearch       | fsdb   |   1,546.49 μs |     230.519 μs |    12.636 μs |      - |     507 B |
| FullTextAccentSearch        | fsdb   |     851.04 μs |     116.621 μs |     6.392 μs |      - |     505 B |
| FullTextBooleanPrefixSearch | fsdb   |     534.08 μs |      60.449 μs |     3.313 μs |      - |     505 B |
| FullTextJoinUsers           | fsdb   |   2,428.65 μs |     644.568 μs |    35.331 μs |      - |     525 B |
| **FullTextNaturalSearch**       | **mysql**  |     **399.90 μs** |      **36.777 μs** |     **2.016 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,103.89 μs |      35.502 μs |     1.946 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |     213.93 μs |     131.654 μs |     7.216 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql  |     326.99 μs |      51.501 μs |     2.823 μs |      - |     504 B |
| FullTextJoinUsers           | mysql  |     417.36 μs |      69.395 μs |     3.804 μs |      - |     521 B |
| **PointSelectByPk**             | **fsdb**   |     **201.63 μs** |      **40.350 μs** |     **2.212 μs** |      **-** |     **881 B** |
| FilterScanOrderLimit        | fsdb   |   8,927.82 μs |     673.925 μs |    36.940 μs |      - |     581 B |
| FilterBySecondaryEquality   | fsdb   |     391.50 μs |     226.549 μs |    12.418 μs |      - |     521 B |
| InsertSingle                | fsdb   |     287.73 μs |     613.758 μs |    33.642 μs |      - |    1241 B |
| ReplaceExistingByPk         | fsdb   |     225.99 μs |     212.523 μs |    11.649 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |     269.50 μs |     151.143 μs |     8.285 μs |      - |     680 B |
| JoinUsersOrders             | fsdb   |     307.56 μs |      58.110 μs |     3.185 μs |      - |     809 B |
| UncorrelatedInSubquery      | fsdb   |     550.89 μs |     132.853 μs |     7.282 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |  34,193.45 μs |  18,281.709 μs | 1,002.082 μs |      - |     562 B |
| JsonExtract                 | fsdb   |     725.87 μs |      28.744 μs |     1.576 μs |      - |     505 B |
| UpdateByNonIndexed          | fsdb   |  11,186.01 μs |   2,878.613 μs |   157.786 μs |      - |     741 B |
| **PointSelectByPk**             | **mysql**  |      **39.99 μs** |       **7.947 μs** |     **0.436 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,955.38 μs |     395.419 μs |    21.674 μs |      - |     642 B |
| FilterBySecondaryEquality   | mysql  |     178.96 μs |      58.783 μs |     3.222 μs |      - |     520 B |
| InsertSingle                | mysql  |     115.14 μs |      62.124 μs |     3.405 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     140.07 μs |       4.461 μs |     0.245 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     115.75 μs |      43.352 μs |     2.376 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     184.32 μs |      50.124 μs |     2.747 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     157.57 μs |      10.666 μs |     0.585 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,183.46 μs |     424.993 μs |    23.295 μs |      - |     634 B |
| JsonExtract                 | mysql  |      63.58 μs |       9.982 μs |     0.547 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |   3,590.10 μs |     558.403 μs |    30.608 μs |      - |     788 B |
| **FilterByPrimaryKeyList**      | **fsdb**   |     **251.22 μs** |     **132.396 μs** |     **7.257 μs** |      **-** |    **1694 B** |
| **FilterByPrimaryKeyList**      | **mysql**  |      **49.53 μs** |       **5.387 μs** |     **0.295 μs** | **0.1831** |    **1694 B** |
| **LeftJoinUsersOrders**         | **fsdb**   |     **268.59 μs** |      **26.841 μs** |     **1.471 μs** |      **-** |     **825 B** |
| RightJoinUsersOrders        | fsdb   |     325.74 μs |      81.318 μs |     4.457 μs |      - |     817 B |
| ReorderedIndexedJoin        | fsdb   |     304.19 μs |      74.119 μs |     4.063 μs |      - |     817 B |
| IndexedStringInSubquery     | fsdb   |     745.06 μs |      23.802 μs |     1.305 μs |      - |     505 B |
| DecimalInSubquery           | fsdb   |  18,304.80 μs |   2,747.139 μs |   150.580 μs |      - |     530 B |
| CompositeInSubquery         | fsdb   |   1,075.34 μs |     120.496 μs |     6.605 μs |      - |     491 B |
| QuantifiedMembership        | fsdb   |   3,514.14 μs |   3,223.008 μs |   176.664 μs |      - |     493 B |
| CorrelatedOrderCount        | fsdb   |   2,124.84 μs |     455.270 μs |    24.955 μs |      - |     509 B |
| **LeftJoinUsersOrders**         | **mysql**  |     **179.40 μs** |      **27.547 μs** |     **1.510 μs** |      **-** |     **824 B** |
| RightJoinUsersOrders        | mysql  |     230.88 μs |     107.182 μs |     5.875 μs |      - |     897 B |
| ReorderedIndexedJoin        | mysql  |     122.78 μs |       2.664 μs |     0.146 μs |      - |     816 B |
| IndexedStringInSubquery     | mysql  |     323.17 μs |     427.030 μs |    23.407 μs |      - |     505 B |
| DecimalInSubquery           | mysql  |   8,292.62 μs |   2,150.675 μs |   117.886 μs |      - |     585 B |
| CompositeInSubquery         | mysql  |     301.75 μs |     468.245 μs |    25.666 μs |      - |     569 B |
| QuantifiedMembership        | mysql  |   6,299.47 μs |     264.212 μs |    14.482 μs |      - |     576 B |
| CorrelatedOrderCount        | mysql  |     207.63 μs |      43.032 μs |     2.359 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **217.17 μs** |      **83.396 μs** |     **4.571 μs** |      **-** |    **1001 B** |
| **OrderBySecondaryRange**       | **mysql**  |      **50.33 μs** |      **12.546 μs** |     **0.688 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **149.01 μs** |      **56.403 μs** |     **3.092 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |     318.44 μs |     145.567 μs |     7.979 μs |      - |     848 B |
| **FilterBySecondaryRange**      | **mysql**  |      **44.30 μs** |      **16.796 μs** |     **0.921 μs** | **0.0610** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |     113.65 μs |      40.054 μs |     2.195 μs |      - |     911 B |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=32  IterationCount=3
LaunchCount=1  UnrollFactor=1  WarmupCount=3

```
| Method                   | Target | Mean     | Error       | StdDev   | Allocated |
|------------------------- |------- |---------:|------------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**   | **306.0 μs** | **1,017.29 μs** | **55.76 μs** |  **32.31 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **214.9 μs** |    **28.20 μs** |  **1.55 μs** |  **33.63 KB** |
