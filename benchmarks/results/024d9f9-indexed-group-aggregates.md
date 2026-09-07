# Index-owned simple group aggregates

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. Both fsdb runs used the same host,
schema, SQL, three warmups, and three measured iterations.

`GroupByIndexedSimpleAggregates` groups 100,000 users by indexed `age` and
projects `COUNT(*)`, `COUNT(age)`, `COUNT(id)`, `MIN(age)`, and `MAX(age)`.

| Implementation | Mean | Allocated |
|---|---:|---:|
| fsdb before (`7a02144`) | 122.30 ms | not reported |
| fsdb after (`024d9f9`) | 6.067 ms | 579 B |
| MySQL 8.4.11 | 13.523 ms | 810 B |

The ordered index already contains each grouping key and its adjacent row
cardinality. The new path derives counts and grouping-key extrema from that
information instead of resolving and retaining every row. Nullable non-key
aggregates, filters, ordering, richer expressions, and extension overrides
continue through the general evaluator.

Command:

```sh
FSDB_BENCH_PORT=3441 FSDB_BENCH_MYSQL_PORT=3442 FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=GroupByIndexedSimpleAggregates just _bench-run --quick
```
