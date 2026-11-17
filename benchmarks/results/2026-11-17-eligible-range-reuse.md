# Range-selection reuse with verified branch coverage

Reusing the selected immutable range improves this eligible correlated
workload: median throughput rises 33.8% and allocation falls 23.7% relative
to `7ccdf3f1`. Both initial and recheck diagnostic records remain present.

## Coverage before measurement

The earlier selection-reuse experiment did not reach its changed branch
because numeric DOUBLE bounds were rejected. That coverage gap is corrected
by the exact numeric-bound normalization in `7ccdf3f1`.

A temporary marker at the changed branch's row-materialization callback was
run with `range 0`: the benchmark executed its ten normal warmup queries,
checking all expected result counts, but no timed queries. It recorded 1,010
successful selections, each with two candidate rows. The phase array still
contains both `EstimateRange` and `RecheckRange`; the correctness gate checks
their observable warnings. The
[coverage summary](2026-11-17-eligible-range-reuse-coverage.json) records the
probe. The marker was removed and the Release binary rebuilt before timing.

## Implementation

The initial and refinement stages use the same physical projection and
immutable table snapshot. Correlated bounds are restricted by the existing
statement-stability checks. The refinement stage now materializes the initial
selection instead of repeating column resolution, bound normalization, and
range selection. It retains separate diagnostic records for both phases,
including deferred setup when no outer rows execute. This introduces no
cross-statement cache.

## Matched comparison

The same native M2 Max host, .NET 10.0.401, Release builds, in-memory stores,
data, warmup, and alternating three eight-second samples per revision/workload
from the [benchmark protocol](2026-11-17-correlated-range-ref-estimate.md)
were used. All query-result invariants passed. Builds completed before
measurement. A copied pre-change output directory isolated the baseline.
The [raw samples](2026-11-17-eligible-range-reuse.json) retain order, elapsed
time, counts, allocations, and derived rates.

| Workload | Before median queries/sec | After median queries/sec | Before bytes/query | After bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 3,849 | 4,047 | 645,122 | 647,886 |
| Eligible range | 544 | 728 | 4,117,753 | 3,139,934 |

Range samples span 533–565 queries/sec before and 715–745 after, without
overlap. Allocations are consistently near 4.11–4.12 MB before and 3.14 MB
after. The unchanged equality path has approximately 5% higher median
throughput with approximately 0.4% allocation variation, so background timing
variation remains a limitation. The range allocation reduction and the
consistent paired speed improvement support retaining this change.

The earlier failed-plan/equality-fallback workload used a different execution
path; these results do not establish full recovery relative to that path or
MySQL speed parity. Broader planning/context costs remain open. Temporary
marker output and baseline binaries were removed after measurement.

Validation: `just check` passed all 3,954 tests, including the 14/6/2 and
16/8/4 diagnostic matrices, empty-table setup, forced-index controls, and
the broad-candidate intersection regression.
