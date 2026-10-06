<!--
sha: 2ae5c37f
date: 2026-10-06T19:56:19Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

Status: inconclusive for before/after performance. The point-query control
changed from 512.38 to 600.06 µs for fsdb (17.1%) and 77.93 to 165.97 µs
for MySQL (113.0%). MySQL exceeds the documented 20% repeatability threshold;
this matrix is excluded from performance conclusions.

The [first matrix](2ae5c37f-numeric-unstable.md) also failed repeatability.
The intervening [control run](2ae5c37f-control.md) and both raw matrices are
retained. Compared with the [earlier snapshot](c29d188d-numeric.md), both
servers and the point-lookup control are slower. These observations cannot
isolate the effect of the intervening compatibility changes.

Raw fsdb means across the two matrices were 316–412 µs for literal numeric
expressions, 350–399 µs for scalar subqueries, 285–290 µs for prepared
numeric expressions, and 45.2–48.5 ms for text numeric aggregates. The
aggregate workload remains a candidate for profiling, but these runs do not
establish a regression or a reliable fsdb/MySQL ratio.

Native same-host read-only ShortRun on an interactive Apple M2 Max. No
processor-count override was applied to the benchmark processes. The schema
and dataset match the earlier snapshot. Prepared timing includes command
creation, preparation, execution, and disposal; text numeric aggregation
scans 10,000 users with three aggregates including DISTINCT. Allocation
measurements describe the client process. In-memory fsdb versus durable
MySQL is suitable here only for read-path observations, not write parity.

Command:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,LiteralNumericExpressions,ScalarLiteralSubqueries,TextNumericAggregates,PreparedNumericExpressions just bench-quick
```

Both matrix runs and the control completed successfully. Benchmark servers
were stopped afterward. Correctness validation preceding the measurements:
`just check` passed all 2,822 tests; the native MySQL 8.4.11 maintained oracle
passed; compatibility contracts passed 3,205 steps across 36 cases with no
differences. The root check used `DOTNET_PROCESSOR_COUNT=4`.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1
WarmupCount=3

```
| Method                     | Target | Mean         | Error       | StdDev     | Allocated |
|--------------------------- |------- |-------------:|------------:|-----------:|----------:|
| **LiteralNumericExpressions**  | **fsdb**   |    **411.83 μs** |    **75.11 μs** |   **4.117 μs** |     **569 B** |
| ScalarLiteralSubqueries    | fsdb   |    350.18 μs |    50.39 μs |   2.762 μs |     537 B |
| TextNumericAggregates      | fsdb   | 45,201.61 μs | 6,953.84 μs | 381.163 μs |         - |
| PreparedNumericExpressions | fsdb   |    290.35 μs |    53.89 μs |   2.954 μs |     984 B |
| **LiteralNumericExpressions**  | **mysql**  |     **76.74 μs** |   **107.85 μs** |   **5.912 μs** |     **648 B** |
| ScalarLiteralSubqueries    | mysql  |    167.74 μs |   285.41 μs |  15.644 μs |     538 B |
| TextNumericAggregates      | mysql  |  6,985.93 μs | 2,272.40 μs | 124.558 μs |     526 B |
| PreparedNumericExpressions | mysql  |     65.78 μs |   102.81 μs |   5.635 μs |     984 B |
| **PointSelectByPk**            | **fsdb**   |    **600.06 μs** |   **559.99 μs** |  **30.695 μs** |     **881 B** |
| **PointSelectByPk**            | **mysql**  |    **165.97 μs** |   **274.13 μs** |  **15.026 μs** |     **880 B** |
