# Grouped aggregate evaluation and projection side effects

Native MySQL 8.4.11 and fsdb at `9a498be5` differ in observable evaluation
order across grouped input, ordering expressions, and returned projection rows.
The [maintained oracle](../scripts/grouped-evaluation-oracle.py) checks direct
execution and SQL PREPARE/EXECUTE against the same fixtures. It covers three
input sequences, repeated group keys, bare and wrapped aggregate ordering,
multiple projections, HAVING, and LIMIT.

All examples reset `@n=0` and use `eval_order(v INT)` with the stated input
sequence. Results without ORDER BY describe this fixture, not a guaranteed
SQL row order. The differing aggregate multisets below do not depend on order.

## Aggregate input order

With input `2,1,2`, this query returns totals `4,2` on MySQL and `3,3` on fsdb;
both leave `@n=3`:

```sql
SELECT SUM(@n:=@n+1) AS s FROM eval_order GROUP BY v;
```

The native result is consistent with incrementing the variable for each source
row before accumulating it into its group: group 2 receives 1 and 3; group 1
receives 2. fsdb collects groups first and evaluates their aggregates afterward,
so group 2 receives 1 and 2; group 1 receives 3. Adding `ORDER BY s` exposes the
same wrong totals with deterministic result ordering.

## Projection reuse and hidden ordering aggregates

For input `2,1`, the query below returns `1,2` and leaves `@n=2` on MySQL.
fsdb returns `1,3` and leaves `@n=4`:

```sql
SELECT SUM(@n:=@n+1) AS s FROM eval_order
GROUP BY v ORDER BY SUM(@n:=@n+1);
```

Wrapping the ordering aggregate in ABS changes the native contract: totals
become `2,4`, with `@n=4`. fsdb still returns `1,3`, with `@n=4`. Thus a repeated
bare projection and an aggregate nested in an ordering expression cannot share
one blanket reuse rule. With repeated input `2,1,2`, the wrapped native totals
are `4,8`, while fsdb returns `5,3`; both leave `@n=6`.

The native observations are consistent with evaluating hidden ordering
aggregate arguments before projected aggregate arguments for each source row.
They do not establish every plan's internal implementation or justify moving
all ordering evaluation ahead of projection.

## Returned projection side effects

For input `2,1,3`, this query returns `(9,10,3),(1,2,2),(5,6,1)` on both engines:

```sql
SELECT (@n:=@n+1) AS a,(@n:=@n+1) AS b,v FROM eval_order
GROUP BY v ORDER BY IF(a=b,v,-v);
```

MySQL finishes with `@n=6`, the last returned row's `b`; fsdb finishes with
`@n=12`. LIMIT 1 leaves `@n=10` on MySQL and `@n=12` on fsdb. LIMIT 0 leaves
`@n=0` on MySQL and `@n=12` on fsdb. The two-row fixture similarly ends at 6,
2, or 0 on MySQL depending on the limit, while fsdb remains at 8.

This is consistent with restoring stored projection-assignment values when
returning materialized rows. Merely skipping the final group's ordering
expression would not explain the three-group and LIMIT observations.

## Implementation boundary and validation

At `9a498be5`, `runGroupedSelect` collects source rows into groups, then `processGroup` calls
`projectGroup` before `orderKeysOf`. Each call to `rewriteAggregates` evaluates
its aggregate inputs over that group's stored rows. Sorting and LIMIT run after
projection and ordering evaluation; returned rows do not restore assignment
values. These are distinct stages with distinct observable differences.

The oracle passes all direct and SQL-prepared fixtures on a disposable native
MySQL 8.4.11 server with 64 MiB buffer and redo limits. Current-engine probes
use the embedded Debug assembly, .NET SDK 10.0.401, eight logical processors,
and a 4 GiB GC heap cap. The contracts distinguish scalar aggregate controls,
ordinary alias reuse, input-row accumulation, and returned-row side effects.


## Materialized aggregate inputs

Grouped input rows retain evaluated aggregate arguments before groups are folded.
Distinct syntax occurrences keep distinct values, even when their expressions
are structurally equal. Immutable source columns and literals read their retained
row/syntax value directly; routine variables and computed expressions are
materialized. Hidden ordering and HAVING inputs precede projected inputs. A bare
ordering aggregate matching a projection reuses its projected value.

