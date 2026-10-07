# Grouped right join operands

Status: partial implementation. Baseline `18eb1f6a` rejects a grouped right operand with
1064 / 42000. Left-associated parenthesized chains remain supported.

The [native oracle](../scripts/grouped-join-oracle.py) passes against disposable
MySQL 8.4.11:

```sh
python3 torture/scripts/grouped-join-oracle.py
```

The fixture has `a.id = {1,2}`, `b.id = {1,3}`, and `c.id = {3}`. Every query
selects `a.id,b.id,c.id` ordered by those columns.

| FROM clause | Native rows |
|---|---|
| `a LEFT JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id` | `(1,NULL,NULL)`, `(2,NULL,NULL)` |
| `(a LEFT JOIN b ON a.id=b.id) JOIN c ON b.id=c.id` | empty |
| `a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id` | empty |
| `a LEFT JOIN (b,c) ON a.id=b.id` | `(1,1,3)`, `(2,NULL,NULL)` |
| `a LEFT JOIN (b LEFT JOIN c ON b.id=c.id) ON a.id=b.id` | `(1,1,NULL)`, `(2,NULL,NULL)` |

The first two cases prove that removing parentheses and appending joins to the
flat chain changes outer-join results. The inner ON expression in
`a LEFT JOIN (b JOIN c ON c.id=a.id) ON a.id=b.id` cannot reference `a`;
MySQL returns 1054 / 42S22. The enclosing ON may reference both grouped sources.

Fsdb represents a grouped operand directly in `FromItem`. Execution preserves
its association, qualified source columns, inner ON scope, and NULL-extension
boundary. Regressions cover direct reads, mutations, lock checks, authorization,
CTE dependencies, EXPLAIN, full-text scope, stored-view rendering, and recovery.
Remaining work includes broader nested-source execution and prepared-metadata
verification.

## Nested USING column ownership

The native oracle also pins these projections against the same fixture:

| Query | Native rows |
|---|---|
| `SELECT * FROM a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id ORDER BY a.id` | `(1,NULL)`, `(2,NULL)` |
| `SELECT * FROM a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id ORDER BY a.id` | `(1,1)`, `(2,NULL)` |
| `SELECT * FROM a LEFT JOIN (b JOIN c USING(id)) USING(id) ORDER BY id` | `(1)`, `(2)` |

A group's logical output columns differ from its qualified source columns.
The first query exposes two `id` columns, while `b.id` and `c.id` remain
separately addressable. The enclosing USING in the third query consumes the
group's merged `id`. Retaining only physical source columns loses this
ownership. Projection and grouped right-side join conditions now consume a
logical column plan on both operands. Chained right-preserved keys have SELECT,
UPDATE, and DELETE regressions; deeper ON-scope interactions still need coverage.

## Mutation authorization diagnostics

A user with SELECT on all fixture tables and UPDATE/DELETE only on `target`
cannot mutate `b` inside `target JOIN (b JOIN c ON b.id=c.id)`.
The native oracle returns 1143 / 42000 for `SET b.id=4` and 1142 / 42000
for `DELETE b`. The grouped target must be discovered before authorization.
Joined UPDATE checks source access before assigned-column privileges. With
SELECT access but no UPDATE grant, the column denial is 1143; single-table
`UPDATE b SET id=4` instead returns table-level 1142. The regression pins both
forms and verifies rejected writes leave `b` unchanged. Table-level UPDATE
requirements remain as a fallback when an assignment has no resolved column
target.

## Stored view rendering

`CREATE VIEW grouped_using_view(aid,shared_id) AS SELECT * FROM a LEFT JOIN
(b LEFT JOIN c USING(id)) ON a.id=b.id` returns `(1,1)` and `(2,NULL)` on
MySQL and in the current grouped executor. The initial renderer produced
`SHOW CREATE VIEW` output that failed recreation with 1353 because star
expansion counted physical group columns. The regression drops and
recreates the rendered definition before comparing rows, rather than only
reading the original view. Execution and rendering now share logical-column
planning, and the round-trip and WAL/snapshot recovery regressions pass.

## Full-text source preparation

Native MySQL returns the matching document for both a full-text source before
an inner grouped operand and a full-text source inside that operand, including
with `LIMIT 1`. The streaming-order eligibility check only accepts physical
table operands. A grouped operand uses ordinary join execution and retains
prepared source overrides recursively, including synthetic full-text scores.
Regressions cover the former internal error for the outer source and the
missing score-column error for the nested source.

## Invalid USING resolution

