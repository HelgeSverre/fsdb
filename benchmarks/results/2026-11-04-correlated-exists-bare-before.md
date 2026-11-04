<!--
sha: f124fbaf
date: 2026-11-04T17:00:04Z
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
| Method                             | Target | Mean       | Error     | StdDev   | Allocated |
|----------------------------------- |------- |-----------:|----------:|---------:|----------:|
| **CorrelatedPhysicalExistsColumn**     | **fsdb**   |   **776.1 μs** |  **88.40 μs** |  **4.85 μs** |     **505 B** |
| CorrelatedPhysicalExistsBareColumn | fsdb   | 4,233.4 μs | 898.03 μs | 49.22 μs |     515 B |
| **CorrelatedPhysicalExistsColumn**     | **mysql**  |   **181.9 μs** |  **57.14 μs** |  **3.13 μs** |     **504 B** |
| CorrelatedPhysicalExistsBareColumn | mysql  |   175.4 μs |  17.38 μs |  0.95 μs |     504 B |
