<!--
sha: 2634b856
date: 2026-11-04T17:22:59Z
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
| Method                             | Target | Mean       | Error       | StdDev    | Allocated |
|----------------------------------- |------- |-----------:|------------:|----------:|----------:|
| **CorrelatedPhysicalExistsColumn**     | **fsdb**   |   **740.3 μs** |   **156.85 μs** |   **8.60 μs** |     **505 B** |
| CorrelatedPhysicalExistsExpression | fsdb   | 4,940.3 μs | 2,142.96 μs | 117.46 μs |     515 B |
| **CorrelatedPhysicalExistsColumn**     | **mysql**  |   **179.9 μs** |    **96.55 μs** |   **5.29 μs** |     **504 B** |
| CorrelatedPhysicalExistsExpression | mysql  |   177.6 μs |    11.35 μs |   0.62 μs |     504 B |
