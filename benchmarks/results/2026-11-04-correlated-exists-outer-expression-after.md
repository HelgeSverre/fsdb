<!--
sha: 189480fd
date: 2026-11-04T18:21:55Z
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
| Method                                  | Target | Mean     | Error     | StdDev   | Allocated |
|---------------------------------------- |------- |---------:|----------:|---------:|----------:|
| **CorrelatedPhysicalExistsExpression**      | **fsdb**   | **582.8 μs** | **165.71 μs** |  **9.08 μs** |     **505 B** |
| CorrelatedPhysicalExistsOuterExpression | fsdb   | 641.0 μs | 332.82 μs | 18.24 μs |     504 B |
| **CorrelatedPhysicalExistsExpression**      | **mysql**  | **181.6 μs** |  **89.23 μs** |  **4.89 μs** |     **504 B** |
| CorrelatedPhysicalExistsOuterExpression | mysql  | 170.9 μs |  13.11 μs |  0.72 μs |     504 B |
