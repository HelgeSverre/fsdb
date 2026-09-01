<!--
sha: 55cb607
date: 2026-08-31T19:24:47Z
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
| Method                      | Target | Mean          | Error          | StdDev        | Median        | Gen0   | Allocated |
|---------------------------- |------- |--------------:|---------------:|--------------:|--------------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**   |   **6,623.61 μs** |   **1,609.186 μs** |     **88.205 μs** |   **6,623.59 μs** | **7.8125** |   **96659 B** |
| ReplaceNewRow               | fsdb   |     268.60 μs |      77.629 μs |      4.255 μs |     269.57 μs |      - |    1281 B |
| PreparedPointSelect         | fsdb   |     132.00 μs |      11.848 μs |      0.649 μs |     131.64 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     235.82 μs |     104.532 μs |      5.730 μs |     233.58 μs |      - |     881 B |
| SelectCurrentUser           | fsdb   |     108.99 μs |       9.499 μs |      0.521 μs |     109.17 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      79.66 μs |       3.297 μs |      0.181 μs |      79.66 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      84.21 μs |      19.498 μs |      1.069 μs |      83.83 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |   1,699.08 μs |      82.929 μs |      4.546 μs |   1,699.61 μs |      - |     571 B |
| InfoSchemaTablesScan        | fsdb   |     278.90 μs |      37.755 μs |      2.069 μs |     279.05 μs |      - |     521 B |
| UserPrivilegesScan          | fsdb   |     375.01 μs |      43.716 μs |      2.396 μs |     373.77 μs |      - |     521 B |
| CreateGrantDropUser         | fsdb   |     618.07 μs |     959.781 μs |     52.609 μs |     641.39 μs |      - |    1992 B |
| **InsertBatch100**              | **mysql**  |   **2,218.22 μs** |   **4,583.567 μs** |    **251.241 μs** |   **2,230.46 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     113.63 μs |      85.631 μs |      4.694 μs |     111.54 μs |      - |    1280 B |
| PreparedPointSelect         | mysql  |      33.70 μs |      16.659 μs |      0.913 μs |      34.12 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      40.23 μs |      10.187 μs |      0.558 μs |      40.55 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      29.69 μs |       9.810 μs |      0.538 μs |      29.76 μs |      - |     504 B |
| ShowGrants                  | mysql  |      30.02 μs |       2.796 μs |      0.153 μs |      30.07 μs |      - |     488 B |
| ShowFullTables              | mysql  |     260.34 μs |      80.792 μs |      4.429 μs |     261.59 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | mysql  |     260.77 μs |      26.246 μs |      1.439 μs |     261.23 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   1,321.74 μs |     155.922 μs |      8.547 μs |   1,322.07 μs |      - |     522 B |
| UserPrivilegesScan          | mysql  |      87.32 μs |      13.737 μs |      0.753 μs |      87.71 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   1,919.18 μs |      56.016 μs |      3.070 μs |   1,917.56 μs |      - |    1994 B |
| **ConcurrentPointUpdateBurst**  | **fsdb**   |   **2,992.57 μs** |     **223.768 μs** |     **12.265 μs** |   **2,987.34 μs** | **3.9063** |   **39989 B** |
| ConcurrentInsertBurst       | fsdb   |            NA |             NA |            NA |            NA |     NA |        NA |
| **ConcurrentPointUpdateBurst**  | **mysql**  |     **651.23 μs** |   **1,799.870 μs** |     **98.657 μs** |     **595.25 μs** | **4.8828** |   **41009 B** |
| ConcurrentInsertBurst       | mysql  |     681.84 μs |   1,657.246 μs |     90.839 μs |     666.82 μs | 4.8828 |   48017 B |
| **UpsertExistingByPk**          | **fsdb**   |     **326.31 μs** |     **165.069 μs** |      **9.048 μs** |     **327.65 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |     775.83 μs |     140.008 μs |      7.674 μs |     778.69 μs |      - |    2160 B |
| ComputedProjection          | fsdb   |     208.68 μs |     183.928 μs |     10.082 μs |     210.04 μs |      - |     849 B |
| ViewFilterLimit             | fsdb   |     481.00 μs |      32.700 μs |      1.792 μs |     481.01 μs |      - |     537 B |
| ViewAggregate               | fsdb   |  56,297.18 μs |  68,048.915 μs |  3,729.991 μs |  58,334.91 μs |      - |         - |
| RecursiveCte100             | fsdb   |     880.66 μs |      71.293 μs |      3.908 μs |     881.65 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |     481.00 μs |      13.904 μs |      0.762 μs |     481.15 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |     198.46 μs |      48.934 μs |      2.682 μs |     198.61 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |     377.47 μs |      22.066 μs |      1.209 μs |     377.91 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **130.60 μs** |      **73.658 μs** |      **4.037 μs** |     **128.79 μs** | **0.1221** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     217.53 μs |      16.982 μs |      0.931 μs |     217.49 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      40.19 μs |       2.210 μs |      0.121 μs |      40.13 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      62.66 μs |      10.165 μs |      0.557 μs |      62.44 μs |      - |     536 B |
| ViewAggregate               | mysql  |  20,308.56 μs |     238.485 μs |     13.072 μs |  20,304.40 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      53.15 μs |       4.607 μs |      0.253 μs |      53.16 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     121.20 μs |       2.683 μs |      0.147 μs |     121.15 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |      94.89 μs |      58.563 μs |      3.210 μs |      96.38 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |      98.17 μs |      40.798 μs |      2.236 μs |      98.70 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **324,756.21 μs** |  **36,317.099 μs** |  **1,990.663 μs** | **323,974.69 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   | 144,916.17 μs | 344,028.350 μs | 18,857.355 μs | 144,452.46 μs |      - |         - |
| **WindowTopOrders**             | **mysql**  |  **65,974.86 μs** |  **64,685.288 μs** |  **3,545.619 μs** |  **64,800.12 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  |  22,366.25 μs |   4,846.461 μs |    265.651 μs |  22,242.36 μs |      - |     538 B |
| **FullTextNaturalSearch**       | **fsdb**   |   **2,850.08 μs** |     **222.352 μs** |     **12.188 μs** |   **2,849.37 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | fsdb   |   2,227.78 μs |     543.608 μs |     29.797 μs |   2,219.55 μs |      - |     509 B |
| FullTextAccentSearch        | fsdb   |   1,427.74 μs |     127.642 μs |      6.996 μs |   1,429.63 μs |      - |     507 B |
| FullTextBooleanPrefixSearch | fsdb   |   4,365.92 μs |   5,796.191 μs |    317.709 μs |   4,542.91 μs |      - |     514 B |
| FullTextJoinUsers           | fsdb   |  10,885.04 μs |  51,021.188 μs |  2,796.643 μs |  12,086.83 μs |      - |     521 B |
| **FullTextNaturalSearch**       | **mysql**  |     **470.96 μs** |   **1,870.499 μs** |    **102.528 μs** |     **412.92 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,197.62 μs |   1,018.972 μs |     55.853 μs |   1,181.15 μs |      - |     504 B |
| FullTextAccentSearch        | mysql  |     277.04 μs |   1,202.679 μs |     65.923 μs |     246.86 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql  |     419.80 μs |   1,633.652 μs |     89.546 μs |     423.65 μs |      - |     505 B |
| FullTextJoinUsers           | mysql  |     422.04 μs |     228.973 μs |     12.551 μs |     416.68 μs |      - |     520 B |
| **PointSelectByPk**             | **fsdb**   |     **221.16 μs** |     **109.372 μs** |      **5.995 μs** |     **220.87 μs** |      **-** |     **881 B** |
| FilterScanOrderLimit        | fsdb   |  21,281.01 μs |  72,392.977 μs |  3,968.103 μs |  19,066.82 μs |      - |     602 B |
| FilterBySecondaryEquality   | fsdb   |     636.94 μs |     173.685 μs |      9.520 μs |     631.94 μs |      - |     521 B |
| InsertSingle                | fsdb   |     309.63 μs |     455.065 μs |     24.944 μs |     297.69 μs |      - |    1241 B |
| ReplaceExistingByPk         | fsdb   |     252.73 μs |     375.406 μs |     20.577 μs |     246.26 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |     301.47 μs |     262.283 μs |     14.377 μs |     296.95 μs |      - |     680 B |
| JoinUsersOrders             | fsdb   |   1,253.89 μs |  10,055.191 μs |    551.159 μs |     964.73 μs |      - |     812 B |
| UncorrelatedInSubquery      | fsdb   |   1,029.74 μs |   7,358.486 μs |    403.343 μs |     850.91 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |  48,552.10 μs |  30,731.390 μs |  1,684.491 μs |  49,355.89 μs |      - |         - |
| JsonExtract                 | fsdb   |     705.47 μs |      77.163 μs |      4.230 μs |     703.40 μs |      - |     505 B |
| UpdateByNonIndexed          | fsdb   |  16,286.65 μs |   8,848.549 μs |    485.019 μs |  16,300.07 μs |      - |     762 B |
| **PointSelectByPk**             | **mysql**  |      **40.72 μs** |       **8.148 μs** |      **0.447 μs** |      **40.48 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,961.57 μs |     186.768 μs |     10.237 μs |   1,956.60 μs |      - |     644 B |
| FilterBySecondaryEquality   | mysql  |     178.24 μs |      18.046 μs |      0.989 μs |     177.99 μs |      - |     520 B |
| InsertSingle                | mysql  |     119.80 μs |      51.320 μs |      2.813 μs |     120.38 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     157.28 μs |     267.169 μs |     14.644 μs |     150.53 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     177.16 μs |     546.630 μs |     29.963 μs |     162.58 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     183.64 μs |      16.681 μs |      0.914 μs |     184.07 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     159.21 μs |      20.617 μs |      1.130 μs |     159.59 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,404.45 μs |   4,679.114 μs |    256.478 μs |  20,283.47 μs |      - |     634 B |
| JsonExtract                 | mysql  |      62.36 μs |      10.595 μs |      0.581 μs |      62.46 μs | 0.0610 |     584 B |
| UpdateByNonIndexed          | mysql  |   3,657.90 μs |     594.900 μs |     32.608 μs |   3,640.16 μs |      - |     788 B |
| **FilterByPrimaryKeyList**      | **fsdb**   |     **281.66 μs** |     **186.284 μs** |     **10.211 μs** |     **282.25 μs** |      **-** |    **1694 B** |
| **FilterByPrimaryKeyList**      | **mysql**  |      **46.44 μs** |      **21.075 μs** |      **1.155 μs** |      **45.99 μs** | **0.1831** |    **1694 B** |
| **LeftJoinUsersOrders**         | **fsdb**   |     **746.22 μs** |     **100.556 μs** |      **5.512 μs** |     **745.02 μs** |      **-** |     **825 B** |
| RightJoinUsersOrders        | fsdb   | 248,537.86 μs | 421,488.108 μs | 23,103.186 μs | 256,304.67 μs |      - |         - |
| ReorderedIndexedJoin        | fsdb   |     868.17 μs |   2,491.985 μs |    136.594 μs |     800.20 μs |      - |     819 B |
| IndexedStringInSubquery     | fsdb   |   1,386.59 μs |   5,493.170 μs |    301.099 μs |   1,328.69 μs |      - |     506 B |
| DecimalInSubquery           | fsdb   | 108,307.26 μs | 261,030.558 μs | 14,307.966 μs | 101,916.00 μs |      - |         - |
| CompositeInSubquery         | fsdb   |   1,825.29 μs |   2,404.808 μs |    131.816 μs |   1,835.84 μs |      - |     493 B |
| QuantifiedMembership        | fsdb   |  15,608.41 μs |  28,781.119 μs |  1,577.590 μs |  14,895.69 μs |      - |     509 B |
| CorrelatedOrderCount        | fsdb   |   1,949.29 μs |   1,027.415 μs |     56.316 μs |   1,922.68 μs |      - |     509 B |
| **LeftJoinUsersOrders**         | **mysql**  |     **195.76 μs** |     **110.908 μs** |      **6.079 μs** |     **194.48 μs** |      **-** |     **824 B** |
| RightJoinUsersOrders        | mysql  |     244.64 μs |     150.101 μs |      8.228 μs |     241.67 μs |      - |     896 B |
| ReorderedIndexedJoin        | mysql  |     138.17 μs |     143.571 μs |      7.870 μs |     134.49 μs |      - |     816 B |
| IndexedStringInSubquery     | mysql  |     324.68 μs |     127.446 μs |      6.986 μs |     322.71 μs |      - |     505 B |
| DecimalInSubquery           | mysql  |   8,294.36 μs |   1,289.816 μs |     70.699 μs |   8,273.28 μs |      - |     585 B |
| CompositeInSubquery         | mysql  |     291.75 μs |      63.113 μs |      3.459 μs |     290.21 μs |      - |     568 B |
| QuantifiedMembership        | mysql  |   6,464.74 μs |     387.271 μs |     21.228 μs |   6,459.90 μs |      - |     576 B |
| CorrelatedOrderCount        | mysql  |     215.87 μs |      37.294 μs |      2.044 μs |     216.40 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **547.80 μs** |   **6,761.449 μs** |    **370.618 μs** |     **441.35 μs** |      **-** |    **1001 B** |
| **OrderBySecondaryRange**       | **mysql**  |     **276.65 μs** |   **4,416.490 μs** |    **242.083 μs** |     **227.79 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **250.94 μs** |     **903.788 μs** |     **49.540 μs** |     **224.83 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |     781.98 μs |   2,252.103 μs |    123.445 μs |     825.66 μs |      - |     850 B |
| **FilterBySecondaryRange**      | **mysql**  |     **247.44 μs** |     **738.247 μs** |     **40.466 μs** |     **246.48 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |     182.48 μs |     110.527 μs |      6.058 μs |     185.05 μs |      - |     911 B |

Benchmarks with issues:
  ServerBenchmarks.ConcurrentInsertBurst: ShortRun(IterationCount=3, LaunchCount=1, WarmupCount=3) [Target=fsdb]
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
| **ConnectAuthenticateClose** | **fsdb**   | **299.6 μs** | **348.7 μs** | **19.11 μs** |  **32.29 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **217.8 μs** | **227.0 μs** | **12.44 μs** |  **33.63 KB** |
