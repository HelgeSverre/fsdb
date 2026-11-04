<!--
sha: b7c1312f
date: 2026-11-04T16:05:46Z
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
| Method                      | Target | Mean      | Error     | StdDev    | Allocated |
|---------------------------- |------- |----------:|----------:|----------:|----------:|
| **CorrelatedEqualityOuterOnly** | **fsdb**   | **404.72 μs** |  **95.44 μs** |  **5.231 μs** |     **505 B** |
| CorrelatedScalarNoSource    | fsdb   | 576.58 μs | 545.42 μs | 29.897 μs |     505 B |
| **CorrelatedEqualityOuterOnly** | **mysql**  |  **76.76 μs** |  **49.57 μs** |  **2.717 μs** |     **504 B** |
| CorrelatedScalarNoSource    | mysql  |  83.91 μs | 122.95 μs |  6.739 μs |     504 B |
