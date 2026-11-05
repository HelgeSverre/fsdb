<!--
sha: 69f39a72
date: 2026-11-05T11:46:52Z
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
| **CorrelatedScalarNoSourceFiltered**       | **fsdb**   |   **690.44 μs** |   **869.08 μs** |  **47.637 μs** |     **504 B** |
| CorrelatedNestedScalarNoSourceFiltered | fsdb   | 6,958.91 μs | 2,002.01 μs | 109.737 μs |     835 B |
| **CorrelatedScalarNoSourceFiltered**       | **mysql**  |    **93.41 μs** |    **26.81 μs** |   **1.470 μs** |     **504 B** |
| CorrelatedNestedScalarNoSourceFiltered | mysql  |   324.92 μs |    57.06 μs |   3.127 μs |     825 B |
