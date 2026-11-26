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
different inner row and loop counts. Static EXPLAIN, runtime details for
other access paths, and MySQL's full iterator tree remain open.
