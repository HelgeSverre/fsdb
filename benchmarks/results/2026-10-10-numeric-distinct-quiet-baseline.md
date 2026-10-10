<!--
sha: 3378e71d
variant: original per-value Diagnostics.suppress for DISTINCT numeric conversion
date: 2026-10-10T05:44:14Z
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
| **TextNumericAggregates**    | **fsdb**   | **53.789 ms** | **86.0724 ms** | **4.7179 ms** |         **-** |
| SumCastAgeAsChar         | fsdb   | 13.610 ms | 14.1100 ms | 0.7734 ms |     509 B |
| SumDistinctCastAgeAsChar | fsdb   | 31.326 ms | 64.7905 ms | 3.5514 ms |     573 B |
| **TextNumericAggregates**    | **mysql**  |  **2.289 ms** |  **0.0173 ms** | **0.0009 ms** |     **524 B** |
| SumCastAgeAsChar         | mysql  |  1.143 ms |  0.1479 ms | 0.0081 ms |     490 B |
| SumDistinctCastAgeAsChar | mysql  |  1.619 ms |  0.0674 ms | 0.0037 ms |     490 B |
