# Statement-scoped validation for inner-only EXISTS projections

The indexed correlated `EXISTS` path validates an unused SELECT expression
before counting matches. An expression such as `c.id+0` has the same binding
validation for every outer row when all column references belong
to the inner source. The new statement memo stores successful validation for
that case. Expressions that depend on an outer binding still take the existing
per-row path. The memo is reset for each statement, so schema changes in a
later statement cannot reuse it.

The [ShortRun result](2026-11-04-correlated-exists-projection-cache.md) on the
10,000-user, 50,000-order benchmark measured the expression query at 602 µs
after the change, versus 974 µs in the [earlier result](2026-11-04-correlated-exists-expression-after.md).
The qualified-column control moved from 725 to 807 µs between runs, and the
three-iteration confidence intervals are wide. The result supports a speedup
for this case but does not establish a precise effect size. MySQL measured
176 µs for the expression query in the new run, so substantial query overhead
remains.

The focused regression covers matching and missing outer rows, indexed probe
work, invalid inner columns, and an outer-dependent projection. The full test
suite result is recorded with the commit.

```sh
FSDB_BENCH_METHODS=CorrelatedPhysicalExistsColumn,CorrelatedPhysicalExistsExpression just bench-quick
dotnet run --project tests/Fsdb.Tests -c Release -- --filter-test-case 'correlated EXISTS uses a complete inner equality key'
```
