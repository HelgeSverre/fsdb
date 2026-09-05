# Relevance-ordered full-text joins

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`77ad1ff`; the current server is `809dc91`. Both used the same benchmark
host, schema, SQL, three warmups, and three measured iterations.

`FullTextJoinUsers` searches articles in natural-language mode, joins each
article to a unique user row, and returns the first twenty rows in implicit
relevance order.

| Article and user rows | Previous fsdb | Current fsdb | Change | MySQL | fsdb / MySQL |
|---:|---:|---:|---:|---:|---:|
| 10,000 | 2.274 ms | 789.3 us | -65.3% | 456.5 us | 1.7x |
| 100,000 | 32.314 ms | 15.016 ms | -53.5% | 4.024 ms | 3.7x |

The full-text source is heapified by its synthetic relevance score before an
exact unique inner-join chain. The ordinary lazy select pipeline can then
dequeue only enough candidates to satisfy `LIMIT`, without projecting or
joining every matching row. Joins that can multiply a source row, carry
residual join predicates, or otherwise fail to preserve left-side order
retain the complete scoring and ordering path.

Commands:

```sh
FSDB_BENCH_METHODS=FullTextJoinUsers just _bench-run --quick
FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=100000 FSDB_BENCH_METHODS=FullTextJoinUsers just _bench-run --quick
```
