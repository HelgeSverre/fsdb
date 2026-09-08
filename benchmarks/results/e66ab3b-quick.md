<!--
sha: e66ab3b
date: 2026-09-08T23:41:51Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.400
  [Host]   : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.11 (10.0.1126.37416), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                                  | Target | Mean       | Error       | StdDev     | Allocated |
|---------------------------------------- |------- |-----------:|------------:|-----------:|----------:|
| **CorrelatedDerivedMixedCompositeEquality** | **fsdb**   |   **1.589 ms** |   **0.7035 ms** |  **0.0386 ms** |     **849 B** |
| **CorrelatedDerivedMixedCompositeEquality** | **mysql**  | **863.443 ms** | **269.7307 ms** | **14.7848 ms** |         **-** |
