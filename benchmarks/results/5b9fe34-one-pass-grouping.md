# One-pass grouping

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11,
BenchmarkDotNet 0.14.0, MySQL 8.4.11. The previous fsdb server is
`d24accd`; the current server is `5b9fe34`. Both used the same benchmark
host, schema, SQL, 50,000-row order table, three warmups, and three measured
iterations.

`GroupByAggregate` groups an unindexed status column and computes `COUNT(*)`
and `SUM(total)` for every group.

| Engine | Previous | Current | Change |
|---|---:|---:|---:|
| fsdb | 32.362 ms | 23.202 ms | -28.3% |
| MySQL | 20.536 ms | 20.519 ms | -0.1% |

The grouped executor now evaluates each row's filter and grouping key in one
cancellation-aware pass. Group members still retain their original row
references for aggregate evaluation, but the executor no longer allocates and
walks a second complete list of filtered rows before building the groups.

Command:

```sh
FSDB_BENCH_METHODS=GroupByAggregate just _bench-run --quick
```