A grouped USING key absent from one operand returns 1054 / 42S22; an
uncoalesced duplicate key in a grouped operand returns 1052 / 23000. Native
MySQL rejects both at query execution and PREPARE. The logical-column planner
returns explicit missing/ambiguous resolution errors. Metadata inference
returns no descriptor for these invalid shapes instead of throwing a .NET
exception, and execution retains the native error codes.

SQL PREPARE and binary preparation retain these errors through direct queries,
derived tables, CTEs, scalar subqueries, UNION branches, UPDATE, DELETE, and
INSERT…SELECT. Schema-only description distinguishes unavailable metadata from
a known invalid query, and preparation returns the latter without evaluating
row expressions or invoking application functions. Authorization runs before
schema validation; grouped mutation privilege errors match native preparation.
Regressions check both preparation paths, absence of a rejected SQL statement
handle, and unchanged rows after preparing a mutation.

PREPARE also validates ordinary and grouped ON references against their operands.
The native oracle and regressions cover unknown qualified and bare names,
ambiguous names, references to later joins, and references to siblings outside
a grouped operand. Subqueries inside ON retain that operand scope for their
own projections, predicates, and nested joins; errors retain the native clause
label. Valid merged USING keys and enclosing-query references remain accepted.

## Preparation scopes and CTE dependencies

Schema-only binding keeps qualified source columns and logical merged columns
in a separate scope for each query block. LATERAL sources receive preceding
tables, including tables preceding a grouped operand. Ordinary derived tables
receive enclosing-query scopes but cannot see sibling sources. Correlated
subqueries can refer to enclosing projection aliases where MySQL permits them.
SQL and binary preparation share the same checks without evaluating rows.

CTE descriptors are lazy and capture the namespace at their declaration.
Unused CTEs are not bound, including unused nested CTEs and definitions shadowed
by a nested WITH clause. Referenced CTEs in later UNION branches retain the
first branch's WITH namespace. Native acceptance and rejection cases cover
these boundaries.

This binding pass does not establish full PREPARE semantic parity or execution
parity for every accepted nested source.

## Relation-name diagnostics

The [relation-name oracle](../scripts/relation-name-oracle.py) checks ordinary
execution and SQL PREPARE against native MySQL 8.4.11. Duplicate table aliases
and duplicate local CTE declarations produce 1066 / 42000. Duplicate derived
or referenced CTE output columns produce 1060 / 42S21, preserving the spelling
of the repeated name. Grouped sources share their containing query block's
relation namespace; nested query blocks retain separate namespaces. Physical
tables from different databases and physical versus derived sources retain
the namespace distinctions accepted by MySQL.

Explicit CTE and stored-view column names apply before output-name uniqueness
is checked, so they may replace duplicate projection names. Unused CTE output
names are not forced. Recursive CTE members see the renamed anchor columns.
Shared checks cover schema-only preparation and execution; a describable
derived relation is rejected before application expressions run. Correlated
bodies whose standalone metadata is unavailable still use execution-time
validation. Regression tests check codes, SQLSTATEs, spelling, renamed rows,
and absence of callback side effects; stored-view recovery tests cover
explicit names over grouped USING projections.

## CTE body source origins

`WITH grouped AS (SELECT a.id AS aid,b.id AS bid FROM a LEFT JOIN
(b JOIN c ON b.id=c.id) ON a.id=b.id) SELECT aid,bid FROM grouped ORDER BY aid`
returns `(1,NULL)` and `(2,NULL)` on the native fixture. CTE origin tracking
visits each leaf source inside grouped operands; treating a group as one
qualified table caused an internal error after its rows were materialized.
The native oracle and query regression cover this CTE-body direction in
addition to CTE references inside a group.

## Mutation matching on merged keys

With target keys `1,2,3`, `b.id=1,3`, and `c.id=2,3`, the grouped operand
`b RIGHT JOIN c USING(id)` exposes logical keys `2,3`. Both native UPDATE and
DELETE through an enclosing USING join match targets `2,3`, including the
row where `b.id` is NULL. NATURAL joins have the same behavior. SELECT and
mutation execution share the resolved join-condition builder, so key
extraction and residual evaluation retain the group's merged expression.
The regression checks changed-row counts, resulting values, and surviving
target identities.

## Chained left-operand ownership

For `a.id=1,2`, `b.id=1,3`, and `c.id=3`, the query
`SELECT b.id FROM a RIGHT JOIN b USING(id) JOIN c USING(id)` returns `3`.
The second join reads the first join's merged key, even though `a.id` is NULL.
The same rule applies inside a grouped operand and in UPDATE/DELETE chains.
Join execution retains the left operand's association for logical-column
resolution; physical row layout and mutation identities remain separate.

