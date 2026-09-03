# Low-cardinality join planning

Environment: Apple M2 Max, macOS 15.6, .NET 10.0.11, BenchmarkDotNet 0.14.0,
MySQL 8.4.11. `LowCardinalityIndexedJoin` joins the first 100 users to all
users by the indexed `age` column. `LowCardinalityHashJoin` uses the unindexed
`scan_age` mirror as a plan-shape reference.

The before and after quick runs use BenchmarkDotNet `ShortRun`. The full after
run uses three warmups and six measured iterations.

| Users | Engine | Before indexed | After indexed | Hash twin |
|---:|---|---:|---:|---:|
| 10,000 | fsdb | 21.720 ms | 17.753 ms | 15.469 ms |
| 10,000 | MySQL | 1.710 ms | 1.715 ms | 1.841 ms |
| 100,000 | fsdb | 267.39 ms | 188.16 ms | 204.60 ms |
| 100,000 | MySQL | 15.30 ms | 15.28 ms | 17.29 ms |

The 100,000-row fsdb samples are bimodal and the short-run confidence intervals
are wide. The longer after run measured 17.786 ms at 10,000 users and 189.52 ms
at 100,000 users. The stable 10,000-row result is an 18% improvement over the
before run. The remaining MySQL gap is primarily a constant-factor execution
gap rather than the former plan-selection penalty.

The planner retains index probes below its small-table floor, whenever the
hash path cannot preserve the comparison semantics, and when `LIMIT` may stop
the consumer before a full hash build pays for itself. For a first physical
join, `EXPLAIN` applies the same policy to the base-table cardinalities.
