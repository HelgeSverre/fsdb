<!--
sha: 6933dde4
date: 2026-11-04T11:38:37Z
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
| Method                      | Target | Mean      | Error      | StdDev    | Allocated |
|---------------------------- |------- |----------:|-----------:|----------:|----------:|
| **CorrelatedDerivedEquality**   | **fsdb**   | **640.26 μs** | **322.452 μs** | **17.675 μs** |     **777 B** |
| CorrelatedEqualityOuterOnly | fsdb   | 429.79 μs | 507.523 μs | 27.819 μs |     505 B |
| CorrelatedPhysicalEquality  | fsdb   | 649.39 μs | 278.492 μs | 15.265 μs |     729 B |
| **CorrelatedDerivedEquality**   | **mysql**  | **219.13 μs** |   **3.441 μs** |  **0.189 μs** |     **776 B** |
| CorrelatedEqualityOuterOnly | mysql  |  80.82 μs |  58.136 μs |  3.187 μs |     504 B |
| CorrelatedPhysicalEquality  | mysql  | 214.88 μs |  23.351 μs |  1.280 μs |     728 B |
