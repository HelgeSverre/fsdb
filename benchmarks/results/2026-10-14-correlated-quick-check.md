# Correlated equality planning: current quick check

This is a directional performance snapshot at `de493f5d`, after the joined-view
authorization work. It is not a before/after measurement of that change. The
three BenchmarkDotNet ShortRun passes used the same native host, MySQL 8.4.11,
.NET 10.0.401, 10,000 users, and 50,000 orders. FSDB was in memory and MySQL
used its normal durable configuration; these read-query timings do not compare
write durability. Each pass included `PointSelectByPk` as a noise control.

| Query (mean) | FSDB run 1 | FSDB run 2 | FSDB run 3 | MySQL run 1 | MySQL run 2 | MySQL run 3 |
|---|---:|---:|---:|---:|---:|---:|
| PointSelectByPk | 464 µs | 409 µs | 372 µs | 58.8 µs | 43.6 µs | 44.6 µs |
| CorrelatedDerivedEquality | 1,301 µs | 908 µs | 949 µs | 223 µs | 220 µs | 221 µs |
| CorrelatedCteEquality | 1,143 µs | 1,003 µs | 814 µs | 243 µs | 220 µs | 216 µs |

The MySQL control changed by 26% from pass 1 to pass 2, beyond the harness's
20% noise threshold, so pass 1 is excluded from interpretation. Between passes
2 and 3, the FSDB control changed by 9% and the MySQL control by 2%. In that
pair, the derived equality query took about 4.1–4.3 times MySQL's time. The
CTE form took about 3.8–4.6 times MySQL's time and varied more on FSDB.
Point lookups were also substantially slower on FSDB, so these whole-query
ratios do not isolate a correlated-planning cost. The runs reveal no new
full-scan cliff in these bounded shapes, but they do leave material per-query
overhead. A matched baseline and longer runs are needed before claiming a
regression or an optimization win.

Raw BenchmarkDotNet summaries: [run 1](de493f5d-correlated-run1.md),
[run 2](de493f5d-correlated-run2.md), and
[run 3](de493f5d-correlated-run3.md). Each contains the host, versions,
dataset, target modes, three measured iterations, and allocation estimates.

Command for each pass:

```sh
FSDB_BENCH_METHODS=PointSelectByPk,CorrelatedDerivedEquality,CorrelatedCteEquality just bench-quick
```
