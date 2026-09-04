# Low-cardinality join planning

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11, BenchmarkDotNet 0.14.0,
MySQL 8.4.11. The previous engine is `b98840e`; the current planner is
`bf4b847`. Both revisions ran the same benchmark source, schema, SQL, data,
three warmups, and six measured iterations. `LowCardinalityIndexedJoin` joins
the first 100 users to all users by indexed `age`. `LowCardinalityHashJoin`
uses the unindexed `scan_age` mirror as the physical-plan control.

| Users | Engine | Workload | Previous | Current | Change |
|---:|---|---|---:|---:|---:|
| 10,000 | fsdb | indexed low-cardinality join | 23.318 ms | 16.580 ms | -28.9% |
| 10,000 | fsdb | hash control | 17.110 ms | 18.249 ms | +6.7% |
| 10,000 | MySQL | indexed low-cardinality join | 1.725 ms | 1.677 ms | -2.8% |
| 10,000 | MySQL | hash control | 1.776 ms | 1.819 ms | +2.4% |
| 100,000 | fsdb | indexed low-cardinality join | 327.97 ms | 208.47 ms | -36.4% |
| 100,000 | fsdb | hash control | 194.98 ms | 199.05 ms | +2.1% |
| 100,000 | MySQL | indexed low-cardinality join | 14.69 ms | 14.74 ms | +0.3% |
| 100,000 | MySQL | hash control | 16.71 ms | 16.75 ms | +0.2% |

`JoinUsersOrders`, the selective indexed control, moved from 308.8 us to
300.0 us on fsdb at 10,000 users. The new policy therefore removes the broad
index-probe penalty without slowing the selective path.

The 100,000-row fsdb samples remain bimodal: the current indexed median was
202.24 ms, down from 326.44 ms. Its mean and median now track the unindexed hash
control, so the former plan-selection cliff is gone. The remaining gap to
MySQL is execution cost after choosing the same broad plan, not another
indexed-versus-hash decision error.

The planner retains index probes below its small-table floor, whenever the
hash path cannot preserve comparison semantics, and when `LIMIT` may stop the
consumer before a full hash build pays for itself. For a first physical join,
`EXPLAIN` applies the same policy to base-table cardinalities.

Commands:

```sh
FSDB_BENCH_METHODS=LowCardinalityIndexedJoin,LowCardinalityHashJoin,JoinUsersOrders just _bench-run
FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=LowCardinalityIndexedJoin,LowCardinalityHashJoin just _bench-run
```
