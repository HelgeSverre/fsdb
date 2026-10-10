# Indexed constant-bound lookup snapshot

`PointSelectByFixedPk`, `PointSelectByRoundPi`, and
`PointSelectByBitCountBound` return the same row and six columns from the
10,000-row `users` table. The latter two predicates use `ROUND(PI())` and
`BIT_COUNT(7)` in place of the literal key `3`. The audited plans use
`PRIMARY` on both engines.

Three consecutive native same-host `ShortRun` invocations used:

```sh
FSDB_BENCH_METHODS=PointSelectByFixedPk,PointSelectByRoundPi,PointSelectByBitCountBound just bench-quick
```

The first two runs used the same uncommitted benchmark-method diff on
`b7723d14`; the third used the clean `2067dd31` benchmark commit. No source or
method logic changed between runs. The [third run](2067dd31-quick.md) retains
the generated toolchain, target, and dataset provenance.

| Engine and query | Run 1 | Run 2 | Clean run |
|---|---:|---:|---:|
| fsdb, fixed key | 220.19 µs | 230.18 µs | 248.35 µs |
| fsdb, `ROUND(PI())` | 247.18 µs | 232.54 µs | 231.83 µs |
| fsdb, `BIT_COUNT(7)` | 252.74 µs | 247.50 µs | 225.58 µs |
| MySQL, fixed key | 42.59 µs | 40.94 µs | 41.81 µs |
| MySQL, `ROUND(PI())` | 43.19 µs | 40.49 µs | 43.23 µs |
| MySQL, `BIT_COUNT(7)` | 40.10 µs | 41.96 µs | 42.12 µs |

The fixed-key control varies by 13% across the fsdb runs and 4% across the
MySQL runs, within the repository's 20% repeatability threshold. Fsdb's
point-lookup latency is roughly 5–6 times MySQL's here. The expression-bound
ordering changes between runs, so this snapshot does not establish a stable
incremental cost from either constant expression. It points to the common
lookup and query pipeline as the next profile target. These are read-only
queries; the in-memory fsdb and durable MySQL target distinction is not a
write-durability comparison.
