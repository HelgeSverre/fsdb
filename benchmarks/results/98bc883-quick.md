<!--
sha: 98bc883
date: 2026-09-01T08:26:39Z
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
| Method                      | Target | Mean         | Error          | StdDev        | Median       | Gen0   | Allocated |
|---------------------------- |------- |-------------:|---------------:|--------------:|-------------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**   |  **6,737.58 μs** |     **146.427 μs** |      **8.026 μs** |  **6,740.21 μs** | **7.8125** |   **96659 B** |
| ReplaceNewRow               | fsdb   |    294.65 μs |      93.309 μs |      5.115 μs |    292.02 μs |      - |    1281 B |
| PreparedPointSelect         | fsdb   |    135.19 μs |     119.627 μs |      6.557 μs |    131.42 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |    220.07 μs |      62.442 μs |      3.423 μs |    219.17 μs |      - |     881 B |
| SelectCurrentUser           | fsdb   |    112.95 μs |      99.762 μs |      5.468 μs |    110.41 μs |      - |     504 B |
| ShowGrants                  | fsdb   |     86.27 μs |       4.457 μs |      0.244 μs |     86.36 μs |      - |     488 B |
| ShowFullTables              | fsdb   |     99.78 μs |     165.950 μs |      9.096 μs |    102.55 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |    217.24 μs |      69.553 μs |      3.812 μs |    219.22 μs |      - |     568 B |
| InfoSchemaTablesScan        | fsdb   |    205.23 μs |      18.741 μs |      1.027 μs |    205.50 μs |      - |     520 B |
| UserPrivilegesScan          | fsdb   |    209.05 μs |       8.994 μs |      0.493 μs |    208.93 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |    426.45 μs |     117.434 μs |      6.437 μs |    430.16 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |  **1,697.76 μs** |   **1,210.733 μs** |     **66.364 μs** |  **1,702.34 μs** | **7.8125** |   **96716 B** |
| ReplaceNewRow               | mysql  |    122.23 μs |      15.036 μs |      0.824 μs |    121.82 μs |      - |    1280 B |
| PreparedPointSelect         | mysql  |     34.20 μs |       2.665 μs |      0.146 μs |     34.22 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |     40.61 μs |       0.628 μs |      0.034 μs |     40.63 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |     28.70 μs |      21.619 μs |      1.185 μs |     28.28 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |     30.11 μs |       1.780 μs |      0.098 μs |     30.13 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |    262.45 μs |     151.229 μs |      8.289 μs |    258.58 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |    248.02 μs |      23.800 μs |      1.305 μs |    247.27 μs |      - |     568 B |
| InfoSchemaTablesScan        | mysql  |  1,175.18 μs |     328.638 μs |     18.014 μs |  1,165.67 μs |      - |     602 B |
| UserPrivilegesScan          | mysql  |     88.85 μs |       2.817 μs |      0.154 μs |     88.91 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |  1,961.68 μs |     685.513 μs |     37.575 μs |  1,940.74 μs |      - |    1996 B |
| **ConcurrentPointUpdateBurst**  | **fsdb**   |  **2,936.09 μs** |     **417.589 μs** |     **22.889 μs** |  **2,932.94 μs** | **3.9063** |   **39989 B** |
| ConcurrentInsertBurst       | fsdb   |  3,608.42 μs |     992.723 μs |     54.415 μs |  3,603.23 μs | 3.9063 |   47637 B |
| **ConcurrentPointUpdateBurst**  | **mysql**  |  **1,105.88 μs** |   **1,387.830 μs** |     **76.072 μs** |  **1,071.83 μs** | **4.8828** |   **41009 B** |
| ConcurrentInsertBurst       | mysql  |  1,145.03 μs |  12,562.932 μs |    688.617 μs |    762.84 μs | 3.9063 |   47634 B |
| **UpsertExistingByPk**          | **fsdb**   |    **303.07 μs** |     **150.660 μs** |      **8.258 μs** |    **304.37 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |    792.04 μs |     281.555 μs |     15.433 μs |    790.81 μs |      - |    2159 B |
| ComputedProjection          | fsdb   |    217.85 μs |     580.242 μs |     31.805 μs |    201.33 μs |      - |     849 B |
| ViewFilterLimit             | fsdb   |    417.69 μs |     682.336 μs |     37.401 μs |    432.22 μs |      - |     537 B |
| ViewAggregate               | fsdb   | 28,909.03 μs |  28,065.108 μs |  1,538.343 μs | 29,479.71 μs |      - |     562 B |
| RecursiveCte100             | fsdb   |    953.03 μs |     586.035 μs |     32.123 μs |    949.14 μs |      - |     504 B |
| CorrelatedJsonTable         | fsdb   |    421.57 μs |      42.431 μs |      2.326 μs |    422.85 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |    203.30 μs |     234.244 μs |     12.840 μs |    198.32 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |    378.51 μs |     216.624 μs |     11.874 μs |    372.16 μs |      - |     705 B |
| **UpsertExistingByPk**          | **mysql**  |    **125.98 μs** |     **182.319 μs** |      **9.994 μs** |    **121.16 μs** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |    238.66 μs |      41.436 μs |      2.271 μs |    238.31 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |     41.52 μs |      30.724 μs |      1.684 μs |     40.64 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |     60.53 μs |      13.818 μs |      0.757 μs |     60.89 μs | 0.0610 |     536 B |
| ViewAggregate               | mysql  | 20,180.15 μs |   1,604.679 μs |     87.958 μs | 20,215.70 μs |      - |     634 B |
| RecursiveCte100             | mysql  |     54.37 μs |       1.993 μs |      0.109 μs |     54.39 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |    129.04 μs |      62.039 μs |      3.401 μs |    127.11 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |    115.35 μs |     283.115 μs |     15.518 μs |    114.57 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |    120.37 μs |      53.403 μs |      2.927 μs |    119.94 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **85,185.71 μs** |  **47,018.289 μs** |  **2,577.231 μs** | **84,194.30 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   | 11,329.51 μs |   7,814.676 μs |    428.349 μs | 11,294.50 μs |      - |     525 B |
| **WindowTopOrders**             | **mysql**  | **98,877.50 μs** | **343,329.201 μs** | **18,819.032 μs** | **94,101.75 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  | 26,154.36 μs |  20,801.360 μs |  1,140.193 μs | 26,618.24 μs |      - |     528 B |
| **FullTextNaturalSearch**       | **fsdb**   |  **1,059.75 μs** |     **994.837 μs** |     **54.530 μs** |  **1,057.16 μs** |      **-** |     **504 B** |
| FullTextBooleanSearch       | fsdb   |  1,525.78 μs |      82.790 μs |      4.538 μs |  1,525.18 μs |      - |     507 B |
| FullTextAccentSearch        | fsdb   |    539.46 μs |     140.143 μs |      7.682 μs |    539.48 μs |      - |     505 B |
| FullTextBooleanPrefixSearch | fsdb   |    575.67 μs |     510.323 μs |     27.973 μs |    560.40 μs |      - |     505 B |
| FullTextJoinUsers           | fsdb   |  2,006.44 μs |   1,617.379 μs |     88.654 μs |  1,972.26 μs |      - |     523 B |
| **FullTextNaturalSearch**       | **mysql**  |    **429.30 μs** |     **321.811 μs** |     **17.640 μs** |    **419.13 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |  1,142.65 μs |      69.364 μs |      3.802 μs |  1,140.79 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |    241.74 μs |      18.919 μs |      1.037 μs |    241.99 μs |      - |     505 B |
| FullTextBooleanPrefixSearch | mysql  |    320.08 μs |      40.565 μs |      2.223 μs |    318.83 μs |      - |     505 B |
| FullTextJoinUsers           | mysql  |    430.39 μs |      47.405 μs |      2.598 μs |    430.58 μs |      - |     521 B |
| **PointSelectByPk**             | **fsdb**   |    **237.27 μs** |     **443.001 μs** |     **24.282 μs** |    **227.02 μs** |      **-** |     **881 B** |
| FilterScanOrderLimit        | fsdb   |  5,972.41 μs |   5,985.493 μs |    328.085 μs |  5,818.68 μs |      - |     571 B |
| FilterBySecondaryEquality   | fsdb   |    443.58 μs |     441.319 μs |     24.190 μs |    431.22 μs |      - |     521 B |
| InsertSingle                | fsdb   |    298.96 μs |     186.856 μs |     10.242 μs |    295.11 μs |      - |    1241 B |
| ReplaceExistingByPk         | fsdb   |    241.26 μs |     252.506 μs |     13.841 μs |    236.48 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |    291.52 μs |      92.586 μs |      5.075 μs |    289.16 μs |      - |     680 B |
| JoinUsersOrders             | fsdb   |    337.70 μs |     226.595 μs |     12.420 μs |    333.75 μs |      - |     808 B |
| UncorrelatedInSubquery      | fsdb   |    498.81 μs |      61.938 μs |      3.395 μs |    500.57 μs |      - |     505 B |
| GroupByAggregate            | fsdb   | 27,188.03 μs |  27,130.192 μs |  1,487.097 μs | 27,832.89 μs |      - |     562 B |
| JsonExtract                 | fsdb   |    200.43 μs |      41.608 μs |      2.281 μs |    201.69 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |  7,982.78 μs |  26,548.672 μs |  1,455.222 μs |  7,158.93 μs |      - |     731 B |
| **PointSelectByPk**             | **mysql**  |     **40.39 μs** |       **6.097 μs** |      **0.334 μs** |     **40.44 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |  1,956.44 μs |     116.707 μs |      6.397 μs |  1,952.95 μs |      - |     640 B |
| FilterBySecondaryEquality   | mysql  |    178.77 μs |       8.488 μs |      0.465 μs |    179.02 μs |      - |     520 B |
| InsertSingle                | mysql  |    124.22 μs |      19.900 μs |      1.091 μs |    123.63 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |    151.66 μs |      38.103 μs |      2.089 μs |    152.56 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |    130.47 μs |      18.663 μs |      1.023 μs |    130.50 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |    184.01 μs |      10.672 μs |      0.585 μs |    183.93 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |    161.65 μs |       7.973 μs |      0.437 μs |    161.77 μs |      - |     584 B |
| GroupByAggregate            | mysql  | 20,110.48 μs |   1,139.162 μs |     62.441 μs | 20,124.46 μs |      - |     634 B |
| JsonExtract                 | mysql  |     64.24 μs |       7.816 μs |      0.428 μs |     64.49 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |  3,630.94 μs |     103.459 μs |      5.671 μs |  3,629.31 μs |      - |     788 B |
| **FilterByPrimaryKeyList**      | **fsdb**   |    **263.13 μs** |     **146.876 μs** |      **8.051 μs** |    **262.97 μs** |      **-** |    **1694 B** |
| **FilterByPrimaryKeyList**      | **mysql**  |     **50.66 μs** |       **2.970 μs** |      **0.163 μs** |     **50.67 μs** | **0.1831** |    **1694 B** |
| **LeftJoinUsersOrders**         | **fsdb**   |    **281.99 μs** |      **38.367 μs** |      **2.103 μs** |    **281.58 μs** |      **-** |     **825 B** |
| RightJoinUsersOrders        | fsdb   |    341.58 μs |      18.822 μs |      1.032 μs |    341.61 μs |      - |     817 B |
| ReorderedIndexedJoin        | fsdb   |    323.51 μs |      22.327 μs |      1.224 μs |    323.78 μs |      - |     817 B |
| IndexedStringInSubquery     | fsdb   |    714.74 μs |     118.368 μs |      6.488 μs |    711.42 μs |      - |     504 B |
| DecimalInSubquery           | fsdb   | 11,738.55 μs |   2,682.384 μs |    147.031 μs | 11,725.02 μs |      - |     500 B |
| CompositeInSubquery         | fsdb   |  1,110.50 μs |     414.501 μs |     22.720 μs |  1,122.20 μs |      - |     490 B |
| QuantifiedMembership        | fsdb   |  2,643.96 μs |   2,649.201 μs |    145.212 μs |  2,570.20 μs |      - |     492 B |
| CorrelatedOrderCount        | fsdb   |  1,224.90 μs |     104.528 μs |      5.730 μs |  1,223.24 μs |      - |     507 B |
| **LeftJoinUsersOrders**         | **mysql**  |    **184.22 μs** |      **18.254 μs** |      **1.001 μs** |    **184.11 μs** |      **-** |     **824 B** |
| RightJoinUsersOrders        | mysql  |    269.80 μs |      12.533 μs |      0.687 μs |    269.47 μs |      - |     897 B |
| ReorderedIndexedJoin        | mysql  |    123.93 μs |       3.474 μs |      0.190 μs |    123.96 μs |      - |     816 B |
| IndexedStringInSubquery     | mysql  |    338.77 μs |      43.620 μs |      2.391 μs |    339.97 μs |      - |     505 B |
| DecimalInSubquery           | mysql  |  8,206.00 μs |   1,647.455 μs |     90.303 μs |  8,155.45 μs |      - |     569 B |
| CompositeInSubquery         | mysql  |    318.82 μs |      16.465 μs |      0.903 μs |    318.85 μs |      - |     569 B |
| QuantifiedMembership        | mysql  |  6,348.54 μs |     352.697 μs |     19.333 μs |  6,339.00 μs |      - |     576 B |
| CorrelatedOrderCount        | mysql  |    207.16 μs |       8.383 μs |      0.460 μs |    207.14 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |    **184.23 μs** |     **143.628 μs** |      **7.873 μs** |    **183.43 μs** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**  |     **50.69 μs** |       **3.454 μs** |      **0.189 μs** |     **50.64 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |    **143.00 μs** |     **119.986 μs** |      **6.577 μs** |    **140.88 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |    326.37 μs |      24.960 μs |      1.368 μs |    326.01 μs |      - |     848 B |
| **FilterBySecondaryRange**      | **mysql**  |     **44.23 μs** |       **1.618 μs** |      **0.089 μs** |     **44.19 μs** | **0.0610** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |    132.31 μs |     131.862 μs |      7.228 μs |    130.80 μs |      - |     911 B |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=32  IterationCount=3
LaunchCount=1  UnrollFactor=1  WarmupCount=3

```
| Method                   | Target | Mean     | Error    | StdDev   | Allocated |
|------------------------- |------- |---------:|---------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**   | **308.3 μs** | **696.2 μs** | **38.16 μs** |  **32.31 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **223.3 μs** | **320.8 μs** | **17.58 μs** |  **33.63 KB** |
