<!--
sha: ad4ac75
date: 2026-08-25T07:22:18Z
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
| **InsertBatch100**              | **fsdb**   |   **2,985.22 μs** |   **7,590.567 μs** |   **416.065 μs** | **7.8125** |   **96653 B** |
| ReplaceNewRow               | fsdb   |     124.51 μs |      82.702 μs |     4.533 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | fsdb   |      79.60 μs |       5.895 μs |     0.323 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     172.69 μs |       8.690 μs |     0.476 μs |      - |     880 B |
| SelectCurrentUser           | fsdb   |      95.68 μs |       2.217 μs |     0.122 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      67.74 μs |       2.856 μs |     0.157 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      73.16 μs |       4.812 μs |     0.264 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |     535.17 μs |      60.024 μs |     3.290 μs |      - |     569 B |
| InfoSchemaTablesScan        | fsdb   |     216.95 μs |      10.532 μs |     0.577 μs |      - |     520 B |
| UserPrivilegesScan          | fsdb   |     202.88 μs |       5.972 μs |     0.327 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |   1,957.31 μs |   4,587.644 μs |   251.464 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |   **1,541.49 μs** |   **3,746.481 μs** |   **205.357 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     109.92 μs |     272.099 μs |    14.915 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | mysql  |      30.98 μs |      43.209 μs |     2.368 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      39.23 μs |      13.185 μs |     0.723 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      28.99 μs |       1.110 μs |     0.061 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |      38.00 μs |     249.702 μs |    13.687 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |     265.31 μs |       9.237 μs |     0.506 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |     236.34 μs |     284.414 μs |    15.590 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   2,707.98 μs |     501.482 μs |    27.488 μs |      - |     604 B |
| UserPrivilegesScan          | mysql  |      92.27 μs |      41.236 μs |     2.260 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   1,947.16 μs |   1,421.701 μs |    77.928 μs |      - |    1994 B |
| **UpsertExistingByPk**          | **fsdb**   |     **185.05 μs** |      **82.759 μs** |     **4.536 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |     467.21 μs |     139.224 μs |     7.631 μs |      - |    2160 B |
| ComputedProjection          | fsdb   |     150.58 μs |      11.671 μs |     0.640 μs |      - |     848 B |
| ViewFilterLimit             | fsdb   |  44,078.75 μs |   8,913.936 μs |   488.603 μs |      - |         - |
| ViewAggregate               | fsdb   |  87,344.49 μs |  12,827.210 μs |   703.103 μs |      - |         - |
| RecursiveCte100             | fsdb   |     589.55 μs |      12.439 μs |     0.682 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |     482.41 μs |       6.313 μs |     0.346 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |     114.07 μs |      78.256 μs |     4.289 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |     138.78 μs |      69.869 μs |     3.830 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **136.83 μs** |      **59.739 μs** |     **3.274 μs** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     207.39 μs |     133.254 μs |     7.304 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      38.29 μs |      46.113 μs |     2.528 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      61.34 μs |      15.824 μs |     0.867 μs |      - |     536 B |
| ViewAggregate               | mysql  |  19,909.85 μs |   1,771.532 μs |    97.104 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      52.46 μs |      47.409 μs |     2.599 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     125.84 μs |      11.236 μs |     0.616 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |     108.87 μs |      35.521 μs |     1.947 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |     117.08 μs |       8.626 μs |     0.473 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **387,382.29 μs** |  **74,167.507 μs** | **4,065.371 μs** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql**  |  **59,744.05 μs** | **118,421.270 μs** | **6,491.069 μs** |      **-** |         **-** |
| **FullTextNaturalSearch**       | **fsdb**   |   **2,553.05 μs** |     **304.478 μs** |    **16.689 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | fsdb   |   2,059.05 μs |   3,417.586 μs |   187.329 μs |      - |     507 B |
| FullTextAccentSearch        | fsdb   |   1,478.54 μs |   1,054.978 μs |    57.827 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | fsdb   |     714.65 μs |     772.783 μs |    42.359 μs |      - |     505 B |
| FullTextJoinUsers           | fsdb   |   5,437.20 μs |   5,275.318 μs |   289.158 μs |      - |     531 B |
| **FullTextNaturalSearch**       | **mysql**  |     **406.19 μs** |      **61.936 μs** |     **3.395 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,118.86 μs |     132.047 μs |     7.238 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |     238.17 μs |      66.111 μs |     3.624 μs |      - |     505 B |
| FullTextBooleanPrefixSearch | mysql  |     335.95 μs |     197.500 μs |    10.826 μs |      - |     505 B |
| FullTextJoinUsers           | mysql  |     425.42 μs |     232.834 μs |    12.762 μs |      - |     521 B |
| **PointSelectByPk**             | **fsdb**   |     **176.81 μs** |      **84.842 μs** |     **4.650 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb   |  12,954.05 μs |   1,738.179 μs |    95.275 μs |      - |     577 B |
| FilterBySecondaryEquality   | fsdb   |     497.10 μs |      48.827 μs |     2.676 μs |      - |     522 B |
| InsertSingle                | fsdb   |     149.93 μs |     264.045 μs |    14.473 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | fsdb   |     173.53 μs |      40.414 μs |     2.215 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |     192.52 μs |     144.171 μs |     7.902 μs |      - |     679 B |
| JoinUsersOrders             | fsdb   |   6,973.67 μs |   3,071.552 μs |   168.362 μs |      - |     829 B |
| UncorrelatedInSubquery      | fsdb   |     545.03 μs |      26.277 μs |     1.440 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |  90,651.51 μs |  63,336.068 μs | 3,471.664 μs |      - |         - |
| JsonExtract                 | fsdb   |     217.01 μs |     248.439 μs |    13.618 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   9,144.90 μs |     637.525 μs |    34.945 μs |      - |     741 B |
| **PointSelectByPk**             | **mysql**  |      **39.72 μs** |       **2.503 μs** |     **0.137 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,921.30 μs |      48.142 μs |     2.639 μs |      - |     642 B |
| FilterBySecondaryEquality   | mysql  |     176.39 μs |       4.117 μs |     0.226 μs |      - |     520 B |
| InsertSingle                | mysql  |     118.00 μs |     130.720 μs |     7.165 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     173.82 μs |     107.525 μs |     5.894 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     123.11 μs |      38.229 μs |     2.095 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     178.44 μs |       8.569 μs |     0.470 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     165.96 μs |      19.365 μs |     1.061 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,168.92 μs |   2,144.127 μs |   117.527 μs |      - |     634 B |
| JsonExtract                 | mysql  |      62.21 μs |       4.094 μs |     0.224 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |   3,590.06 μs |     443.144 μs |    24.290 μs |      - |     788 B |
| **ReorderedIndexedJoin**        | **fsdb**   |   **4,155.75 μs** |     **625.180 μs** |    **34.268 μs** |      **-** |     **827 B** |
| CorrelatedOrderCount        | fsdb   |   1,001.43 μs |      88.230 μs |     4.836 μs |      - |     505 B |
| **ReorderedIndexedJoin**        | **mysql**  |     **119.21 μs** |      **14.482 μs** |     **0.794 μs** |      **-** |     **816 B** |
| CorrelatedOrderCount        | mysql  |     209.09 μs |      40.335 μs |     2.211 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **168.30 μs** |       **1.848 μs** |     **0.101 μs** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**  |      **49.84 μs** |       **3.920 μs** |     **0.215 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **125.38 μs** |      **25.826 μs** |     **1.416 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |     262.48 μs |     519.127 μs |    28.455 μs |      - |     848 B |
| **FilterBySecondaryRange**      | **mysql**  |      **48.29 μs** |      **45.520 μs** |     **2.495 μs** | **0.0610** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |     131.83 μs |       9.140 μs |     0.501 μs |      - |     911 B |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  InvocationCount=32  IterationCount=3  
LaunchCount=1  UnrollFactor=1  WarmupCount=3  

```
| Method                   | Target | Mean     | Error     | StdDev   | Allocated |
|------------------------- |------- |---------:|----------:|---------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**   | **274.2 μs** | **740.82 μs** | **40.61 μs** |  **32.31 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **207.3 μs** |  **45.29 μs** |  **2.48 μs** |   **33.6 KB** |