## ON references to merged columns

`a RIGHT JOIN b USING(id) JOIN (SELECT 3 AS wanted) d ON id=d.wanted`
resolves `id` to the preceding join's merged column. The equivalent reference
into a grouped right operand follows the same rule. The native oracle and
regressions cover SELECT, UPDATE, and DELETE; they also retain error 1052 when
both operands expose `id`. ON resolution and projection rewriting share the
unique merged-column map, so only unambiguous logical names are substituted.

## Full-text conditions inside groups

`MATCH` in a grouped operand's ON condition belongs to the enclosing query
block. Native SELECT, UPDATE, and DELETE evaluate it against the indexed
source in that group. Expression discovery and score substitution traverse
all grouped ON conditions, while derived subqueries keep their own scope.
Mutation scoring also discovers physical leaves inside groups and retains
stored row identities beside score-augmented rows. The regression checks
selected rows, updated values, and the surviving document after DELETE.

## Merged-column metadata ownership

For `a LEFT JOIN (b LEFT JOIN c USING(id)) ON a.id=b.id`, MySQL reports
owners `a,b`. Replacing the inner join with RIGHT JOIN reports `a,c`; an inner
JOIN reports `a,b`. Explicit `COALESCE(b.id,c.id)` has no physical origin.
Prepared and executed metadata regressions pin these distinctions.

The logical-column plan distinguishes source columns from merged columns.
A merged key references the preserved operand directly: the right operand for
RIGHT JOIN, and the left for LEFT/INNER JOIN. This retains source metadata and
avoids treating a USING key as an ordinary computed COALESCE expression.

## Origins through derived tables and CTEs

Derived and CTE projections of `b RIGHT JOIN c USING(id)` retain `c` as the
original table, including aliased projections and explicit CTE column names.
Schema-only projection expansion and origin tracking use the same logical
SELECT column plan as execution and rendering. CTE origin inference resolves
its declared columns before materialization. Prepared and executed metadata
are checked against native MySQL for these relation boundaries.

## Source qualification

Source classification distinguishes one qualified relation from a grouped
operand. Single-source optimizations use optional qualifier resolution and
leave grouped operands to general execution. Leading groups are normalized
at execution boundaries without changing association. Metadata traversals
visit leaf sources where physical ownership is required. The partial
single-qualifier accessor is removed, and the solution builds without
incomplete-pattern warnings.

## Correlation inside grouped conditions

An outer reference inside a grouped ON condition makes the containing subquery
dependent. With the fixture above, `SELECT a.id,(SELECT COUNT(*) FROM b JOIN
(c JOIN (SELECT 1 AS seed) d ON c.id=a.id+1) ON 1) AS n FROM a ORDER BY a.id`
returns `(1,0),(2,2)`, and MySQL labels query block 2 `DEPENDENT SUBQUERY`.
Outer-reference detection traverses grouped conditions through the shared
join-condition helper. The regression checks both rows and EXPLAIN classification.

## Qualified outer-column fallback

The [correlated-column oracle](../scripts/correlated-column-oracle.py) verifies
that a local qualifier without the requested column does not hide an outer
qualified column. For outer `a.id` values `1,2` and `b.id=1`, the subquery
`SELECT COUNT(*) FROM (SELECT 1 AS other) a JOIN b ON a.id=b.id` returns `1,0`.
The inner alias `a` has no `id`, so its ON condition resolves the outer column.
Runtime lookup follows the same rule as schema-only preparation, including
multiple intervening scopes. A local `a.id` containing NULL wins over outer
values. Native results and an execution regression cover these cases.

## Outer-join result nullability

The [outer-join metadata oracle](../scripts/outer-join-metadata-oracle.py)
uses primary-key columns to distinguish declared nullability from NULL
extension. For `a LEFT JOIN (b JOIN c USING(id)) ON a.id=b.id`, projected
`a.id,b.id,c.id` have NOT_NULL flags `true,false,false`. A RIGHT join makes
the accumulated left operand optional instead. A merged USING key retains
the nullability of its preserved source, including when the containing group
is optional in an enclosing join.

Schema-only preparation and execution share the traversal of optional source
qualifiers. Execution clears NOT_NULL without discarding the physical source's
key flags. Regressions cover explicit projections, merged stars, LEFT/RIGHT
boundaries, and empty results. Computed expressions and broader nested-source metadata still need independent
coverage; the prepared source-flag boundary is covered below.

## Prepared physical and materialized source flags

