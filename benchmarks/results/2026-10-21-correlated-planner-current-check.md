# Correlated equality planner: current-head control check

Three BenchmarkDotNet ShortRun passes on `f8c3bf74` compared the maintained
point-key control with derived-table and CTE correlated equality queries. Each
pass used the native macOS host, .NET 10.0.12, 10,000 users, 50,000 orders,
and MySQL 8.4.11. Fsdb used its in-memory server; MySQL used the benchmark's
normal configuration. Each method had three warmups and three measured
iterations. This is a short-run diagnostic, not an A/B test of a code change.

| Mean latency | Run 1 fsdb | Run 1 MySQL | Run 2 fsdb | Run 2 MySQL | Run 3 fsdb | Run 3 MySQL |
|---|---:|---:|---:|---:|---:|---:|
| PointSelectByPk | 948 µs | 66 µs | 488 µs | 50 µs | 429 µs | 60 µs |
| CorrelatedDerivedEquality | 1,000 µs | 340 µs | 987 µs | 997 µs | 1,004 µs | 321 µs |
| CorrelatedCteEquality | 1,348 µs | 314 µs | 1,438 µs | 389 µs | 1,067 µs | 637 µs |

Fsdb's derived-equality samples were the stable result: 987–1,004 µs across
passes. Runs 1 and 3 put MySQL's corresponding query at 321–340 µs; its run
2 mean of 997 µs is inconsistent with both and cannot be used for a stable
engine ratio. The fsdb point control fell from a noisy 948 µs in run 1 to
429–488 µs in runs 2–3. CTE means varied substantially in both engines.
Those controls prevent attributing the whole derived/CTE latency gap to the
correlated planner. The derived result is also close to the
[October 14 check](2026-10-14-correlated-quick-check.md), where fsdb took
908–949 µs in the stable pair and the point control already lagged MySQL.

The current evidence shows no new correlated scan cliff in these bounded
queries and does not justify changing index-selection heuristics. A next
implementation round should isolate per-query setup/projection work or use a
correlated shape with measured candidate-row amplification, with a matched
point control and before/after runs. The complete ShortRun outputs are
[run 1](f8c3bf74-correlated-run1.md),
[run 2](f8c3bf74-correlated-run2.md), and
[run 3](f8c3bf74-correlated-run3.md).

Each pass ran:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,CorrelatedDerivedEquality,CorrelatedCteEquality just bench-quick
```
