<!--
sha: 98bc883
date: 2026-09-01T09:12:25Z
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
  Job-XMMPOA : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                      | Target | Mean            | Error            | StdDev         | Median          | Gen0   | Allocated |
|---------------------------- |------- |----------------:|-----------------:|---------------:|----------------:|-------:|----------:|
| **WindowTopOrders**             | **fsdb**   | **1,211,414.79 μs** | **1,593,482.842 μs** | **568,251.331 μs** | **1,195,212.35 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | fsdb   |   246,679.20 μs |   321,484.917 μs | 114,644.618 μs |   186,363.36 μs |      - |         - |
| **WindowTopOrders**             | **mysql**  | **1,197,938.72 μs** |   **159,418.036 μs** |  **41,400.358 μs** | **1,206,737.50 μs** |      **-** |         **-** |
| WindowCumeDistPeers         | mysql  |   458,388.63 μs |    60,767.127 μs |  21,670.143 μs |   461,998.48 μs |      - |         - |
| **FullTextNaturalSearch**       | **fsdb**   |    **10,276.81 μs** |       **520.191 μs** |     **185.505 μs** |    **10,251.69 μs** |      **-** |     **525 B** |
| FullTextBooleanSearch       | fsdb   |    23,213.05 μs |       546.176 μs |     194.771 μs |    23,157.52 μs |      - |     546 B |
| FullTextAccentSearch        | fsdb   |     6,299.11 μs |       309.044 μs |     110.208 μs |     6,291.40 μs |      - |     515 B |
| FullTextBooleanPrefixSearch | fsdb   |     6,979.89 μs |        53.115 μs |      13.794 μs |     6,978.07 μs |      - |     515 B |
| FullTextJoinUsers           | fsdb   |    24,978.14 μs |     1,311.547 μs |     467.710 μs |    24,919.66 μs |      - |     562 B |
| **FullTextNaturalSearch**       | **mysql**  |     **3,889.33 μs** |        **72.875 μs** |      **25.988 μs** |     **3,878.72 μs** |      **-** |     **509 B** |
| FullTextBooleanSearch       | mysql  |    11,690.93 μs |        95.126 μs |      33.923 μs |    11,692.42 μs |      - |     746 B |
| FullTextAccentSearch        | mysql  |     1,908.27 μs |       382.728 μs |      99.393 μs |     1,872.60 μs |      - |     507 B |
| FullTextBooleanPrefixSearch | mysql  |     2,864.68 μs |       239.838 μs |      62.285 μs |     2,828.10 μs |      - |     509 B |
| FullTextJoinUsers           | mysql  |     3,942.38 μs |       362.997 μs |      94.269 μs |     3,903.61 μs |      - |     528 B |
| **PointSelectByPk**             | **fsdb**   |       **266.88 μs** |        **32.440 μs** |       **8.425 μs** |       **264.78 μs** |      **-** |     **881 B** |
| FilterScanOrderLimit        | fsdb   |    84,076.80 μs |     4,136.978 μs |   1,475.286 μs |    84,531.62 μs |      - |         - |
| FilterBySecondaryEquality   | fsdb   |     3,808.32 μs |        53.134 μs |      18.948 μs |     3,809.80 μs |      - |     525 B |
| InsertSingle                | fsdb   |       404.04 μs |        24.168 μs |       8.618 μs |       402.92 μs |      - |    1241 B |
| ReplaceExistingByPk         | fsdb   |       322.21 μs |        15.085 μs |       3.918 μs |       324.02 μs |      - |    1120 B |
| UpdateSingleRow             | fsdb   |       368.53 μs |        48.629 μs |      12.629 μs |       365.36 μs |      - |     681 B |
| JoinUsersOrders             | fsdb   |       302.40 μs |         3.835 μs |       1.368 μs |       302.80 μs |      - |     809 B |
| UncorrelatedInSubquery      | fsdb   |       519.08 μs |        10.653 μs |       3.799 μs |       517.95 μs |      - |     505 B |
| GroupByAggregate            | fsdb   |   313,032.70 μs |    17,766.377 μs |   4,613.872 μs |   314,754.08 μs |      - |         - |
| JsonExtract                 | fsdb   |       182.87 μs |         0.631 μs |       0.164 μs |       182.85 μs |      - |     504 B |
| UpdateByNonIndexed          | fsdb   |   104,790.22 μs |    11,019.129 μs |   3,929.528 μs |   106,276.78 μs |      - |         - |
| **PointSelectByPk**             | **mysql**  |        **41.87 μs** |         **2.125 μs** |       **0.758 μs** |        **42.20 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit        | mysql  |    18,373.41 μs |       389.513 μs |     101.155 μs |    18,380.60 μs |      - |     674 B |
| FilterBySecondaryEquality   | mysql  |     1,286.28 μs |        14.911 μs |       3.872 μs |     1,284.72 μs |      - |     522 B |
| InsertSingle                | mysql  |       114.72 μs |         8.436 μs |       3.008 μs |       114.59 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk         | mysql  |       155.61 μs |         9.156 μs |       3.265 μs |       156.50 μs |      - |    1119 B |
| UpdateSingleRow             | mysql  |       141.89 μs |        17.400 μs |       6.205 μs |       143.08 μs |      - |     744 B |
| JoinUsersOrders             | mysql  |       194.38 μs |         2.979 μs |       1.062 μs |       194.27 μs |      - |     808 B |
| UncorrelatedInSubquery      | mysql  |       163.38 μs |         0.916 μs |       0.327 μs |       163.42 μs |      - |     584 B |
| GroupByAggregate            | mysql  |   200,403.21 μs |     7,680.025 μs |   1,994.478 μs |   199,976.82 μs |      - |         - |
| JsonExtract                 | mysql  |        63.55 μs |         0.870 μs |       0.310 μs |        63.60 μs |      - |     584 B |
| UpdateByNonIndexed          | mysql  |    41,212.48 μs |    22,597.747 μs |   8,058.574 μs |    37,708.70 μs |      - |     875 B |
| **FilterByPrimaryKeyList**      | **fsdb**   |       **310.82 μs** |         **6.323 μs** |       **2.255 μs** |       **311.93 μs** |      **-** |    **1732 B** |
| **FilterByPrimaryKeyList**      | **mysql**  |        **51.85 μs** |         **1.091 μs** |       **0.389 μs** |        **51.86 μs** | **0.1831** |    **1732 B** |
| **LeftJoinUsersOrders**         | **fsdb**   |       **300.07 μs** |        **15.436 μs** |       **5.505 μs** |       **299.35 μs** |      **-** |     **825 B** |
| RightJoinUsersOrders        | fsdb   |       354.48 μs |         4.839 μs |       1.726 μs |       355.03 μs |      - |     829 B |
| ReorderedIndexedJoin        | fsdb   |       292.93 μs |        18.110 μs |       6.458 μs |       294.30 μs |      - |     817 B |
| IndexedStringInSubquery     | fsdb   |       742.87 μs |        35.354 μs |      12.607 μs |       742.36 μs |      - |     505 B |
| DecimalInSubquery           | fsdb   |   131,985.11 μs |    19,717.957 μs |   7,031.614 μs |   128,259.67 μs |      - |         - |
| CompositeInSubquery         | fsdb   |     1,211.34 μs |        76.385 μs |      19.837 μs |     1,208.56 μs |      - |     488 B |
| QuantifiedMembership        | fsdb   |    39,304.93 μs |     2,116.817 μs |     754.877 μs |    39,347.93 μs |      - |         - |
| CorrelatedOrderCount        | fsdb   |     1,245.47 μs |       136.344 μs |      48.622 μs |     1,246.58 μs |      - |     504 B |
| **LeftJoinUsersOrders**         | **mysql**  |       **202.78 μs** |         **3.262 μs** |       **0.847 μs** |       **202.32 μs** |      **-** |     **824 B** |
| RightJoinUsersOrders        | mysql  |       266.57 μs |         3.958 μs |       1.028 μs |       266.55 μs |      - |     897 B |
| ReorderedIndexedJoin        | mysql  |       136.46 μs |         5.035 μs |       1.308 μs |       135.98 μs |      - |     816 B |
| IndexedStringInSubquery     | mysql  |       367.72 μs |        10.548 μs |       3.761 μs |       367.23 μs |      - |     512 B |
| DecimalInSubquery           | mysql  |    80,963.10 μs |       240.489 μs |      62.454 μs |    80,979.88 μs |      - |         - |
| CompositeInSubquery         | mysql  |       332.81 μs |         2.591 μs |       0.673 μs |       333.20 μs |      - |     569 B |
| QuantifiedMembership        | mysql  |    63,038.49 μs |     3,394.224 μs |   1,210.413 μs |    62,670.73 μs |      - |         - |
| CorrelatedOrderCount        | mysql  |       216.54 μs |        14.894 μs |       5.311 μs |       213.90 μs |      - |     504 B |
| **OrderBySecondaryRange**       | **fsdb**   |       **264.34 μs** |        **16.879 μs** |       **6.019 μs** |       **262.59 μs** |      **-** |    **1008 B** |
| **OrderBySecondaryRange**       | **mysql**  |        **51.46 μs** |         **0.454 μs** |       **0.162 μs** |        **51.39 μs** | **0.0610** |    **1007 B** |
| **FilterBySecondaryRange**      | **fsdb**   |       **195.37 μs** |        **12.901 μs** |       **3.350 μs** |       **194.38 μs** |      **-** |     **879 B** |
| UpdateBySecondaryRange      | fsdb   |       484.20 μs |       200.157 μs |      51.980 μs |       474.72 μs |      - |     849 B |
| **FilterBySecondaryRange**      | **mysql**  |        **46.15 μs** |         **1.010 μs** |       **0.262 μs** |        **46.09 μs** | **0.0610** |     **879 B** |
| UpdateBySecondaryRange      | mysql  |       121.58 μs |         7.557 μs |       2.695 μs |       121.21 μs |      - |     912 B |
