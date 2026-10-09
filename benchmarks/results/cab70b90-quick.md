<!--
sha: cab70b90
date: 2026-10-09T13:26:39Z
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
| Method             | Target | Mean     | Error    | StdDev   | Allocated |
|------------------- |------- |---------:|---------:|---------:|----------:|
| **UpsertExistingByPk** | **fsdb**   | **484.9 μs** | **716.1 μs** | **39.25 μs** |   **1.47 KB** |
| **UpsertExistingByPk** | **mysql**  | **170.9 μs** | **308.0 μs** | **16.88 μs** |   **1.47 KB** |
