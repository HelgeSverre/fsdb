<!--
sha: 578482d6
date: 2026-11-05T12:19:21Z
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
| Method                                          | Target | Mean       | Error      | StdDev    | Allocated |
|------------------------------------------------ |------- |-----------:|-----------:|----------:|----------:|
| **CorrelatedNestedScalarNoSourceFiltered**          | **fsdb**   | **6,063.0 μs** | **2,036.7 μs** | **111.64 μs** |     **835 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | fsdb   | 6,207.6 μs | 2,284.4 μs | 125.21 μs |     843 B |
| **CorrelatedNestedScalarNoSourceFiltered**          | **mysql**  |   **317.7 μs** |   **420.4 μs** |  **23.04 μs** |     **824 B** |
| CorrelatedNestedScalarNoSourceFilteredTwoScopes | mysql  |   327.2 μs |   177.1 μs |   9.71 μs |     832 B |
