<!--
sha: 386f5d95
variant: original Value.coerceLeadingDouble from HEAD
date: 2026-10-10T05:26:43Z
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
| Method                | Target | Mean         | Error        | StdDev      | Allocated |
|---------------------- |------- |-------------:|-------------:|------------:|----------:|
| **TextNumericAggregates** | **fsdb**   | **108,493.3 μs** | **25,005.57 μs** | **1,370.64 μs** |         **-** |
| SumAge                | fsdb   |   3,898.4 μs |  4,157.39 μs |   227.88 μs |     493 B |
| SumCastAgeAsChar      | fsdb   |  31,004.6 μs | 61,670.46 μs | 3,380.37 μs |     573 B |
| **TextNumericAggregates** | **mysql**  |   **2,290.5 μs** |    **179.05 μs** |     **9.81 μs** |     **524 B** |
| SumAge                | mysql  |     970.7 μs |     30.49 μs |     1.67 μs |     489 B |
| SumCastAgeAsChar      | mysql  |   1,149.5 μs |    172.04 μs |     9.43 μs |     490 B |
