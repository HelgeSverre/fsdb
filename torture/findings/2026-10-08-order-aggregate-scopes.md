# Aggregate ownership across ordering subqueries

Native MySQL 8.4.11 exposes a query-scope correctness gap beyond the previously
recorded ORDER BY error-code difference. With `aggregate_order(v INT)` containing
`2,1`, `SELECT (SELECT SUM(v)) AS total FROM aggregate_order` returns one row,
`3`. At `f88bb501`, fsdb returns two rows, `2` and `1`.

The [maintained native oracle](../scripts/order-aggregate-scope-oracle.py) verifies
results and error code/SQLSTATE pairs. Every standalone SELECT is also prepared;
binding rejections occur at preparation, independently of whether rows are read.
Scalar-subquery cardinality error 1242 occurs at execution; preparation succeeds.

## Native scope boundary

| Query shape | MySQL | fsdb at `f88bb501` |
|---|---|---|
| `SELECT (SELECT SUM(v)) AS total FROM aggregate_order` | one total, `3` | two totals, `2,1` |
| Outer ORDER BY `(SELECT SUM(v+1))` | 3029 / HY000 | succeeds, orders `1,2` |
| Outer ORDER BY `(SELECT SUM(aggregate_order.v) FROM aggregate_order i LIMIT 1)` | 3029 / HY000 | succeeds, orders `1,2` |
| Outer ORDER BY `(SELECT SUM((SELECT v)))` | 3029 / HY000 | succeeds, orders `1,2` |
| Outer ORDER BY `(SELECT SUM(i.v) FROM aggregate_order i)` | succeeds | matches |
| Outer ORDER BY `(SELECT SUM(1))` | succeeds | matches |
| Outer ORDER BY `(SELECT SUM(i.v+aggregate_order.v) FROM aggregate_order i)` | succeeds, orders `1,2` | matches |
| `SELECT SUM(v) AS s ... ORDER BY (SELECT SUM(v))` | one total, `3` | matches this fixture |

The references inside aggregate arguments determine ownership. An aggregate
whose arguments refer only to the outer source can belong to the outer query,
even when its syntax appears inside a subquery with its own FROM. A local input
reference keeps the mixed aggregate local. Literal-only aggregation also stays
inside its written subquery. The matching grouped fixture does not prove that
fsdb assigns ownership correctly; other fixtures expose the difference.

## Ordering diagnostics

An aggregate in ORDER BY cannot by itself turn a non-aggregated query into an
aggregated query. Native MySQL reports 3029 / HY000 with the one-based position
of the offending ordering expression. This applies to constant projections,
source-free SELECT, DISTINCT, window projections, empty input, LIMIT 0, and
`sql_mode=''`. At `f88bb501`, fsdb either accepts these queries or emits 1140.

An existing GROUP BY, projection aggregate, or aggregate HAVING permits aggregate
ordering. An ordinary HAVING predicate does not. Nested aggregate use retains
1111; invalid projection, WHERE, or ordering column references retain 1054 before
3029. The oracle includes these precedence cases and an offending second ORDER
BY term.

This requires aggregate binding by query scope, followed by query classification
and ordering validation. A guard based only on syntactically local aggregate
calls would leave the wrong-row result and correlated ordering rejections open.

## Evidence and limits

The oracle passes against a disposable native MySQL 8.4.11 server with 64 MiB
buffer and redo limits. Current-engine observations use the embedded Debug
assembly, .NET SDK 10.0.401, eight logical processors, and a 4 GiB GC heap cap.
The script is a native contract, not an accepted differential failure or a claim
that fsdb implements the behavior. No known-gap signatures were enrolled.


## Implemented ownership coverage

Expression-subquery aggregates whose arguments bind only to the enclosing source
are evaluated over that enclosing group. The one-total fixture above returns
`3`, including nested scalar forms, empty inputs, membership/existence forms,
and grouped/window projections. Local-input and literal-only aggregates keep
their inner scope. Inner source cardinality remains observable.

Grouping validation checks unaggregated enclosing references during execution
and preparation, while ANY_VALUE retains its exemption in either query scope.
The scope binder is shared by query classification, aggregate rewriting,
grouping validation, and grouped-window lowering. Unchanged expression trees
retain their identity for subquery memoization.

Validation: `just check` passes 3,040 tests with no build warnings or errors and
a 4 GiB GC heap cap. Native MySQL 8.4.11 contracts pass 53 cases / 5,349 steps
with zero differences at `20261008T012642104-6253/contracts`, using disposable
64 MiB buffer and redo limits. The maintained native oracle passes separately.
The [allocation probe](../../benchmarks/results/d2fed802-aggregate-scope-binding.md)
records the cost of the scope checks.

ORDER BY-only aggregate classification and error precedence remain open; this
fix does not implement error 3029. Broader derived/CTE aggregate-scope combinations
are not established by these expression-subquery fixtures.
