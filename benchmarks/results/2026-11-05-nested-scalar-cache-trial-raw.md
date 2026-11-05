<!--
sha: 69f39a72
date: 2026-11-05T11:50:50Z
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
| Method                                 | Target | Mean        | Error       | StdDev     | Allocated |
|--------------------------------------- |------- |------------:|------------:|-----------:|----------:|
| **CorrelatedScalarNoSourceFiltered**       | **fsdb**   |   **682.42 μs** |   **363.94 μs** |  **19.949 μs** |     **505 B** |
| CorrelatedNestedScalarNoSourceFiltered | fsdb   | 7,075.13 μs | 2,846.12 μs | 156.005 μs |     835 B |
| **CorrelatedScalarNoSourceFiltered**       | **mysql**  |    **91.44 μs** |   **109.45 μs** |   **6.000 μs** |     **504 B** |
| CorrelatedNestedScalarNoSourceFiltered | mysql  |   376.58 μs |   160.61 μs |   8.804 μs |     824 B |
