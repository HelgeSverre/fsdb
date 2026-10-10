<!--
sha: 87f34eeb
date: 2026-10-10T05:02:44Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                   | Target | Mean         | Error        | StdDev      | Allocated |
|------------------------- |------- |-------------:|-------------:|------------:|----------:|
| **TextNumericAggregates**    | **fsdb**   | **166,318.5 μs** | **18,284.97 μs** | **1,002.26 μs** |         **-** |
| CountUsers               | fsdb   |     250.1 μs |     58.84 μs |     3.23 μs |     489 B |
| SumAge                   | fsdb   |   3,731.2 μs |  8,119.64 μs |   445.06 μs |     499 B |
| SumCastAgeAsChar         | fsdb   |  51,415.4 μs | 19,340.98 μs | 1,060.14 μs |         - |
| AvgCastAgeAsChar         | fsdb   |  49,083.9 μs |  7,780.97 μs |   426.50 μs |         - |
| SumDistinctCastAgeAsChar | fsdb   |  68,526.2 μs |  6,137.70 μs |   336.43 μs |         - |
| **TextNumericAggregates**    | **mysql**  |   **2,288.5 μs** |    **318.39 μs** |    **17.45 μs** |     **524 B** |
| CountUsers               | mysql  |     762.2 μs |      6.78 μs |     0.37 μs |     489 B |
| SumAge                   | mysql  |     972.1 μs |    123.18 μs |     6.75 μs |     489 B |
| SumCastAgeAsChar         | mysql  |   1,151.0 μs |     36.11 μs |     1.98 μs |     490 B |
| AvgCastAgeAsChar         | mysql  |   1,172.3 μs |    248.36 μs |    13.61 μs |     490 B |
| SumDistinctCastAgeAsChar | mysql  |   1,611.9 μs |    185.81 μs |    10.18 μs |     490 B |
