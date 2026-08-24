<!--
sha: ebc3fca
date: 2026-08-24T19:43:07Z
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
  Job-UOSKYM : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                      | Target | Mean            | Error            | StdDev         | Median          | Gen0   | Allocated |
|---------------------------- |------- |----------------:|-----------------:|---------------:|----------------:|-------:|----------:|
| **WindowTopOrders**             | **fsdb**   | **5,039,958.53 μs** | **1,722,445.967 μs** | **614,240.824 μs** | **4,992,976.35 μs** |      **-** |         **-** |
| **WindowTopOrders**             | **mysql**  |   **714,860.86 μs** |   **184,006.297 μs** |  **65,618.418 μs** |   **676,480.40 μs** |      **-** |         **-** |
| **FullTextNaturalSearch**       | **fsdb**   |   **707,789.99 μs** |   **328,151.532 μs** | **117,021.997 μs** |   **752,688.54 μs** |      **-** |         **-** |
| FullTextBooleanSearch       | fsdb   |   662,801.85 μs |   203,431.534 μs |  72,545.644 μs |   682,811.88 μs |      - |         - |
| FullTextAccentSearch        | fsdb   |   727,897.65 μs |   244,686.960 μs |  87,257.727 μs |   761,026.69 μs |      - |         - |
| FullTextBooleanPrefixSearch | fsdb   |   696,055.35 μs |   539,257.272 μs | 192,304.338 μs |   741,679.15 μs |      - |         - |
| **FullTextNaturalSearch**       | **mysql**  |     **3,723.42 μs** |        **22.518 μs** |       **5.848 μs** |     **3,722.24 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | mysql  |    10,772.34 μs |       144.718 μs |      51.608 μs |    10,775.00 μs |      - |     521 B |
| FullTextAccentSearch        | mysql  |     1,887.87 μs |       185.893 μs |      48.276 μs |     1,864.55 μs |      - |     504 B |
| FullTextBooleanPrefixSearch | mysql  |     2,590.98 μs |        13.651 μs |       3.545 μs |     2,592.32 μs |      - |     508 B |
| **PointSelectByPk**             | **fsdb**   |     **1,164.78 μs** |     **1,118.447 μs** |     **398.849 μs** |     **1,327.70 μs** |      **-** |     **881 B** |
| FilterScanOrderLimit        | fsdb   |   133,666.62 μs |     3,519.266 μs |     913.942 μs |   133,749.23 μs |      - |         - |
| FilterBySecondaryEquality   | fsdb   |     4,595.77 μs |       289.515 μs |     103.244 μs |     4,586.28 μs |      - |     531 B |
| InsertSingle                | fsdb   |        91.28 μs |        38.425 μs |      13.703 μs |        84.62 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | fsdb   |       108.52 μs |        11.882 μs |       3.086 μs |       106.91 μs | 0.1221 |    1119 B |
| UpdateSingleRow             | fsdb   |       129.99 μs |         8.029 μs |       2.863 μs |       129.56 μs |      - |     680 B |
| JoinUsersOrders             | fsdb   |   123,934.32 μs |    48,181.493 μs |  17,181.985 μs |   122,997.01 μs |      - |         - |
| UncorrelatedInSubquery      | fsdb   | 1,093,996.11 μs |    21,675.913 μs |   7,729.840 μs | 1,095,555.48 μs |      - |         - |
| GroupByAggregate            | fsdb   | 1,167,995.69 μs |    47,257.341 μs |  16,852.423 μs | 1,162,341.31 μs |      - |         - |
| JsonExtract                 | fsdb   |       197.82 μs |         2.838 μs |       0.737 μs |       197.58 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   129,363.87 μs |     5,323.658 μs |   1,898.468 μs |   128,472.66 μs |      - |         - |
| **PointSelectByPk**             | **mysql**  |        **39.37 μs** |         **2.392 μs** |       **0.853 μs** |        **39.63 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |    18,397.14 μs |       287.111 μs |     102.386 μs |    18,349.04 μs |      - |     674 B |
| FilterBySecondaryEquality   | mysql  |     1,307.61 μs |        28.202 μs |      10.057 μs |     1,307.42 μs |      - |     522 B |
| InsertSingle                | mysql  |       108.03 μs |        23.142 μs |       8.253 μs |       112.14 μs |      - |    1240 B |
| ReplaceExistingByPk         | mysql  |       145.50 μs |         6.841 μs |       2.440 μs |       145.48 μs |      - |    1119 B |
| UpdateSingleRow             | mysql  |       121.56 μs |        61.018 μs |      15.846 μs |       115.02 μs |      - |     744 B |
| JoinUsersOrders             | mysql  |       194.43 μs |        10.085 μs |       3.596 μs |       196.13 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |       160.19 μs |         1.527 μs |       0.545 μs |       160.12 μs |      - |     584 B |
| GroupByAggregate            | mysql  |   197,736.15 μs |     3,212.603 μs |   1,145.645 μs |   197,439.05 μs |      - |         - |
| JsonExtract                 | mysql  |        62.02 μs |         1.453 μs |       0.518 μs |        62.01 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |    34,379.72 μs |       488.921 μs |     174.354 μs |    34,431.06 μs |      - |     855 B |
| **OrderBySecondaryRange**       | **fsdb**   |       **172.31 μs** |         **3.492 μs** |       **1.245 μs** |       **172.13 μs** |      **-** |    **1007 B** |
| **OrderBySecondaryRange**       | **mysql**  |        **50.48 μs** |         **0.767 μs** |       **0.273 μs** |        **50.52 μs** | **0.0610** |    **1007 B** |
| **FilterBySecondaryRange**      | **fsdb**   |       **150.69 μs** |        **55.634 μs** |      **19.840 μs** |       **140.71 μs** |      **-** |     **879 B** |
| **FilterBySecondaryRange**      | **mysql**  |        **61.42 μs** |        **80.816 μs** |      **28.820 μs** |        **43.55 μs** | **0.0610** |     **880 B** |
