<!--
sha: ddafec8
date: 2026-09-08T01:11:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated outer expressions

`CorrelatedOuterExpression` returns correlated order counts for 100 users. Its
inner equality uses `orders.user_id = users.id + 0`, so the outer probe key is
a deterministic expression rather than a bare column.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 1.903 s | 481.9 us | 490.5 us |
| MySQL 8.4 | 260.4 us | 259.4 us | 264.0 us |

fsdb now recognizes scalar expressions whose column dependencies are all
explicitly qualified outer references and whose functions are deterministic.
Existing exact-coercion checks decide whether an index probe is safe; an
inexact value falls back to the ordinary scan. Effectful, variable-bearing,
bare-column, subquery, full-text, and window expressions remain unhoisted.

The repeated current runs are roughly 3,900 times faster than the baseline.
They retain a roughly 1.9-times constant-factor gap to MySQL. BenchmarkDotNet
ShortRun results are directional, but the several-orders-of-magnitude change
is unambiguous.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=CorrelatedOuterExpression \
FSDB_BENCH_PORT=3407 \
FSDB_BENCH_MYSQL_PORT=3416 \
just _bench-run --quick
```
