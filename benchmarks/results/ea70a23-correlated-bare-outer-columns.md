<!--
sha: ea70a23
date: 2026-09-08T01:33:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated bare outer columns

`CorrelatedBareOuterColumn` counts orders for 100 rows selected from a derived
users table. The inner predicate refers to the derived `outer_key` without a
qualifier. Because the inner orders source has no column with that name,
MySQL resolves it against the outer query.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 1.773 s | 13.340 ms | 13.599 ms |
| MySQL 8.4 | 222.0 us | 229.0 us | 243.3 us |

fsdb now carries the normalized inner source schema alongside its correlated
probe qualifier. A bare name can become an outer probe dependency only when
that schema proves it is not local. Same-named inner columns continue to win,
and unresolved or ambiguous names fall back to ordinary evaluation.

The repeated current runs are roughly 130 times faster than the baseline.
They retain a substantial constant-factor gap to MySQL, largely in repeated
per-query and per-outer-row execution rather than inner-table scanning; this
result does not isolate those residual costs. BenchmarkDotNet ShortRun results
are directional, but the removed full-scan cliff is unambiguous.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=CorrelatedBareOuterColumn \
FSDB_BENCH_PORT=3408 \
FSDB_BENCH_MYSQL_PORT=3417 \
just _bench-run --quick
```