The interleaved SUM, MIN/MAX, repeated projection, hidden ordering/HAVING, and
scalar aggregate controls now match MySQL. DISTINCT-count controls also pass.
Pure ROLLUP sums retain source arguments rather than replacing aggregate inputs
with the rolled-up output key. Numeric COUNT/SUM/AVG metadata reports binary
charset/collation with coercibility 5; the registry override check remains intact.

Validation: 3,043 tests pass with no build warnings/errors under a 4 GiB GC heap
cap. Native wire contracts pass 55 cases / 5,859 steps with zero differences at
`20261008T021834854-55098/contracts`. The maintained native oracle separately
passes direct and SQL-prepared fixtures, including the open boundaries below,
on disposable MySQL 8.4.11 with 64 MiB buffer and redo limits. It is not a claim
that fsdb matches every native-only fixture. No known-gap signatures were added.
The [grouped-input benchmark](../../benchmarks/results/b81d7e4f-grouped-inputs.md)
records the measured allocation tradeoff and unstable timing controls.

## Boundaries at source-order materialization

At `e8fe63ee`, input `2,1,2` exposes these family differences:

| Shape | MySQL | fsdb with materialized inputs |
|---|---|---|
| GROUP_CONCAT assignment, grouped and ordered by `v` | group 1: `1`; group 2: `3,2` | group 1: `2`; group 2: `1,3` |
| JSON_ARRAYAGG assignment, grouped and ordered by `v` | group 1: `[1]`; group 2: `[2,3]` | group 1: `[2]`; group 2: `[1,3]` |
| SUM assignment with ROLLUP | detail totals `2,10`, subtotal `9`, final `@n=6` | detail totals `2,4`, subtotal `6`, final `@n=3` |

The GROUP_CONCAT and JSON observations are consistent with native input grouping
order differing from the ordinary numeric accumulation path. GROUP_CONCAT also
orders equal keys differently in this fixture. ROLLUP exposes separate volatile
argument evaluations across subtotal levels. Materializing one value per source
row does not establish those contracts. The returned-projection assignment and
LIMIT differences above also remain open.

## Aggregate-family grouping strategy

The [grouping-plan oracle](../scripts/grouped-input-order-oracle.py) combines
`SUM(@n:=@n+1)` with a second aggregate over this interleaved fixture:

```sql
CREATE TABLE group_plan(id INT PRIMARY KEY,g INT,v INT);
INSERT INTO group_plan VALUES(1,2,10),(2,1,20),(3,2,30);
SET @n=0;
SELECT g,SUM(@n:=@n+1) AS s,COUNT(DISTINCT v) AS other
FROM group_plan GROUP BY g ORDER BY g;
```

Native MySQL 8.4.11 returns SUM totals 1 and 5 for groups 1 and 2, and leaves
`@n=3`. Without the DISTINCT count, it returns 2 and 4. The change applies to
SUM's input evaluation even though SUM itself is unchanged.

| Additional aggregate | SUM totals for groups 1, 2 | Native plan |
| --- | --- | --- |
| None; MIN(v); MAX(v); BIT_AND(v) | 2, 4 | Aggregate using temporary table |
| MIN(DISTINCT v); MAX(DISTINCT v) | 2, 4 | Aggregate using temporary table |
| COUNT(DISTINCT v); SUM(DISTINCT v); AVG(DISTINCT v) | 1, 5 | Group aggregate over sorted group keys |
| COUNT(DISTINCT 1); SUM(DISTINCT 1); AVG(DISTINCT 1) | 1, 5 | Group aggregate over sorted group keys |
| GROUP_CONCAT(v); GROUP_CONCAT(DISTINCT v) | 1, 5 | Group aggregate over sorted group keys |
| JSON_ARRAYAGG(v); JSON_OBJECTAGG(id,v) | 1, 5 | Group aggregate over sorted group keys |

The oracle checks exact rows and final variable state through direct execution
and SQL PREPARE/EXECUTE, and checks the distinguishing EXPLAIN TREE operators.
It passes on a disposable native MySQL 8.4.11 instance with 64 MiB buffer and
redo limits. Plan costs are deliberately not asserted.

