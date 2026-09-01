<!--
sha: 97b52d6
date: 2026-08-25T23:31:46Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 100000 users, 500000 orders, 100000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]     : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  Job-YDFMYL : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                      | Target | Mean            | Error            | StdDev         | Median          | Gen0   | Allocated |
|---------------------------- |------- |----------------:|-----------------:|---------------:|----------------:|-------:|----------:|
| **WindowTopOrders**             | **fsdb**   | **3,781,120.17 μs** | **1,748,886.665 μs** | **623,669.831 μs** | **3,682,899.69 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   | 2,673,326.58 μs | 1,690,563.131 μs | 602,871.097 μs | 2,516,120.92 μs |      - |         - |
| **WindowTopOrders**             | **mysql**  | **1,139,513.41 μs** | **1,293,143.593 μs** | **461,147.462 μs** |   **992,634.81 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  |   338,345.67 μs |    25,168.543 μs |   8,975.345 μs |   337,093.67 μs |      - |         - |
| **FullTextNaturalSearch**       | **fsdb**   |    **34,080.59 μs** |     **1,638.765 μs** |     **425.582 μs** |    **33,994.53 μs** |      **-** |     **589 B** |
| FullTextBooleanSearch       | fsdb   |    37,760.35 μs |     1,028.623 μs |     267.130 μs |    37,744.28 μs |      - |     601 B |
| FullTextAccentSearch        | fsdb   |    17,556.25 μs |       760.238 μs |     271.108 μs |    17,520.94 μs |      - |     546 B |
| FullTextBooleanPrefixSearch | fsdb   |    14,170.68 μs |       840.426 μs |     299.704 μs |    14,283.71 μs |      - |     525 B |
| FullTextJoinUsers           | fsdb   |    60,780.14 μs |     1,077.955 μs |     279.941 μs |    60,917.06 μs |      - |         - |
| **FullTextNaturalSearch**       | **mysql**  |     **3,876.37 μs** |       **117.694 μs** |      **41.971 μs** |     **3,871.35 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | mysql  |    11,047.61 μs |       256.987 μs |      91.644 μs |    11,042.25 μs |      - |     521 B |
| FullTextAccentSearch        | mysql  |     1,929.41 μs |        56.529 μs |      14.680 μs |     1,933.75 μs |      - |     507 B |
| FullTextBooleanPrefixSearch | mysql  |     2,655.76 μs |        66.045 μs |      23.552 μs |     2,658.20 μs |      - |     508 B |
| FullTextJoinUsers           | mysql  |     3,785.49 μs |        93.175 μs |      33.227 μs |     3,783.80 μs |      - |     525 B |
| **PointSelectByPk**             | **fsdb**   |       **174.48 μs** |         **4.140 μs** |       **1.476 μs** |       **174.32 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit        | fsdb   |   170,119.16 μs |     2,493.354 μs |     889.154 μs |   170,013.17 μs |      - |         - |
| FilterBySecondaryEquality   | fsdb   |     5,669.56 μs |     1,077.918 μs |     384.396 μs |     5,482.82 μs |      - |     531 B |
| InsertSingle                | fsdb   |       149.94 μs |         7.194 μs |       2.566 μs |       149.90 μs |      - |    1240 B |
| ReplaceExistingByPk         | fsdb   |       263.37 μs |        15.815 μs |       5.640 μs |       265.27 μs |      - |    1119 B |
| UpdateSingleRow             | fsdb   |       282.33 μs |        17.636 μs |       6.289 μs |       284.40 μs |      - |     681 B |
| JoinUsersOrders             | fsdb   |       399.85 μs |        25.811 μs |       6.703 μs |       399.00 μs |      - |     809 B |
| UncorrelatedInSubquery      | fsdb   |       697.17 μs |       217.079 μs |      77.412 μs |       688.22 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |   990,788.87 μs |   826,497.761 μs | 294,737.063 μs |   853,162.83 μs |      - |         - |
| JsonExtract                 | fsdb   |       217.95 μs |        36.708 μs |      13.091 μs |       212.85 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   157,323.29 μs |     9,017.342 μs |   2,341.775 μs |   156,014.58 μs |      - |         - |
| **PointSelectByPk**             | **mysql**  |        **40.61 μs** |         **0.546 μs** |       **0.195 μs** |        **40.62 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |    18,651.05 μs |        55.393 μs |      19.754 μs |    18,648.13 μs |      - |     674 B |
| FilterBySecondaryEquality   | mysql  |     1,311.95 μs |        29.449 μs |      10.502 μs |     1,310.93 μs |      - |     522 B |
| InsertSingle                | mysql  |       119.70 μs |        27.129 μs |       9.674 μs |       120.56 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |       153.34 μs |        15.526 μs |       5.537 μs |       155.71 μs |      - |    1119 B |
| UpdateSingleRow             | mysql  |       135.41 μs |        26.592 μs |       9.483 μs |       134.23 μs |      - |     744 B |
| JoinUsersOrders             | mysql  |       191.44 μs |         5.468 μs |       1.950 μs |       190.68 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |       174.20 μs |        10.457 μs |       3.729 μs |       174.10 μs |      - |     584 B |
| GroupByAggregate            | mysql  |   200,218.66 μs |     2,802.545 μs |     999.414 μs |   200,364.69 μs |      - |         - |
| JsonExtract                 | mysql  |        65.03 μs |        11.917 μs |       3.095 μs |        66.48 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |    35,354.72 μs |     1,389.024 μs |     360.725 μs |    35,348.32 μs |      - |     875 B |
| **ReorderedIndexedJoin**        | **fsdb**   |       **354.44 μs** |         **8.406 μs** |       **2.183 μs** |       **353.95 μs** |      **-** |     **817 B** |
| CorrelatedOrderCount        | fsdb   |     1,434.74 μs |        43.978 μs |      15.683 μs |     1,439.13 μs |      - |     507 B |
| **ReorderedIndexedJoin**        | **mysql**  |       **130.56 μs** |        **19.792 μs** |       **7.058 μs** |       **131.44 μs** |      **-** |     **816 B** |
| CorrelatedOrderCount        | mysql  |       770.77 μs |     1,223.497 μs |     436.311 μs |       932.14 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |       **208.01 μs** |        **29.313 μs** |      **10.453 μs** |       **211.00 μs** |      **-** |    **1007 B** |
| **OrderBySecondaryRange**       | **mysql**  |        **51.15 μs** |         **1.360 μs** |       **0.485 μs** |        **51.20 μs** | **0.0610** |    **1007 B** |
| **FilterBySecondaryRange**      | **fsdb**   |       **134.31 μs** |         **1.163 μs** |       **0.415 μs** |       **134.24 μs** |      **-** |     **879 B** |
| UpdateBySecondaryRange      | fsdb   |       339.20 μs |        17.371 μs |       4.511 μs |       339.81 μs |      - |     849 B |
| **FilterBySecondaryRange**      | **mysql**  |        **44.97 μs** |         **0.869 μs** |       **0.226 μs** |        **45.03 μs** | **0.0610** |     **879 B** |
| UpdateBySecondaryRange      | mysql  |       129.31 μs |        24.716 μs |       8.814 μs |       133.66 μs |      - |     912 B |
