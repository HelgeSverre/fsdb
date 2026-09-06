# Streamed mutation targets

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`ac16f33`; the current server is `bedf452`. Both used the same benchmark
host, schema, SQL, 10,000-row user table, three warmups, and three measured
iterations.

`UpdateByNonIndexed` changes one row selected by an unindexed string column.
It therefore retains a full predicate scan while isolating the executor's
mutation-target collection cost.

| Engine | Previous | Current | Change |
|---|---:|---:|---:|
| fsdb | 7.914 ms | 6.303 ms | -20.4% |
| MySQL | 3.764 ms | 3.940 ms | +4.7% |

A repeated pre-change run measured fsdb at 8.922 ms and MySQL at 3.767 ms.
The current fsdb result is below both pre-change samples; ShortRun confidence
intervals remain too broad for a finer claim.

The executor now enumerates immutable row-store candidates directly and
retains only matching targets. Unordered mutations with `LIMIT` stop as soon
as enough targets survive. Ordered mutations still consume every candidate,
because their limit applies after sorting.

Command:

```sh
FSDB_BENCH_METHODS=UpdateByNonIndexed \
FSDB_BENCH_PORT=3407 \
FSDB_BENCH_MYSQL_PORT=3416 \
just _bench-run --quick
```
