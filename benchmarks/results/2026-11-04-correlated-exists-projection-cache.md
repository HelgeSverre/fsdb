<!--
sha: 6d6e7166
date: 2026-11-04T17:38:55Z
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
| Method                             | Target | Mean     | Error     | StdDev   | Allocated |
|----------------------------------- |------- |---------:|----------:|---------:|----------:|
| **CorrelatedPhysicalExistsColumn**     | **fsdb**   | **807.2 μs** | **462.04 μs** | **25.33 μs** |     **505 B** |
| CorrelatedPhysicalExistsExpression | fsdb   | 602.4 μs | 172.85 μs |  9.47 μs |     505 B |
| **CorrelatedPhysicalExistsColumn**     | **mysql**  | **177.1 μs** |  **23.33 μs** |  **1.28 μs** |     **504 B** |
| CorrelatedPhysicalExistsExpression | mysql  | 176.0 μs |   6.44 μs |  0.35 μs |     504 B |
