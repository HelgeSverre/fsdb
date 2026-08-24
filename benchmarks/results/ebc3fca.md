<!--
sha: ebc3fca
date: 2026-08-24T19:04:37Z
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
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-CXRJQV : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                      | Target | Mean          | Error         | StdDev       | Gen0   | Allocated |
|---------------------------- |------- |--------------:|--------------:|-------------:|-------:|----------:|
| **InsertBatch100**              | **fsdb**   |   **1,970.23 μs** |     **26.979 μs** |     **7.006 μs** | **9.7656** |   **96651 B** |
| ReplaceNewRow               | fsdb   |      82.80 μs |      1.309 μs |     0.340 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | fsdb   |      81.01 μs |      0.534 μs |     0.191 μs |      - |     960 B |
| PointSelectAsLimitedUser    | fsdb   |     163.42 μs |      3.112 μs |     1.110 μs |      - |     880 B |
| SelectCurrentUser           | fsdb   |      98.12 μs |      0.813 μs |     0.290 μs |      - |     504 B |
| ShowGrants                  | fsdb   |      68.14 μs |      0.366 μs |     0.095 μs |      - |     488 B |
| ShowFullTables              | fsdb   |      73.89 μs |      0.356 μs |     0.127 μs |      - |     504 B |
| InfoSchemaColumnsForTable   | fsdb   |     566.67 μs |     37.821 μs |    13.487 μs |      - |     569 B |
| InfoSchemaTablesScan        | fsdb   |     207.55 μs |      1.468 μs |     0.524 μs |      - |     520 B |
| UserPrivilegesScan          | fsdb   |     189.39 μs |      1.899 μs |     0.493 μs |      - |     520 B |
| CreateGrantDropUser         | fsdb   |   1,925.46 μs |  1,242.560 μs |   443.109 μs |      - |    1993 B |
| **InsertBatch100**              | **mysql**  |   **1,265.07 μs** |    **198.014 μs** |    **51.424 μs** | **9.7656** |   **96714 B** |
| ReplaceNewRow               | mysql  |     108.74 μs |     15.784 μs |     5.629 μs | 0.1221 |    1280 B |
| PreparedPointSelect         | mysql  |      33.23 μs |      0.184 μs |     0.048 μs | 0.0610 |     960 B |
| PointSelectAsLimitedUser    | mysql  |      38.86 μs |      0.235 μs |     0.061 μs | 0.0610 |     880 B |
| SelectCurrentUser           | mysql  |      28.70 μs |      0.294 μs |     0.105 μs | 0.0305 |     504 B |
| ShowGrants                  | mysql  |      28.85 μs |      0.515 μs |     0.184 μs | 0.0305 |     488 B |
| ShowFullTables              | mysql  |     253.56 μs |      1.892 μs |     0.675 μs |      - |     505 B |
| InfoSchemaColumnsForTable   | mysql  |     246.64 μs |      1.656 μs |     0.590 μs |      - |     569 B |
| InfoSchemaTablesScan        | mysql  |   1,633.98 μs |     15.492 μs |     5.525 μs |      - |     522 B |
| UserPrivilegesScan          | mysql  |      98.15 μs |      0.770 μs |     0.275 μs |      - |     600 B |
| CreateGrantDropUser         | mysql  |   1,825.69 μs |     27.335 μs |     7.099 μs |      - |    1994 B |
| **UpsertExistingByPk**          | **fsdb**   |     **120.03 μs** |      **9.846 μs** |     **3.511 μs** |      **-** |    **1510 B** |
| TransactionTwoPointUpdates  | fsdb   |     331.68 μs |     18.814 μs |     6.709 μs |      - |    2159 B |
| ComputedProjection          | fsdb   |     149.92 μs |      0.642 μs |     0.229 μs |      - |     848 B |
| ViewFilterLimit             | fsdb   |  44,199.87 μs |  1,396.388 μs |   497.966 μs |      - |         - |
| ViewAggregate               | fsdb   |  79,882.84 μs | 14,835.472 μs | 5,290.472 μs |      - |         - |
| RecursiveCte100             | fsdb   |     551.24 μs |     31.619 μs |     8.211 μs |      - |     505 B |
| CorrelatedJsonTable         | fsdb   |  17,792.61 μs |  1,202.918 μs |   428.972 μs |      - |     546 B |
| InsertCheckedGenerated      | fsdb   |      80.14 μs |      0.991 μs |     0.257 μs |      - |     704 B |
| InsertWithAfterTrigger      | fsdb   |      98.14 μs |      2.255 μs |     0.586 μs |      - |     704 B |
| **UpsertExistingByPk**          | **mysql**  |     **123.60 μs** |      **9.132 μs** |     **3.257 μs** | **0.1221** |    **1510 B** |
| TransactionTwoPointUpdates  | mysql  |     203.71 μs |     49.915 μs |    17.800 μs | 0.2441 |    2287 B |
| ComputedProjection          | mysql  |      39.26 μs |      0.210 μs |     0.075 μs | 0.0610 |     848 B |
| ViewFilterLimit             | mysql  |      60.25 μs |      0.228 μs |     0.081 μs | 0.0610 |     536 B |
| ViewAggregate               | mysql  |  19,699.12 μs |    158.548 μs |    56.540 μs |      - |     634 B |
| RecursiveCte100             | mysql  |      48.85 μs |     11.010 μs |     3.926 μs | 0.0610 |     584 B |
| CorrelatedJsonTable         | mysql  |     123.78 μs |      2.539 μs |     0.905 μs |      - |     584 B |
| InsertCheckedGenerated      | mysql  |      96.04 μs |      1.248 μs |     0.445 μs |      - |     704 B |
| InsertWithAfterTrigger      | mysql  |     104.80 μs |     18.534 μs |     6.609 μs |      - |     704 B |
| **WindowTopOrders**             | **fsdb**   | **342,683.99 μs** | **19,922.320 μs** | **5,173.763 μs** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql**  |  **46,661.97 μs** |  **1,772.444 μs** |   **632.070 μs** |      **-** |         **-** |
| **FullTextNaturalSearch**       | **fsdb**   |  **53,683.84 μs** |  **2,885.764 μs** |   **749.424 μs** |      **-** |         **-** |
| FullTextBooleanSearch       | fsdb   |  50,730.05 μs |  7,403.512 μs | 2,640.164 μs |      - |         - |
| FullTextAccentSearch        | fsdb   |  51,176.75 μs |  3,061.021 μs |   794.938 μs |      - |         - |
| FullTextBooleanPrefixSearch | fsdb   |  49,475.35 μs |  3,568.935 μs | 1,272.716 μs |      - |         - |
| **FullTextNaturalSearch**       | **mysql**  |     **392.59 μs** |      **4.210 μs** |     **1.501 μs** |      **-** |     **505 B** |
| FullTextBooleanSearch       | mysql  |   1,041.17 μs |      5.645 μs |     2.013 μs |      - |     506 B |
| FullTextAccentSearch        | mysql  |     241.39 μs |      1.678 μs |     0.598 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql  |     328.40 μs |      7.069 μs |     2.521 μs |      - |     505 B |
| **PointSelectByPk**             | **fsdb**   |     **162.65 μs** |      **3.700 μs** |     **0.961 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb   |  12,369.16 μs |    284.488 μs |   101.451 μs |      - |     581 B |
| FilterBySecondaryEquality   | fsdb   |     472.61 μs |     20.733 μs |     7.393 μs |      - |     521 B |
| InsertSingle                | fsdb   |      79.76 μs |      4.266 μs |     1.108 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | fsdb   |      95.68 μs |      3.481 μs |     1.241 μs | 0.1221 |    1111 B |
| UpdateSingleRow             | fsdb   |     133.23 μs |     18.911 μs |     6.744 μs |      - |     679 B |
| JoinUsersOrders             | fsdb   |   4,490.02 μs |    237.569 μs |    61.696 μs |      - |     816 B |
| UncorrelatedInSubquery      | fsdb   | 102,745.35 μs |  2,503.297 μs |   650.098 μs |      - |         - |
| GroupByAggregate            | fsdb   |  87,512.35 μs | 15,676.537 μs | 5,590.404 μs |      - |         - |
| JsonExtract                 | fsdb   |     194.13 μs |      0.388 μs |     0.138 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   9,203.46 μs |    226.777 μs |    80.871 μs |      - |     741 B |
| **PointSelectByPk**             | **mysql**  |      **39.58 μs** |      **0.317 μs** |     **0.113 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |   1,925.22 μs |     25.695 μs |     9.163 μs |      - |     642 B |
| FilterBySecondaryEquality   | mysql  |     173.35 μs |      2.852 μs |     1.017 μs |      - |     520 B |
| InsertSingle                | mysql  |     107.21 μs |      3.334 μs |     1.189 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |     132.26 μs |     19.344 μs |     5.024 μs |      - |    1111 B |
| UpdateSingleRow             | mysql  |     107.00 μs |     20.772 μs |     7.407 μs |      - |     743 B |
| JoinUsersOrders             | mysql  |     177.02 μs |      3.895 μs |     1.389 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |     148.85 μs |      3.576 μs |     1.275 μs |      - |     584 B |
| GroupByAggregate            | mysql  |  20,173.56 μs |    231.352 μs |    60.081 μs |      - |     634 B |
| JsonExtract                 | mysql  |      60.97 μs |      2.555 μs |     0.911 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |   3,523.13 μs |     80.278 μs |    28.628 μs |      - |     788 B |
| **OrderBySecondaryRange**       | **fsdb**   |     **169.38 μs** |      **1.532 μs** |     **0.398 μs** |      **-** |    **1000 B** |
| **OrderBySecondaryRange**       | **mysql**  |      **49.63 μs** |      **0.744 μs** |     **0.193 μs** | **0.0610** |    **1000 B** |
| **FilterBySecondaryRange**      | **fsdb**   |     **126.46 μs** |      **9.101 μs** |     **3.245 μs** |      **-** |     **872 B** |
| **FilterBySecondaryRange**      | **mysql**  |      **43.99 μs** |      **0.276 μs** |     **0.072 μs** | **0.0610** |     **872 B** |
```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-RSJGQV : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

InvocationCount=32  IterationCount=6  UnrollFactor=1
WarmupCount=3

```
| Method                   | Target | Mean     | Error    | StdDev  | Allocated |
|------------------------- |------- |---------:|---------:|--------:|----------:|
| **ConnectAuthenticateClose** | **fsdb**   | **257.2 μs** | **19.31 μs** | **5.01 μs** |  **32.33 KB** |
| **ConnectAuthenticateClose** | **mysql**  | **221.2 μs** | **15.62 μs** | **5.57 μs** |  **33.62 KB** |
