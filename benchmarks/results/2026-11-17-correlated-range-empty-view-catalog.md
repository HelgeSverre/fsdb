# Empty view-catalog lookup allocation reduction

Returning immediately from an empty immutable `mysql.views` snapshot reduces
range-workload allocations by 7.5% in this matched experiment. Equality-control
allocations fall 2.4%. The implementation retains ordinary definition parsing
when catalog rows exist; it adds no memoization or invalidation state.

## Evidence and change

The [allocation profile](2026-11-17-correlated-range-numeric-probe.md)
attributed approximately 6.5% of sampled bytes to `tryStoredViewDefinition`
as the nearest engine frame. Physical-table probes repeatedly ask whether a
stored view shadows the name, including when the view catalog is empty.
Previously each call created a scan/choose/find pipeline over that empty
catalog.

The lookup now gets one physical catalog snapshot, checks its row count, and
returns None when it is empty. For a nonempty catalog it runs the same parsing
pipeline over the captured snapshot. `Storage.scan` and `tableSnapshot` use
the same table resolution; the optimization changes the empty-table handling.
Temporary-table shadow checks remain in `tryStoredView` before this lookup.

## Matched benchmark

Before: `10e3fc46`. The after candidate changes only this lookup. The existing
[protocol](2026-11-17-correlated-range-ref-estimate.md) and benchmark source
were retained: same native M2 Max host, Release builds, .NET 10.0.401,
in-memory stores, identical data, ten warmup queries, and three alternating
eight-second samples per revision/workload. Both stores have no stored views.
All query-result invariants passed.

| Workload | Before median queries/sec | After median queries/sec | Before bytes/query | After bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 3,251 | 3,773 | 658,780 | 643,193 |
| Range | 814 | 865 | 2,821,765 | 2,610,275 |

The range allocation samples were consistently near 2.82 MB before and
2.61 MB after. Median throughput rose 6.3% for range and 16.0% for equality,
but timing varied materially under uncontrolled host activity. The stable
allocation reduction is stronger evidence than a precise speedup claim.
The [raw samples](2026-11-17-correlated-range-empty-view-catalog.json)
include query counts, elapsed times, allocations, and ordering.

This is an empty-catalog optimization. It does not establish performance
with nonempty view catalogs, durable-write performance, or complete recovery
from the earlier correlated planner regression. The copied before binaries
were removed after measurement.

Validation: `just check` passed all 3,954 tests, including stored-view
replacement and drop cases, correlated diagnostics, and range intersections.
