# Source-free correlated scalar shortcut

The [correlated EXISTS control](2026-11-04-correlated-exists-fast-path.md)
also measured a source-free `(SELECT u.id+0)` scalar projection. It took
about 1.27 ms for 100 outer rows on fsdb, despite having no inner table.
The scalar evaluator was still calling the full SELECT pipeline per row.

For a one-expression SELECT with no source, filter, grouping, ordering, limit,
CTE, or locking clause, fsdb now evaluates the expression in the current outer
row context when its result metadata can be inferred. Stable uncorrelated
subqueries retain statement-local memoization; other shapes retain the SELECT
pipeline. The result still passes through scalar-subquery coercion. A focused
regression covers numeric and nullable text projections, MySQL's reducible
`LIMIT 0` behavior, filtered empty results, source-free aggregates, and invalid
column diagnostics. Disposable MySQL 8.4.11 confirmed the source-free row
values and the `LIMIT 0` boundary.

The [before](2026-11-04-correlated-exists-after.md) and
[after](2026-11-04-source-free-scalar-after.md) artifacts are three-iteration
ShortRuns on the same 10,000-user, 50,000-order dataset and M2 Max host.
The after artifact's commit SHA identifies the committed base; the shortcut
was measured in the working tree before its own commit.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Outer only | 378 µs | 405 µs | 74 µs | 77 µs |
| Source-free correlated scalar | 1,269 µs | 577 µs | 79 µs | 84 µs |
| Scalar increment over outer only | 891 µs | 172 µs | 5 µs | 7 µs |

The fsdb scalar mean fell about 2.2×, and its incremental cost fell about
5.2× in these runs. ShortRun error bounds are broad, so the precise ratio
needs a longer run if used for release claims. The remaining approximately
172 µs increment and the 328 µs outer-only gap versus MySQL still warrant
profiling; this shortcut does not establish general correlated-query parity.

```sh
FSDB_BENCH_METHODS=CorrelatedEqualityOuterOnly,CorrelatedScalarNoSource just bench-quick
```