The [prepared source-flag oracle](../scripts/prepared-source-flags-oracle.py)
reads native COM_STMT_PREPARE column-definition packets and ordinary result
flags. Direct projections
retain primary-key, unique/index, auto-increment, and no-default flags through
aliases. Optional outer-join columns drop NOT_NULL independently. Simple derived
projections retain these source flags, but LIMIT-derived tables and CTEs drop
them while retaining their physical origin names.

Column-source metadata distinguishes physical from materialized values. CTE
bindings retain that distinction after execution removes their WITH clause.
Preparation and execution read physical flags from the original column name
rather than the projection alias; materialized outputs clear source flags in
both paths. ROLLUP results drop physical key and auto-increment properties. The native fixture and regression cover LIMIT, DISTINCT, GROUP BY, HAVING,
and window boundaries. Scalar subqueries in the tested projections retain
physical source flags. Output names remain independent of inferred types, so
an unknown scalar descriptor does not discard a neighboring column's origin.
Optimizer-dependent materialization choices and broader
interactions with computed results remain subject to metadata verification.

## Grouped dependent sources

The [grouped lateral oracle](../scripts/grouped-lateral-oracle.py) verifies
LATERAL and JSON_TABLE references to preceding rows, including a dependent
source at the start of a group and an empty lateral result under LEFT JOIN.
Ordinary ON expressions inside the group still reject references to preceding
siblings outside that group.

Dependent groups prepare their source for each preceding row and use the same
join matcher as independent sources. Query and lateral contexts remain separate
so dependency evaluation does not widen ordinary ON scope. Regressions compare
ordered rows and the invalid ON error. Broader correlated source combinations
and dependent mutations remain outside this coverage.

LEFT LATERAL joins apply ON to candidate rows before deciding whether to emit
NULL padding. The oracle and regression cover false and NULL conditions, an
empty lateral body with a false condition, and a condition matching only one
preceding row. Padding bypasses ON, preserving every unmatched left row.

LATERAL uses the shared prepared-source join matcher for ON, USING, NATURAL,
and left-outer matching. Native fixtures cover merged stars for USING and
NATURAL joins, including empty dependent results under LEFT JOIN. RIGHT LATERAL sources execute once in the preceding scope, without the current
left operand. The preceding scope includes enclosing queries and earlier siblings
outside the grouped operand. Native fixtures cover right padding, merged
USING/NATURAL output, and correlation boundaries. SQL and binary preparation
reject left-operand dependencies while retaining legal preceding references.

LATERAL and ordinary derived sources share relation-body materialization.
Materialized columns retain available result collation IDs, including binary
collation from an enclosing source. The native fixture checks case-sensitive
comparison and COLLATION for local-source projections, direct correlated
projections, and correlated UNION ALL bodies. Every UNION branch receives the
same outer context, including the first branch. Broader computed metadata and
optimizer-dependent materialization behavior remain subject to verification.

## JSON_TABLE natural and right joins

The [JSON_TABLE join oracle](../scripts/json-table-join-oracle.py) covers
correlated NATURAL joins, NATURAL LEFT padding, independent RIGHT and NATURAL
RIGHT sources, and rejection of a right source referencing its left operand.
NATURAL/USING matching uses the shared logical join-condition resolver.
Independent right sources use the ordinary read or mutation matcher. Native
UPDATE fixtures verify affected rows and stored targets, including skipping
NULL-padded targets. Broader dependent mutation combinations remain uncovered.

JSON_TABLE argument references are checked during SQL and binary preparation
without evaluating the argument. The oracle distinguishes missing or ambiguous
columns in a visible preceding scope from qualified references with no visible
source: those report 1054/1052 and 1109 respectively. Runtime lookup retains the
table-function clause through outer scopes. Fixtures cover direct references,
nested COALESCE arguments, forward references, and excluded RIGHT operands.

Direct JSON_TABLE argument references are bound before row expansion, so an
empty preceding input does not suppress missing-column errors in reads or
updates. Preparation and execution share the reference traversal. The native
fixture repeats argument diagnostics with populated and empty input and checks
that a valid empty-input argument leaves a user-variable counter unchanged.

## LATERAL mutation sources

The [lateral mutation oracle](../scripts/lateral-mutation-oracle.py) verifies
independent and correlated UPDATE/DELETE sources, LEFT padding, NATURAL
matching, independent RIGHT sources, and duplicate lateral matches. Source
preparation feeds the shared mutation matcher, retaining physical target
identities and updating each target once. Lateral outputs have no writable
identity. Read-only source and view diagnostics use MySQL's 1288 wording.
Nested dependencies across grouped mutation operands remain uncovered.
