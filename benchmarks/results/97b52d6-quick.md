<!--
sha: 97b52d6
date: 2026-08-25T23:10:49Z
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
| Method                      | Target | Mean          | Error          | StdDev        | Gen0   | Allocated |
|---------------------------- |------- |--------------:|---------------:|--------------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**   |   **3,105.26 μs** |   **7,716.326 μs** |    **422.958 μs** | **7.8125** |   **96653 B** |
| ReplaceNewRow               | fsdb   |     126.70 μs |      90.641 μs |      4.968 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | fsdb   |      82.75 μs |       1.012 μs |      0.055 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     190.03 μs |      31.088 μs |      1.704 μs |      - |     880 B |
| SelectCurrentUser           | fsdb   |     103.19 μs |       5.494 μs |      0.301 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      69.39 μs |       5.230 μs |      0.287 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      75.63 μs |       5.981 μs |      0.328 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |     532.35 μs |     130.320 μs |      7.143 μs |      - |     569 B |
| InfoSchemaTablesScan        | fsdb   |     219.79 μs |       7.858 μs |      0.431 μs |      - |     520 B |
| UserPrivilegesScan          | fsdb   |     203.98 μs |       9.880 μs |      0.542 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |     276.47 μs |      44.201 μs |      2.423 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |   **1,457.52 μs** |   **3,732.665 μs** |    **204.600 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     111.01 μs |       3.679 μs |      0.202 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | mysql  |      31.99 μs |      11.042 μs |      0.605 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      31.67 μs |       7.082 μs |      0.388 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      29.97 μs |       3.486 μs |      0.191 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |      28.86 μs |       1.903 μs |      0.104 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |     236.17 μs |     311.798 μs |     17.091 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |     249.62 μs |      52.359 μs |      2.870 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   3,330.06 μs |      32.625 μs |      1.788 μs |      - |     524 B |
| UserPrivilegesScan          | mysql  |      92.61 μs |       3.409 μs |      0.187 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   2,559.29 μs |   3,038.780 μs |    166.566 μs |      - |    1996 B |
| **ConcurrentPointUpdateBurst**  | **fsdb**   |   **2,770.28 μs** |     **187.176 μs** |     **10.260 μs** | **3.9063** |   **39989 B** |
| ConcurrentInsertBurst       | fsdb   |   1,625.06 μs |     666.021 μs |     36.507 μs | 3.9063 |   47635 B |
| **ConcurrentPointUpdateBurst**  | **mysql**  |     **697.47 μs** |     **418.624 μs** |     **22.946 μs** | **3.9063** |   **41011 B** |
| ConcurrentInsertBurst       | mysql  |     925.47 μs |     406.293 μs |     22.270 μs | 3.9063 |   47635 B |
| **UpsertExistingByPk**          | **fsdb**   |     **179.53 μs** |      **45.117 μs** |      **2.473 μs** |      **-** |    **1511 B** |
| TransactionTwoPointUpdates  | fsdb   |     481.77 μs |     124.977 μs |      6.850 μs |      - |    2160 B |
| ComputedProjection          | fsdb   |     171.75 μs |      15.269 μs |      0.837 μs |      - |     848 B |
| ViewFilterLimit             | fsdb   |     304.02 μs |       8.539 μs |      0.468 μs |      - |     537 B |
| ViewAggregate               | fsdb   | 140,298.31 μs | 169,701.004 μs |  9,301.885 μs |      - |         - |
| RecursiveCte100             | fsdb   |     666.28 μs |     272.201 μs |     14.920 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |     491.61 μs |     133.869 μs |      7.338 μs |      - |     505 B |
| InsertCheckedGenerated      | fsdb   |     114.37 μs |       9.533 μs |      0.523 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |     143.45 μs |     103.124 μs |      5.653 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **117.71 μs** |       **9.752 μs** |      **0.535 μs** | **0.1221** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     223.86 μs |     145.432 μs |      7.972 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      39.44 μs |       5.583 μs |      0.306 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      60.04 μs |       5.160 μs |      0.283 μs | 0.0610 |     536 B |
| ViewAggregate               | mysql  |  20,333.48 μs |   4,342.090 μs |    238.005 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      53.18 μs |      11.098 μs |      0.608 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     120.98 μs |       4.315 μs |      0.237 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |      98.01 μs |     197.835 μs |     10.844 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |     110.38 μs |     149.736 μs |      8.208 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **302,275.67 μs** | **601,887.725 μs** | **32,991.497 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   | 157,625.11 μs |  36,213.733 μs |  1,984.997 μs |      - |         - |
| **WindowTopOrders**             | **mysql**  |  **50,811.58 μs** |  **23,134.169 μs** |  **1,268.062 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  |  20,384.83 μs |   3,048.815 μs |    167.116 μs |      - |     608 B |
| **FullTextNaturalSearch**       | **fsdb**   |   **2,468.85 μs** |     **156.636 μs** |      **8.586 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | fsdb   |   2,465.33 μs |     241.458 μs |     13.235 μs |      - |     509 B |
| FullTextAccentSearch        | fsdb   |   1,239.69 μs |     115.359 μs |      6.323 μs |      - |     507 B |
| FullTextBooleanPrefixSearch | fsdb   |     860.80 μs |      90.914 μs |      4.983 μs |      - |     505 B |
| FullTextJoinUsers           | fsdb   |   4,606.17 μs |     423.350 μs |     23.205 μs |      - |     531 B |
| **FullTextNaturalSearch**       | **mysql**  |     **391.00 μs** |      **37.221 μs** |      **2.040 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,041.71 μs |      10.712 μs |      0.587 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |     239.57 μs |      63.349 μs |      3.472 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql  |     340.09 μs |      87.253 μs |      4.783 μs |      - |     505 B |
| FullTextJoinUsers           | mysql  |     426.99 μs |      16.890 μs |      0.926 μs |      - |     520 B |
| **PointSelectByPk**             | **fsdb**   |     **187.35 μs** |      **46.149 μs** |      **2.530 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb   |  15,151.96 μs |   3,829.219 μs |    209.892 μs |      - |     581 B |
| FilterBySecondaryEquality   | fsdb   |     563.43 μs |      27.764 μs |      1.522 μs |      - |     521 B |
| InsertSingle                | fsdb   |     123.84 μs |     130.668 μs |      7.162 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | fsdb   |     200.37 μs |     318.907 μs |     17.480 μs |      - |    1111 B |
| UpdateSingleRow             | fsdb   |     213.31 μs |     156.213 μs |      8.563 μs |      - |     679 B |
| JoinUsersOrders             | fsdb   |     387.40 μs |      23.785 μs |      1.304 μs |      - |     809 B |
| UncorrelatedInSubquery      | fsdb   |     705.08 μs |      54.729 μs |      3.000 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |  81,904.21 μs |  23,671.787 μs |  1,297.531 μs |      - |         - |
| JsonExtract                 | fsdb   |     225.91 μs |     117.504 μs |      6.441 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |  13,424.20 μs |   5,628.333 μs |    308.508 μs |      - |     741 B |
| **PointSelectByPk**             | **mysql**  |      **33.14 μs** |      **35.222 μs** |      **1.931 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,959.53 μs |     197.841 μs |     10.844 μs |      - |     642 B |
| FilterBySecondaryEquality   | mysql  |     177.51 μs |      15.291 μs |      0.838 μs |      - |     521 B |
| InsertSingle                | mysql  |     113.97 μs |      38.835 μs |      2.129 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     141.13 μs |      43.843 μs |      2.403 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     114.09 μs |     159.957 μs |      8.768 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     177.03 μs |       8.558 μs |      0.469 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     154.85 μs |      22.906 μs |      1.256 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,430.00 μs |   1,699.649 μs |     93.163 μs |      - |     634 B |
| JsonExtract                 | mysql  |      62.65 μs |       3.239 μs |      0.178 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |   3,614.70 μs |      50.709 μs |      2.780 μs |      - |     788 B |
| **ReorderedIndexedJoin**        | **fsdb**   |     **359.29 μs** |      **19.444 μs** |      **1.066 μs** |      **-** |     **816 B** |
| CorrelatedOrderCount        | fsdb   |   1,503.00 μs |     676.221 μs |     37.066 μs |      - |     507 B |
| **ReorderedIndexedJoin**        | **mysql**  |     **120.85 μs** |       **9.983 μs** |      **0.547 μs** |      **-** |     **816 B** |
| CorrelatedOrderCount        | mysql  |     207.37 μs |       9.944 μs |      0.545 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **175.76 μs** |      **14.627 μs** |      **0.802 μs** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**  |      **50.03 μs** |      **15.106 μs** |      **0.828 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **157.34 μs** |     **586.732 μs** |     **32.161 μs** |      **-** |     **872 B** |
| UpdateBySecondaryRange      | fsdb   |     363.93 μs |   1,311.707 μs |     71.899 μs |      - |     848 B |
| **FilterBySecondaryRange**      | **mysql**  |      **44.01 μs** |       **0.413 μs** |      **0.023 μs** | **0.0610** |     **872 B** |
| UpdateBySecondaryRange      | mysql  |     127.52 μs |       9.380 μs |      0.514 μs |      - |     911 B |
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
| **ConnectAuthenticateClose** | **fsdb**   | **281.1 μs** | **852.7 μs** | **46.74 μs** |  **32.31 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **231.6 μs** | **575.0 μs** | **31.52 μs** |  **33.63 KB** |
