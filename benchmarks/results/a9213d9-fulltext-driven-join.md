# Full-text-driven bounded joins

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`1e45c50`; the current server is `a9213d9`. Both used the same host, schema,
SQL, three warmups, and three measured iterations.

`FullTextRightJoinUsers` writes the ordinary `users` table first and the
full-text `articles` table second, joins on the article primary key, and
returns the first twenty rows in implicit relevance order.

| Article and user rows | Previous fsdb | Current fsdb | Change | MySQL | fsdb / MySQL |
|---:|---:|---:|---:|---:|---:|
| 10,000 | 3.379 ms | 726.1 us | -78.5% | 478.9 us | 1.5x |
| 100,000 | 71.891 ms | 12.630 ms | -82.4% | 3.823 ms | 3.3x |

The planner may now put the full-text source first for this bounded shape,
then stream relevance-ordered candidates through the exact unique lookup.
The rewrite is limited to qualified two-table inner joins whose result order
and multiplicity are preserved. `STRAIGHT_JOIN`, locking reads, grouping,
windows, CTEs, residual join predicates, and ambiguous references keep their
original execution order.

Commands:

```sh
FSDB_BENCH_METHODS=FullTextRightJoinUsers just _bench-run --quick
FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=100000 FSDB_BENCH_METHODS=FullTextRightJoinUsers just _bench-run --quick
```
