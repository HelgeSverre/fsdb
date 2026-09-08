<!--
sha: 584d606
date: 2026-09-08T00:20:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Prepared mutation predicates

`UpdateByNonIndexed` changes one row selected by an unindexed string column.
It retains the full table scan while measuring the cost of evaluating the
same direct column/literal comparison for every candidate row.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 5.490 ms | 4.208 ms | 4.287 ms |
| MySQL 8.4 | 3.673 ms | 3.686 ms | 3.670 ms |

The prepared path binds the direct column position, declared type, and literal
once per mutation. It continues to use the shared comparison resolver for
collation, ENUM ordinal, NULL, and operand-order semantics. Other predicate
shapes use the ordinary expression evaluator.

The two repeated current runs place fsdb about 22% below its baseline and
within 17% of MySQL. BenchmarkDotNet ShortRun results are directional; the
repeat is included to distinguish the change from a single noisy sample.

Command:

```sh
FSDB_BENCH_METHODS=UpdateByNonIndexed \
FSDB_BENCH_PORT=3407 \
FSDB_BENCH_MYSQL_PORT=3416 \
just _bench-run --quick
```
