# Filtered source-free correlated scalar check

The existing source-free scalar shortcut excludes `SELECT expression WHERE predicate`.
We tested a candidate that evaluated a correlated outer-column predicate directly
against the outer row. A disposable MySQL 8.4.11 server confirmed the result and
error behavior for matching, nonmatching, NULL, and unknown-column cases. The
focused fsdb regression and the 3,875-test suite passed after excluding
runtime-only predicates from the candidate.

The [baseline](2026-11-04-filtered-source-free-baseline.md) used committed
`1cad341f` plus the new benchmark method. The [candidate](2026-11-04-filtered-source-free-candidate.md)
used the same commit and benchmark method plus the uncommitted shortcut. Both
were ShortRuns on the same host and 10,000-user dataset. Their artifact headers
show the committed base SHA, not the uncommitted candidate diff.

| Query | Baseline fsdb | Candidate fsdb | Baseline MySQL | Candidate MySQL |
|---|---:|---:|---:|---:|
| Outer-only control | 406 µs | 364 µs | 75 µs | 75 µs |
| Filtered correlated scalar | 1,556 µs | 2,053 µs | 88 µs | 88 µs |
| Increment over control | 1,150 µs | 1,689 µs | 13 µs | 13 µs |

The candidate regressed by about 32% despite the control improving in the
second run. Its per-row expression scan, validation, and metadata inference
outweighed avoiding the SELECT pipeline. The candidate was reverted. The
benchmark method remains for a future implementation that preserves MySQL's
conditional scalar materialization and prepared-statement coercion semantics.

```sh
FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedScalarNoSourceFiltered just bench-quick
```
