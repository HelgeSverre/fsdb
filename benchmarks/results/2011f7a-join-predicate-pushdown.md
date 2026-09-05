# Inner-join predicate pushdown

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`60e6b5b`; the current server is `2011f7a`. Both used the same benchmark
host, schema, SQL, 10,000-user data set, three warmups, and three measured
iterations.

Both queries join the first 100 users back to the complete user table by
age. The indexed workload uses `age`; the hash control uses its unindexed
`scan_age` mirror.

| Engine | Workload | Previous | Current | Change |
|---|---|---:|---:|---:|
| fsdb | indexed low-cardinality join | 16.985 ms | 2.926 ms | -82.8% |
| fsdb | hash control | 17.964 ms | 2.742 ms | -84.7% |
| MySQL | indexed low-cardinality join | 1.613 ms | 1.614 ms | +0.1% |
| MySQL | hash control | 1.807 ms | 1.745 ms | -3.4% |

Qualified, deterministic predicates that reference only the base table now
run before an all-inner join chain. Effectful functions, unqualified columns,
subqueries, and outer joins remain on the ordinary post-join path. Plain
`COUNT(*)` also counts its input stream directly instead of retaining a
second list of every joined row.

Command:

```sh
FSDB_BENCH_METHODS=LowCardinalityIndexedJoin,LowCardinalityHashJoin just _bench-run --quick
```
