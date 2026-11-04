<!--
sha: 2ebf0d0a
date: 2026-11-04T11:48:18Z
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
| Method                            | Target | Mean      | Error     | StdDev    | Allocated |
|---------------------------------- |------- |----------:|----------:|----------:|----------:|
| **CorrelatedComputedDerivedEquality** | **fsdb**   |  **3.706 ms** | **0.6649 ms** | **0.0364 ms** |     **843 B** |
| **CorrelatedComputedDerivedEquality** | **mysql**  | **12.613 ms** | **0.3391 ms** | **0.0186 ms** |     **849 B** |
