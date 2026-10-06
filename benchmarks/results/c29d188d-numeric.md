<!--
sha: c29d188d
date: 2026-10-06T16:36:16Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Read-only, native same-host snapshot. Command:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,LiteralNumericExpressions,ScalarLiteralSubqueries,TextNumericAggregates,PreparedNumericExpressions just bench-quick
```

The preceding [point-query repeat](c29d188d-baseline-1.md) measured 302.57 µs
for fsdb and 40.80 µs for MySQL. This run measured 256.29 µs and 41.38 µs,
respectively: differences of 15.3% and 1.4%, within the documented 20%
repeatability threshold. An [earlier matrix](c29d188d-numeric-unstable.md)
failed that check and is excluded from conclusions.

These are directional ShortRun measurements on an interactive machine. Some
numeric cases varied substantially between matrices; use the results to choose
a profiling target, not as evidence of a before/after regression. Prepared
numeric timing includes command creation, preparation, execution, and disposal.
TextNumericAggregates scans 10,000 users and performs three aggregates with
text conversion, including DISTINCT. Allocations describe the client process.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Target | Mean         | Error         | StdDev     | Gen0   | Allocated |
|--------------------------- |------- |-------------:|--------------:|-----------:|-------:|----------:|
| **LiteralNumericExpressions**  | **fsdb**   |    **184.05 μs** |    **530.029 μs** |  **29.053 μs** |      **-** |     **568 B** |
| ScalarLiteralSubqueries    | fsdb   |    308.58 μs |    183.382 μs |  10.052 μs |      - |     537 B |
| TextNumericAggregates      | fsdb   | 39,858.16 μs | 13,364.135 μs | 732.533 μs |      - |         - |
| PreparedNumericExpressions | fsdb   |    160.48 μs |     42.483 μs |   2.329 μs |      - |     984 B |
| **LiteralNumericExpressions**  | **mysql**  |     **35.49 μs** |     **36.341 μs** |   **1.992 μs** | **0.0610** |     **648 B** |
| ScalarLiteralSubqueries    | mysql  |     42.82 μs |     31.650 μs |   1.735 μs | 0.0610 |     536 B |
| TextNumericAggregates      | mysql  |  2,289.41 μs |    157.091 μs |   8.611 μs |      - |     524 B |
| PreparedNumericExpressions | mysql  |     26.70 μs |      3.298 μs |   0.181 μs | 0.0916 |     984 B |
| **PointSelectByPk**            | **fsdb**   |    **256.29 μs** |    **110.203 μs** |   **6.041 μs** |      **-** |     **880 B** |
| **PointSelectByPk**            | **mysql**  |     **41.38 μs** |     **44.051 μs** |   **2.415 μs** | **0.0610** |     **880 B** |
