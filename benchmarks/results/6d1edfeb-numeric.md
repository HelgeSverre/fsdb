<!--
sha: 6d1edfeb
date: 2026-10-06T20:54:30Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->


Status: inconclusive for regression or before/after claims. The subsequent
[point-lookup control](6d1edfeb-control.md) changed fsdb from 799.44 to
560.71 µs (29.9% lower), exceeding the documented 20% repeatability threshold.
MySQL changed from 91.84 to 94.23 µs (2.6% higher). The matrix is retained as
raw evidence and excluded from performance conclusions.

The current snapshot observes 328.73 µs for literal numeric expressions,
651.62 µs for scalar subqueries, 1,223.69 µs for prepared numeric expressions,
and 99.94 ms for text numeric aggregates over 10,000 users. Aggregate timing
has a 33.63 ms standard deviation; prepared timing has a 243.01 µs standard
deviation. These results cannot establish a regression from the
[previous numeric snapshot](2ae5c37f-numeric.md), which also failed repeatability.
Numeric aggregates remain a useful candidate for an isolated profiling run.

Native same-host read-only ShortRun on an interactive Apple M2 Max, with no
processor-count override on benchmark processes. Release compilation used four
processors before measurement. Prepared timing includes command creation,
preparation, execution, and disposal. Text aggregation includes three aggregates
with DISTINCT. Allocations describe the client process. In-memory fsdb versus
durable MySQL supports read-path observations only, not write parity.
BenchmarkDotNet could not raise process priority; both targets used normal
priority. No correctness suite or compilation ran alongside measurement.

Commands:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,LiteralNumericExpressions,ScalarLiteralSubqueries,TextNumericAggregates,PreparedNumericExpressions just bench-quick
FSDB_BENCH_METHODS=PointSelectByPk just bench-quick
```

Both runs completed and their servers were stopped. Revision `6d1edfeb`
includes scientific literal spelling, approximate descriptors, binary precision,
and related integer metadata changes. The native MySQL 8.4.11 oracle and
3,353 differential steps across 38 cases passed before benchmarking.
The final root gate built without warnings and passed 2,827 of 2,828 tests;
the existing lookup timing-ratio test failed during the full suite and passed
when run alone. No threshold or test exclusion was changed.

```

BenchmarkDotNet v0.14.0, macOS Sequoia 15.6 (24G84) [Darwin 24.6.0]
Apple M2 Max, 1 CPU, 12 logical and 12 physical cores
.NET SDK 10.0.401
  [Host]   : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD
  ShortRun : .NET 10.0.12 (10.0.1226.42308), Arm64 RyuJIT AdvSIMD

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method                     | Target | Mean         | Error         | StdDev        | Gen0   | Allocated |
|--------------------------- |------- |-------------:|--------------:|--------------:|-------:|----------:|
| **LiteralNumericExpressions**  | **fsdb**   |    **328.73 μs** |     **838.97 μs** |     **45.987 μs** |      **-** |     **568 B** |
| ScalarLiteralSubqueries    | fsdb   |    651.62 μs |   1,207.67 μs |     66.196 μs |      - |     536 B |
| TextNumericAggregates      | fsdb   | 99,939.85 μs | 613,562.66 μs | 33,631.440 μs |      - |         - |
| PreparedNumericExpressions | fsdb   |  1,223.69 μs |   4,433.37 μs |    243.008 μs |      - |     986 B |
| **LiteralNumericExpressions**  | **mysql**  |     **82.90 μs** |     **481.13 μs** |     **26.372 μs** |      **-** |     **648 B** |
| ScalarLiteralSubqueries    | mysql  |     79.50 μs |      57.25 μs |      3.138 μs |      - |     536 B |
| TextNumericAggregates      | mysql  |  3,580.98 μs |   7,817.21 μs |    428.488 μs |      - |     524 B |
| PreparedNumericExpressions | mysql  |     59.57 μs |      76.87 μs |      4.213 μs | 0.0610 |     984 B |
| **PointSelectByPk**            | **fsdb**   |    **799.44 μs** |   **4,972.37 μs** |    **272.552 μs** |      **-** |     **883 B** |
| **PointSelectByPk**            | **mysql**  |     **91.84 μs** |      **53.83 μs** |      **2.951 μs** |      **-** |     **880 B** |
