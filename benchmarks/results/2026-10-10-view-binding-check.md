# Direct-view binding check

`ViewFilterLimit` was measured on the same macOS host with BenchmarkDotNet
ShortRun (three warmups and three measured iterations) using
`FSDB_BENCH_METHODS=ViewFilterLimit just _bench-run --quick`. Both runs used the
native MySQL 8.4.11 server and fsdb's in-memory mode. The workload reads the
`adult_users` view with `ORDER BY id LIMIT 20` over 10,000 seeded users.

| Code | fsdb mean | MySQL mean |
|---|---:|---:|
| `c74d1623`, before the binding check | 940.17 µs | 77.91 µs |
| Working tree with the binding check | 745.78 µs | 73.39 µs |

The short runs have broad uncertainty (fsdb standard deviations of 41.63 and
103.05 µs, respectively). They do not show a regression from validating a
direct view before merging it, but they do not establish an improvement. The
remaining fsdb/MySQL read-path gap is much larger than the observed difference
between these two runs. This is a read-only comparison; durability does not
affect the query being timed.
