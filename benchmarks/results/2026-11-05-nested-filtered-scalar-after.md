<!--
sha: c67d0747
date: 2026-11-05T10:55:52Z
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
| **CorrelatedScalarNoSourceFiltered**       | **fsdb**   |   **726.03 μs** |   **111.27 μs** |   **6.099 μs** |     **505 B** |
| CorrelatedNestedScalarNoSourceFiltered | fsdb   | 5,343.56 μs | 2,503.91 μs | 137.248 μs |     835 B |
| **CorrelatedScalarNoSourceFiltered**       | **mysql**  |    **88.95 μs** |    **10.14 μs** |   **0.556 μs** |     **504 B** |
| CorrelatedNestedScalarNoSourceFiltered | mysql  |   320.27 μs |   102.11 μs |   5.597 μs |     824 B |
