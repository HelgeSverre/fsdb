# Projection-referenced index ordering

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`db16735`; the current server is `18a1a8d`. Both used the same host, schema,
SQL, three warmups, and three measured iterations.

`OrderByIndexedAlias` orders a bounded result through a projected primary-key
alias. `GroupByIndexedAlias` groups through a projected secondary-index key.

| User rows | Workload | Previous fsdb | Current fsdb | MySQL |
|---:|---|---:|---:|---:|
| 10,000 | ORDER BY alias | 1.527 ms | 163.7 us | 41.3 us |
| 10,000 | GROUP BY alias | 1.568 ms | 396.0 us | 1.069 ms |
| 100,000 | ORDER BY alias | 18.184 ms | 164.1 us | 37.0 us |
| 100,000 | GROUP BY alias | 19.652 ms | 6.756 ms | 9.374 ms |

Alias and ordinal references now reach the same ordered-index paths as their
source expressions. The bounded ORDER BY no longer scales with table size.
GROUP BY gains the index-compatible plan and MySQL-compatible ordering. When
every projection is a grouping key or the unmodified built-in `COUNT(*)`, it
counts adjacent index keys without fetching or retaining full rows. Other
aggregates, filters, extension overrides, and richer grouped expressions keep
the general evaluator.

Commands:

```sh
FSDB_BENCH_USERS=10000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=OrderByIndexedAlias,GroupByIndexedAlias just _bench-run --quick
FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=OrderByIndexedAlias,GroupByIndexedAlias just _bench-run --quick
```
