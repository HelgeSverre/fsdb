<!--
sha: 2e36354
baseline: ca5d377
date: 2026-09-08T02:27:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Projected literal membership

`ProjectedLiteralIn` selects 100 identifiers through a pass-through derived
table. Before this change, the outer literal list was evaluated only after all
10,000 source rows had been projected.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 12.680 ms | 388.2 us | 389.0 us |
| MySQL 8.4 | 201.0 us | 198.6 us | 199.3 us |

Compatible scalar and composite row-value literal lists now resolve the
physical equality index before applying derived-table or CTE projection
steps. Duplicate tuples share a row-id union, partial-NULL tuples cannot add
true candidates, and the original predicate remains as a residual check.

The repeated current runs are roughly 33 times faster than the baseline and
about twice MySQL's time. BenchmarkDotNet ShortRun results are directional;
the stable row count and two independent runs establish that the full-source
materialization cliff is gone.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=ProjectedLiteralIn \
FSDB_BENCH_PORT=3408 \
FSDB_BENCH_MYSQL_PORT=3417 \
just _bench-run --quick
```
