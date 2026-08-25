<!--
sha: a6bfd5d
date: 2026-08-25T07:56:48Z
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
| **InsertBatch100**              | **fsdb**   |   **3,392.69 μs** |   **8,384.823 μs** |   **459.600 μs** | **7.8125** |   **96653 B** |
| ReplaceNewRow               | fsdb   |     121.60 μs |      43.984 μs |     2.411 μs |      - |    1280 B |
| PreparedPointSelect         | fsdb   |      81.40 μs |       6.816 μs |     0.374 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     179.66 μs |      57.078 μs |     3.129 μs |      - |     880 B |
| SelectCurrentUser           | fsdb   |     100.40 μs |      15.164 μs |     0.831 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      67.91 μs |       8.995 μs |     0.493 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      76.29 μs |      27.197 μs |     1.491 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |     623.23 μs |      67.263 μs |     3.687 μs |      - |     569 B |
| InfoSchemaTablesScan        | fsdb   |     223.66 μs |      15.492 μs |     0.849 μs |      - |     520 B |
| UserPrivilegesScan          | fsdb   |     198.96 μs |      75.607 μs |     4.144 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |   2,215.18 μs |   6,088.062 μs |   333.707 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |   **1,655.87 μs** |   **7,158.943 μs** |   **392.406 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     115.76 μs |      31.697 μs |     1.737 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | mysql  |      30.49 μs |      18.262 μs |     1.001 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      36.49 μs |      17.637 μs |     0.967 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      29.11 μs |       8.764 μs |     0.480 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |      29.62 μs |       8.648 μs |     0.474 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |     271.50 μs |      72.414 μs |     3.969 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |     249.68 μs |      45.100 μs |     2.472 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   2,785.79 μs |     460.971 μs |    25.267 μs |      - |     604 B |
| UserPrivilegesScan          | mysql  |      85.12 μs |      36.529 μs |     2.002 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   1,896.09 μs |      96.706 μs |     5.301 μs |      - |    1996 B |
| **UpsertExistingByPk**          | **fsdb**   |     **185.41 μs** |      **43.445 μs** |     **2.381 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |     601.37 μs |   2,871.343 μs |   157.388 μs |      - |    2159 B |
| ComputedProjection          | fsdb   |     162.94 μs |      31.460 μs |     1.724 μs |      - |     848 B |
| ViewFilterLimit             | fsdb   |     288.80 μs |      26.077 μs |     1.429 μs |      - |     537 B |
| ViewAggregate               | fsdb   |  91,012.61 μs |  28,552.841 μs | 1,565.078 μs |      - |         - |
| RecursiveCte100             | fsdb   |     638.83 μs |     296.426 μs |    16.248 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |     491.00 μs |      76.138 μs |     4.173 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |     108.52 μs |      92.136 μs |     5.050 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |     144.04 μs |      57.981 μs |     3.178 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **134.20 μs** |      **26.020 μs** |     **1.426 μs** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     227.59 μs |     213.922 μs |    11.726 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      35.65 μs |      59.188 μs |     3.244 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      60.48 μs |       4.637 μs |     0.254 μs | 0.0610 |     536 B |
| ViewAggregate               | mysql  |  20,091.38 μs |   1,095.878 μs |    60.069 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      48.46 μs |      35.354 μs |     1.938 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     124.88 μs |      33.823 μs |     1.854 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |     109.92 μs |     156.832 μs |     8.596 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |     116.64 μs |      32.108 μs |     1.760 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **397,677.18 μs** | **113,696.723 μs** | **6,232.101 μs** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql**  |  **47,488.81 μs** |  **13,196.379 μs** |   **723.338 μs** |      **-** |    **2952 B** |
| **FullTextNaturalSearch**       | **fsdb**   |   **2,581.34 μs** |     **297.695 μs** |    **16.318 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | fsdb   |   1,878.76 μs |     444.137 μs |    24.345 μs |      - |     507 B |
| FullTextAccentSearch        | fsdb   |   1,320.94 μs |     245.974 μs |    13.483 μs |      - |     507 B |
| FullTextBooleanPrefixSearch | fsdb   |     684.85 μs |      34.249 μs |     1.877 μs |      - |     505 B |
| FullTextJoinUsers           | fsdb   |   4,686.99 μs |   1,345.649 μs |    73.760 μs |      - |     531 B |
| **FullTextNaturalSearch**       | **mysql**  |     **405.65 μs** |      **93.028 μs** |     **5.099 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,125.25 μs |      22.700 μs |     1.244 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |     234.89 μs |      83.955 μs |     4.602 μs |      - |     505 B |
| FullTextBooleanPrefixSearch | mysql  |     345.70 μs |   1,090.438 μs |    59.771 μs |      - |     505 B |
| FullTextJoinUsers           | mysql  |     424.91 μs |      71.032 μs |     3.894 μs |      - |     521 B |
| **PointSelectByPk**             | **fsdb**   |     **192.63 μs** |      **14.450 μs** |     **0.792 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb   |  12,354.34 μs |     775.142 μs |    42.488 μs |      - |     581 B |
| FilterBySecondaryEquality   | fsdb   |     477.23 μs |       7.979 μs |     0.437 μs |      - |     521 B |
| InsertSingle                | fsdb   |     125.27 μs |      49.775 μs |     2.728 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | fsdb   |     167.97 μs |       8.872 μs |     0.486 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |     190.84 μs |      45.945 μs |     2.518 μs |      - |     679 B |
| JoinUsersOrders             | fsdb   |     350.46 μs |      69.937 μs |     3.834 μs |      - |     809 B |
| UncorrelatedInSubquery      | fsdb   |     533.64 μs |     250.513 μs |    13.731 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |  93,099.45 μs | 140,313.886 μs | 7,691.078 μs |      - |         - |
| JsonExtract                 | fsdb   |     201.09 μs |      42.594 μs |     2.335 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   9,628.87 μs |   1,744.183 μs |    95.605 μs |      - |     741 B |
| **PointSelectByPk**             | **mysql**  |      **39.78 μs** |      **14.027 μs** |     **0.769 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,934.70 μs |      51.084 μs |     2.800 μs |      - |     642 B |
| FilterBySecondaryEquality   | mysql  |     176.16 μs |      33.111 μs |     1.815 μs |      - |     520 B |
| InsertSingle                | mysql  |     130.79 μs |     300.830 μs |    16.489 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     141.41 μs |      34.342 μs |     1.882 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     130.92 μs |     115.104 μs |     6.309 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     196.37 μs |      45.549 μs |     2.497 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     169.36 μs |      76.923 μs |     4.216 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,618.31 μs |   1,684.998 μs |    92.360 μs |      - |     624 B |
| JsonExtract                 | mysql  |      62.93 μs |       2.694 μs |     0.148 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |   3,664.95 μs |     323.641 μs |    17.740 μs |      - |     788 B |
| **ReorderedIndexedJoin**        | **fsdb**   |     **340.94 μs** |      **21.025 μs** |     **1.152 μs** |      **-** |     **817 B** |
| CorrelatedOrderCount        | fsdb   |   1,006.80 μs |     398.576 μs |    21.847 μs |      - |     507 B |
| **ReorderedIndexedJoin**        | **mysql**  |     **123.14 μs** |      **33.110 μs** |     **1.815 μs** |      **-** |     **816 B** |
| CorrelatedOrderCount        | mysql  |     208.69 μs |      30.967 μs |     1.697 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **172.79 μs** |       **7.422 μs** |     **0.407 μs** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**  |      **46.97 μs** |      **51.877 μs** |     **2.844 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **127.44 μs** |      **14.420 μs** |     **0.790 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |     288.98 μs |     673.787 μs |    36.933 μs |      - |     848 B |
| **FilterBySecondaryRange**      | **mysql**  |      **43.39 μs** |      **14.190 μs** |     **0.778 μs** | **0.0610** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |     118.84 μs |      34.338 μs |     1.882 μs |      - |     911 B |
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
| **ConnectAuthenticateClose** | **fsdb**   | **276.3 μs** | **200.9 μs** | **11.01 μs** |  **32.31 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **248.5 μs** | **407.1 μs** | **22.32 μs** |   **33.6 KB** |
