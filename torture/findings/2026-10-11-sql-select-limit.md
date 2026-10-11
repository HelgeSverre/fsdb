# Session and global `sql_select_limit`

A disposable native MySQL 8.4.11 server reports `sql_select_limit` as an
unsigned session/global variable defaulting to 18446744073709551615.
`SET SESSION sql_select_limit=2` caps a top-level three-row SELECT or UNION
at two rows. An explicit `LIMIT 3` overrides it. A scalar subquery still sees all
three source rows, as do `INSERT … SELECT` and `CREATE TABLE … AS SELECT`;
the implicit cap belongs to the outer result-producing query. A prepared
statement observes the value at execution, not preparation: the same
prepared UNION returned two rows at setting 2 and three at setting 3.
`SQL_CALC_FOUND_ROWS` returned two rows while `FOUND_ROWS()` reported three.

At zero, MySQL returns no rows for ordinary SELECT and the audited
SELECT-shaped metadata commands (`SHOW VARIABLES`, `SHOW STATUS`,
`SHOW DATABASES`, `SHOW TABLES`, `SHOW TABLE STATUS`, `DESCRIBE`,
`SHOW COLUMNS`, `SHOW ENGINES`, `SHOW PLUGINS`, `SHOW CHARACTER SET`,
`SHOW COLLATION`, `SHOW OPEN TABLES`, `SHOW TRIGGERS`, `SHOW EVENTS`,
`SHOW PROCEDURE STATUS`, and `SHOW INDEX`). `SHOW WARNINGS`, `SHOW GRANTS`,
`SHOW PRIVILEGES`, `SHOW PROCESSLIST`, `SHOW BINARY LOGS`, and
`SHOW CREATE TABLE` retain their rows. This distinction drives the probe whitelist in fsdb.

MySQL rejects string, NULL, fractional, and out-of-range assignments with
1232. It clips `-1` to zero with warning 1292. `SET SESSION ...=DEFAULT`
reads the current global value, while a GLOBAL assignment seeds later
sessions without changing the current one. Fsdb uses the existing bounded
unsigned-variable normalization and applies the implicit cap only where the
native oracle observed it. The focused QueryHandler regression covers the
query forms, metadata commands, assignment diagnostics, and scope reset.
MySQL also shows a zero-limit synthetic plan for `EXPLAIN SELECT` in
traditional, JSON, and TREE formats. A nonzero implicit limit is visible as
a `Limit` node in TREE (or `Limit/Offset` when an offset is present), while
traditional and JSON plans keep the underlying table access. An explicit
`LIMIT` overrides the session value. MySQL still validates missing tables
under a zero limit. Fsdb now matches these SELECT plan distinctions and
keeps the table validation. A distinct UNION under zero limit keeps its
traditional branch rows plus a `UNION RESULT` row with `Extra = Zero limit`,
and TREE reduces to the synthetic zero-row plan. An unordered `UNION ALL`
has no traditional result row; TREE retains a limit over Append. A global
`ORDER BY` materializes even `UNION ALL`, yielding the zero-limit result
row and TREE's synthetic zero-row plan. Fsdb now follows those distinctions.
For plain UNION branches, JSON now nests each branch under
`union_result.query_specifications`, reports `using_temporary_table`, and
adds the zero-limit message only to a materialized result. The branch access
description is retained. An audited derived SELECT with its own explicit
LIMIT now nests under its owning branch's `materialized_from_subquery`
object, retaining the child's select ID and access description. Mergeable
and lateral derived sources, derived set operations, and other expression
subqueries still use the general JSON renderer until their ownership and
dependence are audited. MySQL cost details and exact ANALYZE timing
presentation also remain open.

A follow-up native 8.4.11 probe used a disposable table and a zero session
limit to inspect scalar projection subqueries inside UNION branches. MySQL
places an uncorrelated scalar subquery under its owning branch's
`optimized_away_subqueries` array with `dependent: false` and
`cacheable: true`. A correlated projection subquery appears under the
owning query block's `select_list_subqueries` array with `dependent: true` and
`cacheable: false`. Both retain the child `query_block.select_id` and access
description. A separate native probe confirmed the same placement for a
single SELECT without UNION. Fsdb now nests these audited projection cases
under their owning SELECT or UNION branch, including a scalar in a later branch.

Further native clause probes showed that a correlated scalar in a single-table
`WHERE` predicate appears in the table's `attached_subqueries` array. Fsdb now
uses that placement for audited dependent scalar predicates. MySQL can instead
fold an uncorrelated `WHERE` scalar into `optimized_away_subqueries`, and can
eliminate `EXISTS` and `IN` subquery blocks through semijoin planning, so those
cases remain outside this renderer path. A dependent `HAVING` scalar appears
under `grouping_operation.having_subqueries`, and a dependent `ORDER BY` scalar
under `ordering_operation.order_by_subqueries`; fsdb now renders both for
audited single-table plans. A projected scalar moves into the corresponding
grouping or ordering operation, or into `duplicates_removal` for `DISTINCT`;
fsdb also retains that ownership in the audited simple cases. For an indexed
GROUP BY followed by ORDER BY, native MySQL nests `grouping_operation` inside
`ordering_operation`; projected and HAVING scalars stay with grouping, while
an ORDER BY scalar belongs to the outer ordering operation. Fsdb now composes
those two wrappers for the audited dependent cases and reports a filesort for
the scalar order key even when its traditional plan row omits that flag.
Independent ORDER BY scalars can appear under the operation's
`optimized_away_subqueries`, while independent HAVING scalars can have an
optimized-away child block. DISTINCT with ordering, subqueries in more than
one clause, nested scalar blocks, mixed derived/scalar plans, and broader
optimizer placement remain open.
