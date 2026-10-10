<!--
sha: 87f34eeb
working-tree: direct integer-column format shortcut in Executor.fs
date: 2026-10-10T05:09:51Z
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
| **TextNumericAggregates** | **fsdb**   | **116,655.5 μs** | **27,356.68 μs** | **1,499.51 μs** |         **-** |
| SumAge                | fsdb   |   3,924.6 μs |  2,379.45 μs |   130.43 μs |     493 B |
| **TextNumericAggregates** | **mysql**  |   **2,250.1 μs** |     **64.13 μs** |     **3.52 μs** |     **524 B** |
| SumAge                | mysql  |     968.3 μs |     43.49 μs |     2.38 μs |     489 B |
