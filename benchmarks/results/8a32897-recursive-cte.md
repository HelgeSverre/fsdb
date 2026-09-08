<!--
sha: 8a32897
baseline: de96bc6
date: 2026-09-08T03:01:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Prepared recursive CTE steps

`RecursiveCte100` generates a one-column series through 100 recursive passes,
then returns its count and sum. Previously, every pass rebuilt the full SELECT
planning, metadata, and result-rendering pipeline even though the member shape
was invariant.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 734.5 us | 229.2 us | 240.4 us |
| MySQL 8.4 | 53.6 us | 53.8 us | 53.9 us |

Simple single-recursive-source members now prepare their row predicate,
projection, and stored-value coercions once. Each pass executes that prepared
step directly, while joins, grouping, windows, full-text, locking, and other
complex members retain the general SELECT path. Recursive UNION DISTINCT still
uses its equality set, and nested subqueries retain the recursive scope.

The repeated current runs are about three times faster than the baseline and
roughly 4.4 times MySQL's time. BenchmarkDotNet ShortRun results are
directional, but the independent runs agree on both the improvement and the
remaining constant-factor difference.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=RecursiveCte100 \
FSDB_BENCH_PORT=3408 \
FSDB_BENCH_MYSQL_PORT=3417 \
just _bench-run --quick
```