At `e8fe63ee`, aggregate arguments are materialized in source arrival order for
all these families. The evaluator needs a query-wide grouping input strategy;
sorting only the JSON or DISTINCT aggregate's arguments would leave the adjacent
SUM wrong. MIN/MAX DISTINCT must not trigger that change. These fixtures do
not establish index-selected input order, multi-key collation ordering, or
ROLLUP's separate per-level evaluation. GROUP_CONCAT equal-key ordering also
remains a separate boundary.

## Group-key input evaluation

DISTINCT COUNT/SUM/AVG, GROUP_CONCAT, and JSON aggregates now select group-key
input order for every aggregate in the grouped query. Ordinary numeric,
MIN/MAX DISTINCT, and bitwise controls retain source arrival order. Scalar
aggregation retains source order. Both strategies use the same materialization
and aggregate evaluation functions; key sorting is shared with ROLLUP output.

MySQL can reuse ORDER BY directions when the complete ordering is a prefix of
GROUP BY. Aliases and ordinals resolve before matching; remaining group keys
sort ascending. A computed ordering expression, an additional aggregate term,
or reordered group keys instead leaves grouping input ascending and sorts the
finished results separately. Native EXPLAIN TREE and exact direct/prepared
results establish these boundaries, including multi-key prefixes.

For example, adding `ORDER BY g DESC` to the DISTINCT-count fixture produces
SUM totals 3 and 3, returned as groups 2 and 1. `ORDER BY g DESC,s` instead
produces 5 and 1: its extra aggregate term prevents input-sort reuse.

The final source gate passes 3,045 tests with no build warnings/errors under
a 4 GiB GC heap cap. The expanded wire contracts pass 56 cases / 6,195 steps with zero differences
at `20261008T023911056-667/contracts`. They include ascending and descending
family mixtures, hidden HAVING/ORDER BY aggregates, alias and positional
ordering, and volatile JSON_ARRAYAGG and unordered GROUP_CONCAT arguments.
The maintained native oracle also passes its multi-key plan and result checks.
No known-gap signatures are added.

JSON_ARRAYAGG's input-order difference above is resolved for these fixtures.
GROUP_CONCAT without internal ordering also matches; the internal equal-key
ordering difference is addressed by the GROUP_CONCAT ordering checks below. Volatile ROLLUP evaluations and returned projection
assignment/LIMIT behavior remain open. Index-selected input plans and wider
collation combinations need further evidence before claiming general plan
parity. The [performance snapshot](../../benchmarks/results/a975585d-grouped-order.md)
records the DISTINCT grouping path and ordinary controls.

## GROUP_CONCAT ordering and duplicate retention

The [native oracle](../scripts/group-concat-order-oracle.py) checks equal sort
keys, ascending/descending directions, explicit secondary keys, first duplicate
retention under an accent/case-insensitive collation, numeric and ENUM ordering,
binary collation, and NULL ordering keys. Every case passes direct execution
and SQL PREPARE/EXECUTE on disposable MySQL 8.4.11 with 64 MiB buffer and redo
limits.

For ids 1 through 8 with alternating keys 2 and 1, `GROUP_CONCAT(id ORDER BY k)`
returns `8,6,4,2,7,5,3,1`. Equal keys return in reverse arrival order even when
the ordering direction changes. An explicit secondary key overrides that tie
behavior. Fsdb previously retained arrival order within equal keys.

DISTINCT retains the first spelling and its source ordering keys before sorting.
For values `b,a,c,B,d,e,f,A` with those alternating keys, DISTINCT ordered by k
returns `e,a,f,d,c,b`. Sorting before deduplication selected the wrong retained
values. With no explicit ordering, DISTINCT returns `a,b,c,d,e,f`; numeric values
sort numerically and ENUM labels follow declaration order.

Fsdb now deduplicates before ordering, retains the first original value, and
reverses equal-key arrivals through the shared typed/collation-aware sorter.
The grouped assignment fixture now returns group 1 `1`, group 2 `3,2`, with
final `@n=3`, matching the maintained grouped-evaluation oracle. Byte truncation
and its warnings are also checked through the wire contract.

