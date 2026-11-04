# Indexed correlated EXISTS with an unused expression projection

`EXISTS` needs an inner row, but MySQL still validates the SELECT expression.
The previous indexed shortcut handled literals and inner columns; a form such
as `EXISTS(SELECT c.id+0 FROM orders c WHERE c.user_id=u.id)` ran broader
subquery processing for every outer row. The indexed path now accepts a
nonaggregate, window-free expression without nested subqueries after binding
and function validation in the inner source context. It never evaluates the
projection value. Invalid expressions retain the ordinary path and its error.

The focused fsdb regression checks matching and nonmatching outer rows,
bounded examined-row count, an unused `1/0` projection, and error 1054 for an
unknown inner column. The benchmark also runs the same query against MySQL
8.4.11; MySQL's result and earlier disposable projection probes establish
that EXISTS ignores the projected value while validating names.

The [before](2026-11-04-correlated-exists-expression-before.md) and
[after](2026-11-04-correlated-exists-expression-after.md) artifacts are
three-iteration ShortRuns on the same M2 Max host and 10,000-user,
50,000-order dataset. The before artifact uses committed `2634b856` plus
the new benchmark method; the after artifact includes the uncommitted
engine change. SHA headers identify the committed base.

| Query | fsdb before | fsdb after | MySQL before | MySQL after |
|---|---:|---:|---:|---:|
| Qualified inner column | 740 µs | 725 µs | 180 µs | 177 µs |
| Inner expression `c.id+0` | 4,940 µs | 974 µs | 178 µs | 178 µs |

The expression query fell about 5.1×. The remaining 249 µs over the
qualified-column control includes per-row validation; that cost and the
larger whole-query gap versus MySQL remain open. Aggregate, window, nested
subquery, and broader source shapes keep their existing evaluation paths.

```sh
FSDB_BENCH_METHODS=CorrelatedPhysicalExistsColumn,CorrelatedPhysicalExistsExpression just bench-quick
```
