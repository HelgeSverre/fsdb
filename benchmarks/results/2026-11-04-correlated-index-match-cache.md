# Statement-local equality index-match cache

The [row-scaling probe](2026-11-04-correlated-row-scaling.md) showed that
correlated `COUNT(*)` adds substantial work across 100 outer rows. A matched
control compared the same derived count, direct physical count, and outer-only
query before and after caching the equality index matches for each immutable
table snapshot and set of bound columns within a statement. Value-dependent
key checks and the index probe still run for each outer row.

| Query | Before | Cache run 1 | Cache run 2 |
|---|---:|---:|---:|
| Derived `COUNT(*)` | 892 µs | 640 µs | 739 µs |
| Physical `COUNT(*)` | 929 µs | 649 µs | 710 µs |
| Outer-only control | 369 µs | 430 µs | 553 µs |
| Derived minus outer-only | 523 µs | 210 µs | 186 µs |
| Physical minus outer-only | 560 µs | 219 µs | 157 µs |

The before run's table was captured from the BenchmarkDotNet output; its raw
artifact was overwritten because `bench-quick` names results by the unchanged
commit SHA. The two retained after artifacts are [run 1](6933dde4-memoized-quick-1.md)
and [run 2](6933dde4-memoized-quick-2.md). Each is a three-iteration
ShortRun on the same 10,000-user/50,000-order dataset and host. The second
run had considerable host noise, especially in MySQL. The consistent decrease
in the correlated increment over the outer-only control supports the cache,
but these ShortRuns do not establish a precise speedup percentage.

The cache is keyed by the table object, so a new immutable table version
cannot reuse an earlier version's matches. Index visibility and definitions
are part of that table version. `just check` passed 3,870 tests with no build
warnings after the change. Remaining setup and per-row subquery costs are
still visible; this only removes repeated index discovery.
