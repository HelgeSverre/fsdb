# Correlated query performance after warning fixes

2026-11-08. Compared release baseline `eef1ef37` (Fsdb 0.1.3) with
`4d6b49d2` (correlated COUNT conversion diagnostics). The objective was to
detect a material performance regression in the planner paths touched by the
warning fix. Both revisions ran `just bench` with
`FSDB_BENCH_METHODS=PointSelectByPk,CorrelatedPhysicalExists,CorrelatedPhysicalEquality,CorrelatedOrderCount`.
BenchmarkDotNet used three warmups and six measured iterations per method.
Hardware was an Apple M2 Max with .NET SDK 10.0.401; the data set held 10,000
users, 50,000 orders, and 10,000 articles. FSDB used its in-memory target;
the MySQL target was durable MySQL 8.4.11. Runs were sequential on the same
machine, not interleaved.

| FSDB method | Baseline run 2 | Changed run 2 | Change |
| --- | ---: | ---: | ---: |
| PointSelectByPk control | 351.29 μs | 349.31 μs | -0.6% |
| CorrelatedPhysicalExists | 672.72 μs | 638.47 μs | -5.1% |
| CorrelatedPhysicalEquality | 799.69 μs | 761.88 μs | -4.7% |
| CorrelatedOrderCount | 576.08 μs | 622.04 μs | +8.0% |

The first complete runs were much slower on both revisions. For example,
PointSelectByPk was 1,113.39 μs at baseline and 1,306.64 μs after the change,
versus about 350 μs in both repeat runs. This large control drift makes the
first-run deltas unsuitable for a regression claim. The repeat runs agree on
the control and do not show a broad correlated-query slowdown. The 8% increase
in CorrelatedOrderCount is worth watching, but one stable pair of sequential
runs cannot separate a small code effect from run-order or machine effects.

These benchmarks exercise broad query paths, not the exact malformed numeric
literal that triggers the new warning ledger. They support a limited conclusion:
no material regression was detected in these four methods. They do not prove
that every correlated COUNT shape is unchanged. The focused parity tests and
the full 3,946-test suite are the behavioral evidence.

Raw reports: [baseline run 1](eef1ef37-correlated-warning-run1.md),
[changed run 1](4d6b49d2-correlated-warning-run1.md),
[baseline run 2](eef1ef37-correlated-warning-baseline.md), and
[changed run 2](4d6b49d2-correlated-warning.md).
