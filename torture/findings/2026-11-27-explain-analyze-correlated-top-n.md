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
ANALYZE reported the actual ordered lookups. The separate static
`EXPLAIN WITH` path still fails to resolve the audited materialized
CTE name.
