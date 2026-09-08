<!--
sha: 94a6026
date: 2026-09-08T00:53:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Prepared boolean scans

`CountConjunctiveScan` counts an unindexed integer range expressed as two
direct comparisons joined by `AND`. Both engines scan the same 10,000 rows.

| Engine | Before | Current run 1 | Current run 2 |
|---|---:|---:|---:|
| fsdb | 3.432 ms | 1.207 ms | 1.358 ms |
| MySQL 8.4 | 1.256 ms | 1.254 ms | 1.263 ms |

The evaluator prepares eligible comparison leaves throughout `AND` and `OR`
trees. The same lazy three-valued combinators serve prepared and ordinary
execution, preserving left-to-right evaluation and short-circuiting. Leaves
outside the narrow direct column/literal shape retain the ordinary evaluator.

The repeated current runs reduce fsdb latency by 60–65% and place it between
4% faster and 8% slower than MySQL on this narrow scan. BenchmarkDotNet
ShortRun results are directional; the repeat captures the observed run-to-run
spread.

Command:

```sh
FSDB_BENCH_USERS=10000 \
FSDB_BENCH_METHODS=CountConjunctiveScan \
FSDB_BENCH_PORT=3407 \
FSDB_BENCH_MYSQL_PORT=3416 \
just _bench-run --quick
```
