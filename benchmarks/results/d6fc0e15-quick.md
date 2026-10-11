<!--
sha: d6fc0e15
date: 2026-10-11T02:39:10Z
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
| Method                | Target | Mean       | Error     | StdDev  | Allocated |
|---------------------- |------- |-----------:|----------:|--------:|----------:|
| **FullTextNaturalSearch** | **fsdb**   |   **898.8 μs** |  **65.71 μs** | **3.60 μs** |     **507 B** |
| FullTextBooleanSearch | fsdb   |   981.3 μs |  83.82 μs | 4.59 μs |     505 B |
| **FullTextNaturalSearch** | **mysql**  |   **416.4 μs** | **119.07 μs** | **6.53 μs** |     **505 B** |
| FullTextBooleanSearch | mysql  | 1,087.1 μs | 148.73 μs | 8.15 μs |     506 B |
