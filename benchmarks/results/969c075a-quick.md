<!--
sha: 969c075a
date: 2026-11-04T12:20:27Z
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
| Method                            | Target | Mean        | Error       | StdDev    | Allocated |
|---------------------------------- |------- |------------:|------------:|----------:|----------:|
| **CorrelatedComputedDerivedEquality** | **fsdb**   |  **3,363.7 μs** | **3,274.56 μs** | **179.49 μs** |     **837 B** |
| CorrelatedCteEquality             | fsdb   |    678.2 μs | 1,070.75 μs |  58.69 μs |     841 B |
| **CorrelatedComputedDerivedEquality** | **mysql**  | **12,922.3 μs** | **9,271.22 μs** | **508.19 μs** |     **849 B** |
| CorrelatedCteEquality             | mysql  |    207.9 μs |    20.04 μs |   1.10 μs |     840 B |
