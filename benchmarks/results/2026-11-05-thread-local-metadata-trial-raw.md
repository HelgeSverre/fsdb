<!--
sha: ce0210b7
date: 2026-11-05T12:10:15Z
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
| Method                                          | Target | Mean        | Error       | StdDev      | Allocated |
|------------------------------------------------ |------- |------------:|------------:|------------:|----------:|
| **CorrelatedNestedScalarNoSourceFiltered**          | **fsdb**   | **10,213.6 μs** | **21,732.0 μs** | **1,191.21 μs** |     **826 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | fsdb   |  6,541.7 μs | 24,301.2 μs | 1,332.03 μs |     832 B |
| **CorrelatedNestedScalarNoSourceFiltered**          | **mysql**  |    **568.0 μs** |  **2,878.8 μs** |   **157.80 μs** |     **824 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | mysql  |    336.2 μs |    163.1 μs |     8.94 μs |     832 B |
