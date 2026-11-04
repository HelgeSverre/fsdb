# Correlated derived equality: row-count control

The [November control](2026-11-04-correlated-planner-current-check.md) left
open whether the roughly 1 ms query latency came mostly from statement setup
or correlated row work. Three versions of the same query now select zero,
one, or 100 outer users. The inner derived source and equality predicate are
identical. This [ShortRun](c61baded-quick.md) used the same 10,000-user,
50,000-order dataset, M2 Max, .NET 10.0.12, and MySQL 8.4.11 as the earlier
control. Each method had three warmups and three measured iterations.

| Outer rows | fsdb mean | MySQL mean |
|---:|---:|---:|
| 0 | 189 µs | 50 µs |
| 1 | 218 µs | 66 µs |
| 100 | 977 µs | 263 µs |

The 0-row case measures query handling without executing a correlated probe;
it already accounts for about 189 µs in fsdb. The 100-row result adds about
788 µs over that baseline, versus about 213 µs in MySQL. The single-row
increment is larger than a linear extrapolation from 100 rows in both engines,
so this is a diagnostic rather than a per-row cost model. It nevertheless
shows **both** a setup gap and a row-scaling gap. Improving only fixed setup
cannot close the correlated latency difference. The next profile should
separate outer-row projection/subquery dispatch from candidate lookup and
residual evaluation over the 100-row case.

```sh
FSDB_BENCH_METHODS=CorrelatedDerivedEquality,CorrelatedDerivedEqualityOneRow,CorrelatedDerivedEqualityNoRows just bench-quick
```
