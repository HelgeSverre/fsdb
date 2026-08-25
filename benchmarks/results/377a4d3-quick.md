<!--
sha: 377a4d3
date: 2026-08-25T11:31:00Z
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
| Method                 | Target | Mean          | Error         | StdDev       | Allocated |
|----------------------- |------- |--------------:|--------------:|-------------:|----------:|
| **WindowTopOrders**        | **fsdb**   | **354,299.81 μs** | **146,918.79 μs** | **8,053.115 μs** |         **-** |
| **WindowTopOrders**        | **mysql**  |  **49,288.28 μs** |  **32,129.91 μs** | **1,761.149 μs** |         **-** |
| **JoinUsersOrders**        | **fsdb**   |     **370.72 μs** |     **537.58 μs** |    **29.467 μs** |     **809 B** |
| UncorrelatedInSubquery | fsdb   |     534.13 μs |     188.01 μs |    10.305 μs |     505 B |
| GroupByAggregate       | fsdb   |  98,224.34 μs |  60,719.42 μs | 3,328.236 μs |         - |
| JsonExtract            | fsdb   |     231.46 μs |     108.42 μs |     5.943 μs |     505 B |
| **JoinUsersOrders**        | **mysql**  |     **178.46 μs** |      **36.34 μs** |     **1.992 μs** |     **808 B** |
| UncorrelatedInSubquery | mysql  |     155.48 μs |      24.71 μs |     1.354 μs |     584 B |
| GroupByAggregate       | mysql  |  19,938.43 μs |   2,339.85 μs |   128.255 μs |     634 B |
| JsonExtract            | mysql  |      62.37 μs |      51.55 μs |     2.826 μs |     584 B |
