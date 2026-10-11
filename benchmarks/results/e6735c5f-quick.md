<!--
sha: e6735c5f
date: 2026-10-11T02:37:30Z
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
| Method                | Target | Mean       | Error     | StdDev   | Allocated |
|---------------------- |------- |-----------:|----------:|---------:|----------:|
| **FullTextNaturalSearch** | **fsdb**   |   **463.8 μs** | **100.93 μs** |  **5.53 μs** |     **505 B** |
| FullTextBooleanSearch | fsdb   |   864.8 μs | 183.18 μs | 10.04 μs |     505 B |
| **FullTextNaturalSearch** | **mysql**  |   **418.6 μs** |  **51.40 μs** |  **2.82 μs** |     **505 B** |
| FullTextBooleanSearch | mysql  | 1,143.8 μs | 137.04 μs |  7.51 μs |     506 B |
