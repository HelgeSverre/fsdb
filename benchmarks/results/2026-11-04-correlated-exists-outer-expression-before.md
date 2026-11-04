<!--
sha: 189480fd
date: 2026-11-04T18:16:41Z
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
| Method                                  | Target | Mean       | Error     | StdDev   | Allocated |
|---------------------------------------- |------- |-----------:|----------:|---------:|----------:|
| **CorrelatedPhysicalExistsColumn**          | **fsdb**   |   **600.4 μs** |  **81.31 μs** |  **4.46 μs** |     **505 B** |
| CorrelatedPhysicalExistsExpression      | fsdb   |   627.0 μs | 214.23 μs | 11.74 μs |     506 B |
| CorrelatedPhysicalExistsOuterExpression | fsdb   | 1,129.5 μs |  59.32 μs |  3.25 μs |     507 B |
| **CorrelatedPhysicalExistsColumn**          | **mysql**  |   **177.2 μs** |  **44.80 μs** |  **2.46 μs** |     **504 B** |
| CorrelatedPhysicalExistsExpression      | mysql  |   178.5 μs | 107.56 μs |  5.90 μs |     504 B |
| CorrelatedPhysicalExistsOuterExpression | mysql  |   173.2 μs |  87.95 μs |  4.82 μs |     504 B |
