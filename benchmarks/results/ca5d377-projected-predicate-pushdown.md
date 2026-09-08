<!--
sha: ca5d377
baseline: ea70a23
date: 2026-09-08T01:49:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Projected predicate pushdown

`CorrelatedBareOuterColumn` selects 100 rows from a pass-through derived users
table, then performs an indexed correlated count. Before this change the
correlated lookup was already indexed, but the derived table still projected
all 10,000 users before applying its outer range predicate.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 13.599 ms | 499.8 us | 497.7 us |
| MySQL 8.4 | 243.3 us | 249.0 us | 243.8 us |

Direct-column derived and CTE projections now map compatible literal equality
and range predicates back to the physical source. Exact-coercion and
cardinality checks decide whether to use the index; projection-local filters
and the outer predicate remain residual checks over the narrowed rows.

The two current runs are roughly 27 times faster than the immediate baseline
and about twice MySQL's time. Together with the preceding bare-correlation
change, this query moved from 1.773 seconds to about 0.50 milliseconds.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=CorrelatedBareOuterColumn \
FSDB_BENCH_PORT=3408 \
FSDB_BENCH_MYSQL_PORT=3417 \
just _bench-run --quick
```
