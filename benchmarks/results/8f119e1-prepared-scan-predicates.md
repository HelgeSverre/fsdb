<!--
sha: 8f119e1
date: 2026-09-08T00:41:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Prepared scan predicates

`CountSelectiveEqualityScan` counts one age bucket through an unindexed
integer column. Both engines retain a full table scan; the change prepares the
direct column/literal comparison once before evaluating its candidate rows.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 2.089 ms | 840.9 us | 845.1 us |
| MySQL 8.4 | 1.097 ms | 1.097 ms | 1.097 ms |

The shared prepared path serves ordinary, grouped, windowed, source-pushed,
correlated-count, UPDATE, and DELETE scans. It retains the ordinary expression
evaluator for other predicate shapes and when read-time `CHAR` padding changes
stored values.

The two repeated current runs place fsdb about 60% below its baseline and 23%
below MySQL on this narrow in-memory count. BenchmarkDotNet ShortRun results are
directional; the repeat is included to distinguish the change from a single
noisy sample.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=CountSelectiveEqualityScan \
FSDB_BENCH_PORT=3407 \
FSDB_BENCH_MYSQL_PORT=3416 \
just _bench-run --quick
```
