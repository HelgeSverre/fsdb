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

## Remaining boundaries after materialization

For input `2,1,2`, the maintained family probes still differ:

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
