<!--
sha: 49028123
date: 2026-11-05T12:24:01Z
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
| Method                                          | Target | Mean       | Error       | StdDev    | Allocated |
|------------------------------------------------ |------- |-----------:|------------:|----------:|----------:|
| **CorrelatedNestedScalarNoSourceFiltered**          | **fsdb**   | **6,864.7 μs** | **2,626.70 μs** | **143.98 μs** |     **832 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | fsdb   | 5,677.9 μs |   359.83 μs |  19.72 μs |     843 B |
| **CorrelatedNestedScalarNoSourceFiltered**          | **mysql**  |   **321.8 μs** |    **25.74 μs** |   **1.41 μs** |     **825 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | mysql  |   316.7 μs |    70.86 μs |   3.88 μs |     833 B |
