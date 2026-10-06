<!--
sha: 2ae5c37f
date: 2026-10-06T19:52:11Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Excluded from performance conclusions: the subsequent [control repeat](2ae5c37f-control.md)
changed PointSelectByPk from 576.07 to 512.38 µs for fsdb (11.1%) and
119.90 to 77.93 µs for MySQL (35.0%). MySQL exceeds the documented 20%
repeatability threshold. Raw observations are retained for transparency.

Command: `FSDB_BENCH_METHODS=PointSelectByPk,LiteralNumericExpressions,ScalarLiteralSubqueries,TextNumericAggregates,PreparedNumericExpressions just bench-quick`.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                     | Target | Mean         | Error         | StdDev     | Allocated |
|--------------------------- |------- |-------------:|--------------:|-----------:|----------:|
| **LiteralNumericExpressions**  | **fsdb**   |    **315.61 μs** |     **55.703 μs** |   **3.053 μs** |     **568 B** |
| ScalarLiteralSubqueries    | fsdb   |    399.23 μs |    923.775 μs |  50.635 μs |     536 B |
| TextNumericAggregates      | fsdb   | 48,460.81 μs |  8,683.655 μs | 475.980 μs |         - |
| PreparedNumericExpressions | fsdb   |    284.94 μs |     19.582 μs |   1.073 μs |     985 B |
| **LiteralNumericExpressions**  | **mysql**  |     **79.31 μs** |      **3.825 μs** |   **0.210 μs** |     **648 B** |
| ScalarLiteralSubqueries    | mysql  |    170.80 μs |  1,238.075 μs |  67.863 μs |     536 B |
| TextNumericAggregates      | mysql  |  5,237.24 μs | 14,617.538 μs | 801.237 μs |     528 B |
| PreparedNumericExpressions | mysql  |     61.54 μs |     16.716 μs |   0.916 μs |     984 B |
| **PointSelectByPk**            | **fsdb**   |    **576.07 μs** |    **127.080 μs** |   **6.966 μs** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |    **119.90 μs** |    **427.021 μs** |  **23.406 μs** |     **880 B** |
