<!--
sha: 3b90d9b
date: 2026-08-25T04:28:23Z
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
| Method                 | Target | Mean         | Error        | StdDev      | Allocated |
|----------------------- |------- |-------------:|-------------:|------------:|----------:|
| **ViewAggregate**          | **fsdb**   |  **81,629.0 μs** | **90,178.85 μs** | **4,943.01 μs** |         **-** |
| **ViewAggregate**          | **mysql**  |  **19,333.1 μs** |    **665.87 μs** |    **36.50 μs** |     **634 B** |
| **WindowTopOrders**        | **fsdb**   | **356,196.5 μs** |  **5,104.39 μs** |   **279.79 μs** |         **-** |
| **WindowTopOrders**        | **mysql**  |  **50,909.4 μs** | **17,945.80 μs** |   **983.67 μs** |         **-** |
| **FullTextNaturalSearch**  | **fsdb**   |   **2,329.0 μs** |     **94.12 μs** |     **5.16 μs** |     **509 B** |
| **FullTextNaturalSearch**  | **mysql**  |     **402.3 μs** |     **42.00 μs** |     **2.30 μs** |     **505 B** |
| **JoinUsersOrders**        | **fsdb**   |   **6,138.3 μs** |    **521.41 μs** |    **28.58 μs** |     **819 B** |
| UncorrelatedInSubquery | fsdb   |     519.3 μs |     18.03 μs |     0.99 μs |     505 B |
| GroupByAggregate       | fsdb   |  79,074.8 μs | 54,726.34 μs | 2,999.74 μs |         - |
| **JoinUsersOrders**        | **mysql**  |     **171.0 μs** |     **11.62 μs** |     **0.64 μs** |     **808 B** |
| UncorrelatedInSubquery | mysql  |     151.5 μs |      8.60 μs |     0.47 μs |     584 B |
| GroupByAggregate       | mysql  |  19,279.4 μs |    575.80 μs |    31.56 μs |     634 B |
| **ReorderedIndexedJoin**   | **fsdb**   |   **4,062.6 μs** |    **112.31 μs** |     **6.16 μs** |     **827 B** |
| CorrelatedOrderCount   | fsdb   |     893.7 μs |     92.15 μs |     5.05 μs |     505 B |
| **ReorderedIndexedJoin**   | **mysql**  |     **114.6 μs** |      **2.15 μs** |     **0.12 μs** |     **816 B** |
| CorrelatedOrderCount   | mysql  |     201.8 μs |     23.90 μs |     1.31 μs |     504 B |
