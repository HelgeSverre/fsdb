# Filtered source-free scalar plan check

The 100-row correlated scalar benchmark exposed a row-scaling cost in
`SELECT u.id+0 WHERE u.id % 2 = 0` without a `FROM` clause. An earlier direct
predicate shortcut increased latency because it repeated binding and metadata
work for each outer row. This trial caches validated projection metadata by
statement and stable outer scope, while evaluating the predicate and value for
each row. Literal-only projections retain the existing conditional scalar
reduction path, including its prepared-statement coercion behavior.

Command: `FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedScalarNoSource,CorrelatedScalarNoSourceFiltered,CorrelatedPhysicalExists just bench-quick`

| 100-row method | Baseline fsdb | Candidate fsdb | Baseline MySQL | Candidate MySQL |
| --- | ---: | ---: | ---: | ---: |
| Equality outer-only control | 364.40 µs | 359.36 µs | 75.54 µs | 75.75 µs |
| Scalar without source | 553.86 µs | 604.03 µs | 81.17 µs | 130.86 µs |
| Filtered scalar without source | 1,786.74 µs | 604.95 µs | 87.92 µs | 91.40 µs |
| Physical indexed EXISTS | 640.31 µs | 687.84 µs | 174.14 µs | 173.67 µs |

The filtered-minus-outer-control increment fell from 1,422 to 246 µs,
roughly 83%. The unfiltered scalar and physical `EXISTS` controls varied in
the opposite direction; the MySQL unfiltered scalar candidate was especially
noisy (54.61 µs standard deviation). This is one ShortRun A/B comparison,
so it supports a large gain for this query shape, not a general planner
speedup. Raw runs: [baseline](2026-11-05-filtered-cached-plan-baseline.md)
and [candidate](2026-11-05-filtered-cached-plan-candidate.md).

Correctness checks include filtered values for two outer rows, binding errors
in a filtered projection, the prepared-statement conditional scalar regression,
and the full sequenced suite: 3,884 passed. The optimization applies only to a
single outer-bound projection in a top-level stable scope with a source-free
filtered scalar query. Nested scopes and general materialized subqueries still
use the existing path.
