<!--
sha: af01260a
date: 2026-11-06T01:48:47Z
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
| Method                           | Target | Mean       | Error       | StdDev    | Allocated |
|--------------------------------- |------- |-----------:|------------:|----------:|----------:|
| **CorrelatedPhysicalExistsLimitOne** | **fsdb**   |   **922.3 μs** |   **197.68 μs** |  **10.84 μs** |     **507 B** |
| CorrelatedPhysicalExistsLimitTwo | fsdb   | 4,448.5 μs | 4,635.87 μs | 254.11 μs |     515 B |
| **CorrelatedPhysicalExistsLimitOne** | **mysql**  |   **189.4 μs** |    **60.05 μs** |   **3.29 μs** |     **504 B** |
| CorrelatedPhysicalExistsLimitTwo | mysql  |   171.0 μs |    11.87 μs |   0.65 μs |     504 B |
