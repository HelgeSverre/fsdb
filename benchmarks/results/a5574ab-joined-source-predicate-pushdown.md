# Joined-source predicate pushdown

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`61eae90`; the current server is `a5574ab`. Both used the same benchmark
host, schema, SQL, 10,000-user data set, three warmups, and three measured
iterations.

The workload joins the user table to itself on its unindexed, low-cardinality
`scan_age` column, then retains the first 100 rows from the joined source.

| Engine | Previous | Current | Change |
|---|---:|---:|---:|
| fsdb | 1,216.574 ms | 25.310 ms | -97.9% |
| MySQL | 5.279 ms | 7.853 ms | +48.8% |

The fsdb change filters a joined physical or derived source before an
all-inner join fans it out. Predicates remain on the ordinary post-join path
when a query can stop early or when qualification, purity, or join semantics
make distribution unsafe. The fsdb result removes the table-size-dependent
fan-out cost while retaining a constant-factor gap from MySQL in this short
run.

Command:

```sh
FSDB_BENCH_METHODS=LowCardinalityJoinedFilter just _bench-run --quick
```
