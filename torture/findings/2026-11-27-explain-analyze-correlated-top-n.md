# EXPLAIN ANALYZE for correlated top-N

Fsdb's execution plan now names the index used by a correlated ordered
lookup and reports how many times it opened and how many rows it yielded
per loop. The row count comes from the index stream before the residual
filter. In a query that also takes a fallback path for some outer rows,
the plan reports those invocations separately. Observations belong to
the query block, even when two blocks reuse the same table alias.
[MySQL EXPLAIN ANALYZE](https://dev.mysql.com/doc/refman/8.4/en/explain.html#explain-analyze)

In the [SQL fixture](2026-11-27-explain-analyze-correlated-top-n.sql),
MySQL 8.4.11 identifies the `recent` index in the lateral and scalar
queries, a table scan in the sibling with `IGNORE INDEX`, and the
unexecuted lateral input when the outer table is empty. Fsdb previously
showed all three populated inner accesses as `ALL`. Both Fsdb storage
modes now identify the observed index in the lateral and scalar cases,
keep the ignored sibling's scan estimate, and record no lookup for the
empty outer input. The SQL PREPARE control records the index for a bound
limit of one and records no lookup for a bound limit of zero. Fsdb
still shows an estimate for the empty inner input and the nested zero
limit.

| Fsdb access on the fixture | Index rows per loop | Index loops | Other loops |
| --- | ---: | ---: | ---: |
| Lateral with cutoff and offset | 6.00 | 4 | 1 |
| Reverse scalar with cutoff and offset | 3.75 | 4 | 1 |
| Indexed scalar beside an ignored sibling | 0.75 | 4 | 0 |

MySQL sorts the observed inner matches in this fixture. Fsdb reads them
in the requested index suffix order, so the two engines can report
different inner row and loop counts. Runtime details for other access
paths and MySQL's full iterator tree remain open.

## Static EXPLAIN

Replace `EXPLAIN ANALYZE` with `EXPLAIN` in the same SQL fixture to see
the selected index and the leading key parts bound by the outer query.
Fsdb reports `ref`, `recent`, and the references `o.tenant,const` with
the outer table's schema name. The lateral input is a dependent derived
block, including when an index hint forces it to scan. The scalar
queries keep their own plans when they reuse the same alias.

Both Fsdb storage modes matched MySQL 8.4.11 on the inner select type,
access type, key, key length, and references for all 192 variants, up from
64 matches at `45030f74`. These
varied signed and unsigned integer keys, binary and case-insensitive text
keys, scalar and lateral blocks, empty outer inputs, qualified and bare
outer references across schemas, forward and reverse index order, and
index hints.

JSON renders the references as separate entries and shows the access under
the dependent lateral input. TREE names the selected key. The runtime plan
remains responsible for reporting how many invocations seek or fall back for
individual values. Estimates for those probes and nested zero-limit prepared
plan details remain open. [MySQL EXPLAIN output](https://dev.mysql.com/doc/refman/8.4/en/explain-output.html)

### Materialized derived sources

Static `EXPLAIN` binds correlated keys from the output column definitions
of a derived source. A lateral source's output columns can depend on
preceding sources. The reference label uses the derived alias and
output column, such as `o.tenant`, and retains the schema prefix for a
physical source. A bare reference in the inner filter marks the
subquery dependent when only the outer scope supplies the column,
including when `IGNORE INDEX` forces an inner scan.

The wire comparison matched pinned MySQL 8.4.11 on select type, access
type, key, key length, and references in all 192 cases in memory and
with WAL. The baseline at `b977850b` matched 24. The cases combined
integer and collated text keys, scalar and lateral subqueries, bare
and qualified outer names, both index directions, and force and ignore
hints. Outer sources were limited projections, computed projections,
unions, nested derived tables, grouped queries, or a preceding
lateral source. JSON and TREE named the derived reference, and
ANALYZE reported the actual ordered lookups.

### Materialized common table expressions

`EXPLAIN WITH` resolves a CTE in its declaration scope and emits a
derived block for its definition. Repeated references reuse that block.
An unqualified CTE name shadows a physical table, while a schema-qualified
reference still names the physical table. Correlated probes use the
CTE's declared output columns and the alias at the point of reference.

All 192 indexed and ignored-index variants match MySQL 8.4.11 on
inner select type, access type, key, key length, and references in
memory and with WAL. The cases combine direct, renamed, unioned,
chained, and grouped CTEs or lateral projections that read a CTE,
with integer and text keys, scalar and lateral probes, bare and
qualified names, and both index directions. All 192 returned error
1146 at `1ebb8c6e`. The earlier derived-source comparison still
matches all 192 cases. JSON reports the CTE alias and its nested
body. ANALYZE records the observed ordered lookup for each CTE row.
MySQL's CTE merging, automatic temporary indexes, estimates, and
recursive plan details remain open.

### CTE scope across UNION branches

The `WITH` clause applies to every branch of a `UNION`. Validation
uses the same scope as execution. The plan collector shares CTE blocks
across top-level unions, derived and lateral union sources, and unions
inside CTE definitions. When JSON renders a second reference to the
same materialization, it emits `sharing_temporary_table_with` and the
original select ID.

The pinned MySQL 8.4.11 comparison reused the 192 CTE variants in
three forms. Each query used the same CTE in both branches of a
top-level union, a derived union, or a CTE whose body is a union.
All 576 returned error 1146 at `3e9f8ea1`. Both Fsdb storage modes
now match MySQL on the two inner select types, access types, keys,
key lengths, and references in every case. The 192 single-query
CTE comparisons still match. Ordinary missing tables in later
branches still report error 1146. Full plan estimates and the
select ID of a zero-limit UNION result can differ.

### Computed signed-integer prefixes

For `e.tenant = o.tenant + 1`, the pinned MySQL 8.4.11 plan uses the
`recent(tenant,state,stamp DESC,id)` index. Its key reference is
`func,const` when `state` is fixed. Fsdb now binds the outer expression
as an ordered equality key when the declared signed column ranges prove
that addition or subtraction stays within BIGINT. The original predicate
still decides row membership. NULL-safe equality can seek a NULL key,
and ordinary equality with a NULL bound returns no inner row.

On the 10,000-event, 50-request fixture, Fsdb's plan changed from `ALL`
to an ordered lookup that examined two inner rows per outer row. All 50
results matched between Release builds before and after the change. On
native macOS with both servers in memory, the median of seven alternating
query pairs fell from 34.84 ms to 8.76 ms. Each server ran three warm-up
queries before timing.

The pinned oracle also agrees on reversed equality, nested arithmetic,
INT boundaries, NULL-safe equality, repeated bounds, inner column names
shadowing outer names, and the ignored-index controls.

### Signed BIGINT overflow and empty reads

SQL addition and subtraction over signed BIGINT now raise error 1690
on overflow. The exact DECIMAL and unsigned controls retain their own
arithmetic domains. Integer equality contradictions and constant-false
filters can eliminate a read while still validating column and function
references. A standalone overflowing constant in WHERE can still fail
during condition evaluation.

For the audited correlated equality, an eligible ref lookup evaluates
the overflowing outer key before reading the inner rows. A competing
independent index with no matching key can finish the query first.
Ignoring the ref index also lets an exact local equality reject every
inner row on the scan. A local range predicate retains the error in
the corresponding control.

Projection and sort expressions can overflow on the synthetic row used
to describe an empty result. Fsdb now validates those expressions
in that row's binding scope and defers the error to evaluation on
actual result rows. The pinned MySQL 8.4.11 replay matched all 89
observations, including grouped and windowed empty reads, prepared
limits, wire column types, FOUND_ROWS, and writes.
