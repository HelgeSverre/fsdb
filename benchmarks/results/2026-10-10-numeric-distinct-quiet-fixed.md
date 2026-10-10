<!--
sha: 3378e71d
variant: DISTINCT numeric conversion suppresses only its own warnings
date: 2026-10-10T05:42:29Z
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
| Method                   | Target | Mean      | Error      | StdDev    | Allocated |
|------------------------- |------- |----------:|-----------:|----------:|----------:|
| **TextNumericAggregates**    | **fsdb**   | **38.059 ms** | **10.2120 ms** | **0.5598 ms** |     **617 B** |
| SumCastAgeAsChar         | fsdb   | 13.250 ms | 17.0186 ms | 0.9328 ms |     509 B |
| SumDistinctCastAgeAsChar | fsdb   | 14.535 ms | 31.0856 ms | 1.7039 ms |     530 B |
| **TextNumericAggregates**    | **mysql**  |  **2.266 ms** |  **0.0229 ms** | **0.0013 ms** |     **524 B** |
| SumCastAgeAsChar         | mysql  |  1.148 ms |  0.0881 ms | 0.0048 ms |     490 B |
| SumDistinctCastAgeAsChar | mysql  |  1.601 ms |  0.1904 ms | 0.0104 ms |     490 B |
