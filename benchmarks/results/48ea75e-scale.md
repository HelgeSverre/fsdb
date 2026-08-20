<!--
sha: 48ea75e
date: 2026-08-20T21:41:22Z
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
  Job-FCBOEA : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

IterationCount=6  WarmupCount=3

```
| Method                | Target | Mean            | Error          | StdDev         | Gen0   | Allocated |
|---------------------- |------- |----------------:|---------------:|---------------:|-------:|----------:|
| **WindowTopOrders**       | **fsdb**   | **4,816,365.55 μs** | **554,891.591 μs** | **197,879.686 μs** |      **-** |         **-** |
| FullTextBooleanSearch | fsdb   |   305,118.86 μs |  45,881.680 μs |  16,361.849 μs |      - |         - |
| **WindowTopOrders**       | **mysql**  |   **758,343.31 μs** | **125,332.412 μs** |  **44,694.745 μs** |      **-** |         **-** |
| FullTextBooleanSearch | mysql  |    11,869.39 μs |     405.958 μs |     144.768 μs |      - |     521 B |
| **PointSelectByPk**       | **fsdb**   |       **161.94 μs** |      **22.250 μs** |       **7.934 μs** |      **-** |     **880 B** |
| FilterScanOrderLimit  | fsdb   |   118,290.16 μs |   3,893.374 μs |   1,011.097 μs |      - |         - |
| InsertSingle          | fsdb   |        73.79 μs |       6.830 μs |       2.436 μs | 0.1221 |    1240 B |
| ReplaceExistingByPk   | fsdb   |        77.24 μs |       8.586 μs |       2.230 μs | 0.1221 |    1032 B |
| UpdateSingleRow       | fsdb   |        93.83 μs |       1.657 μs |       0.430 μs |      - |     680 B |
| JoinUsersOrders       | fsdb   |    87,657.77 μs |  51,915.440 μs |  18,513.546 μs |      - |         - |
| GroupByAggregate      | fsdb   | 1,142,447.96 μs | 209,316.603 μs |  54,358.857 μs |      - |         - |
| JsonExtract           | fsdb   |       184.74 μs |       6.684 μs |       2.383 μs |      - |     504 B |
| UpdateByNonIndexed    | fsdb   |    99,121.41 μs |  23,092.470 μs |   8,234.997 μs |      - |         - |
| **PointSelectByPk**       | **mysql**  |        **41.89 μs** |       **0.524 μs** |       **0.136 μs** | **0.0610** |     **880 B** |
| FilterScanOrderLimit  | mysql  |    18,665.50 μs |     337.597 μs |      87.673 μs |      - |     674 B |
| InsertSingle          | mysql  |       113.10 μs |       3.738 μs |       1.333 μs | 0.1221 |    1242 B |
| ReplaceExistingByPk   | mysql  |       135.97 μs |      25.095 μs |       8.949 μs |      - |    1032 B |
| UpdateSingleRow       | mysql  |       115.33 μs |       3.989 μs |       1.422 μs |      - |     746 B |
| JoinUsersOrders       | mysql  |       272.73 μs |      36.488 μs |      13.012 μs |      - |     889 B |
| GroupByAggregate      | mysql  |   203,641.23 μs |   5,390.958 μs |   1,400.015 μs |      - |         - |
| JsonExtract           | mysql  |        65.20 μs |       3.606 μs |       1.286 μs |      - |     584 B |
| UpdateByNonIndexed    | mysql  |    33,533.58 μs |     438.537 μs |     156.387 μs |      - |     851 B |
