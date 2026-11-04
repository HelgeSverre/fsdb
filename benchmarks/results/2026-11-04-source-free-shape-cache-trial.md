# Source-free scalar eligibility-cache trial

The source-free correlated scalar fast path recomputes its shape and
statement-stability check for each outer row. A trial cached that Boolean by
`SelectStmt` identity in the statement memo. The existing source-free scalar
correctness regression passed. Four consecutive ShortRuns on the same host,
10,000-user/50,000-order fixture, and MySQL 8.4.11 compared the outer-only
control with `(SELECT u.id+0)` over 100 outer rows. Each method had three
warmups and three measured iterations.

| Run | Fsdb outer only | Fsdb scalar | Scalar minus control | MySQL outer only | MySQL scalar |
|---|---:|---:|---:|---:|---:|
| A1, current main | 381 µs | 715 µs | 334 µs | 81 µs | 82 µs |
| B1, cached shape | 366 µs | 537 µs | 170 µs | 78 µs | 84 µs |
| A2, current main | 391 µs | 608 µs | 217 µs | 78 µs | 81 µs |
| B2, cached shape | 701 µs | 648 µs | not meaningful | 79 µs | 74 µs |

B2's fsdb outer-only control had 355 µs standard deviation and a 543 µs
median, so its negative scalar-minus-control mean does not measure a real
speedup. The scalar mean in B2 also exceeded the clean A2 run. The data do
not separate a modest caching gain from run variation. The trial was
reverted; no planner code changed. A lower-noise profile should measure
eligibility calls and projection metadata work separately before another
source-free dispatch change.

```sh
FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedScalarNoSource just _bench-run --quick
```