Volatile ROLLUP evaluations, returned projection assignment/LIMIT behavior,
and broader index-selected grouping plans remain separate open boundaries.

Validation: 3,047 tests pass with no build warnings/errors under a 4 GiB GC
heap cap. Native wire contracts pass 57 cases / 6,291 steps with zero differences
at `20261008T024704006-3122/contracts`. The standalone native ordering oracle
passes direct and SQL-prepared fixtures. No known-gap signatures are added.

## ROLLUP level evaluation and demand

The [ROLLUP oracle](../scripts/rollup-evaluation-oracle.py) uses five source rows
with `(g,h)` keys `(2,2),(1,1),(2,1),(1,2),(2,1)`. Each query resets `@n=0`.
It checks direct and SQL-prepared results and final variable state on native
MySQL 8.4.11 with 64 MiB buffer and redo limits.

For two grouping keys and `SUM(@n:=@n+1)`, MySQL evaluates each sorted input
at three levels: grand total, g subtotal, and detail. The first input contributes
1 to the grand total, 2 to its g subtotal, and 3 to its detail. Five inputs leave
`@n=15`; the grand total is 35 and the g subtotals are 7 and 33. Fsdb at
`e8ce7bd7` evaluates once per source row, leaves `@n=5`, and reports grand total
15 and g subtotals 6 and 9.

Multiple aggregate occurrences are evaluated aggregate-first, then level-first
for each row. With two SUM assignments, the first sorted input contributes
1/2/3 to the first aggregate's levels, then 4/5/6 to the second's. It does not
alternate aggregates separately within each level.

| Aggregate shape, two grouping keys | Native final counter | Evaluation observed |
| --- | ---: | --- |
| SUM assignment | 15 | Each subtotal level has its own argument value |
| SUM DISTINCT assignment | 15 | Same level-specific evaluations as SUM |
| MIN and MAX assignments | 30 | Each occurrence evaluates at all three levels |
| JSON_ARRAYAGG assignment | 15 | Arrays retain separate values at each level |
| COUNT DISTINCT assignment | 5 | One argument value per input, shared by levels |
| GROUP_CONCAT assignment | 5 | One argument value per input, shared by levels |
| SUM plus COUNT DISTINCT, or SUM plus GROUP_CONCAT | 20 | Three SUM values followed by one shared value per input |

The COUNT DISTINCT and GROUP_CONCAT standalone fixtures already match fsdb.
A blanket reevaluation of every aggregate at every level would regress them.
Conversely, treating all DISTINCT aggregates alike would leave SUM DISTINCT
incorrect.

| SUM query modifier | Native returned result | Native final counter |
| --- | --- | ---: |
| LIMIT 1 | First detail `(1,1,3)` | 3 |
| LIMIT 0 | No rows | 0 |
| LIMIT 1 OFFSET 2 | First subtotal `(1,NULL,7)` | 6 |
| HAVING GROUPING(g,h)=0 | Detail rows only | 15 |
| HAVING GROUPING(g,h)=3 LIMIT 1 | Grand total 35 | 15 |
| ORDER BY g DESC,h DESC LIMIT 1 | First detail `(2,2,3)` | 15 |
| ORDER BY s DESC LIMIT 1 | Grand total 35 | 15 |
| WHERE FALSE | No rows, including no grand total | 0 |

The descending key order also changes input evaluation with ROLLUP. Its detail
SUM values differ from an ascending-input calculation followed by an output
sort. Explicit ordering in these fixtures still evaluates all inputs before
LIMIT, while unordered LIMIT stops after enough groups have been emitted.
Fsdb currently evaluates every matching input before applying LIMIT and emits
a NULL grand-total row even for the empty-input fixture.

The native oracle passes all direct and SQL-prepared fixtures. Embedded fsdb
comparison at `e8ce7bd7`, under a 4 GiB GC heap cap and eight logical processors,
matches only the standalone COUNT DISTINCT and GROUP_CONCAT fixtures. These
native-only checks remain outside the passing differential contract lane; no
known-gap signatures are added.

The implementation needs both per-aggregate level ownership and demand-aware
group emission. Reusing a single materialized argument list at every subtotal,
or eagerly evaluating every level before LIMIT, cannot satisfy these contracts.
