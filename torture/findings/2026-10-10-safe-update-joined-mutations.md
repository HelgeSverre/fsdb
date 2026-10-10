# Safe updates across a keyed join

MySQL 8.4.11 was probed on a disposable native server. With
`sql_safe_updates=ON`, a single-table `UPDATE` or `DELETE` without `LIMIT`
returns 1175 (`HY000`) unless the access path satisfies safe-update mode.
The audited single-table key predicates and `LIMIT` forms match in fsdb.

For `t(id INT PRIMARY KEY,v INT)` joined to `l(id INT PRIMARY KEY,v INT)` on
`l.id=t.id`, MySQL accepts `UPDATE t JOIN l ... SET t.v=1 WHERE l.v=0` and
`DELETE t FROM t JOIN l ... WHERE l.v=0`. It rejects the same update with no
`WHERE`, or with `WHERE t.v=0`, as well as writes targeting `l` under
`WHERE l.v=0`. Forcing `t` first with `STRAIGHT_JOIN` also returns 1175.
A target-key probe supplied by the join can therefore make a
filter on the lookup side safe, but the join alone is insufficient.
When `t` has two rows and `l` has one hundred, MySQL chooses the target scan
and returns 1175 for the same ordinary inner join.

Fsdb recognizes this two-table, inner-join form when the target's leading
B-tree key is equated to a column of the other source and a source-local
filter applies to that source. Native MySQL 8.4.11 accepts lookup filters
using `BETWEEN`, literal `IN`, ranges, `IS NOT NULL`, a self-comparison,
`NOT`, `<>`, or an OR whose branches both belong to the lookup source. It
rejects a cross-source OR and an arithmetic `l.value+0=0` filter in the
audited fixture. The fsdb rule follows that source-local boundary and still
requires the lookup row count to be no larger than the target row count,
matching the audited source choices. Bare filter columns uniquely owned by
the lookup table are resolved to that source; an ambiguous bare column still
returns MySQL's 1052 (`23000`) before safe-update evaluation, even when the
target has no rows.

For the same two-table inner join, unknown bare or qualified `WHERE` columns
return MySQL's 1054 (`42S22`) before safe-update error 1175, including when
the target is empty. The validator shares this small binding check with its
ambiguity handling.

Native MySQL 8.4.11 also accepts two-target UPDATE and DELETE with either
table's indexed `id=1` filter, and rejects an unindexed lookup-side filter
or an unfiltered two-target UPDATE with 1175. The existing fsdb validator
matches all seven audited multi-target cases; the differential contract and
focused regression now protect them. A three-table chain exposed a concrete
1175 mismatch: MySQL accepts a source-local filter on either lookup table
when indexed equality links lead back to the target, while fsdb previously
recognized only one join. Fsdb now follows those links through ordinary inner
joins. The pinned contract matches two accepted UPDATE filters and a DELETE,
and rejects a target-only filter or forced target-first `STRAIGHT_JOIN`; a
native probe also accepted the chain when the last lookup table had 100 rows.
MySQL accepts an indexed equality in `WHERE` for ordinary JOIN, CROSS JOIN,
comma join, and the audited three-table chain. An equality inside `OR`, or
one on unindexed columns, does not supply the key probe. MySQL also accepts
a source-local filter supplied only by `ON`, but rejects a target-only
`WHERE` filter even when `ON` filters the lookup. Fsdb follows these same
boundaries. MySQL also accepts `JOIN ... USING(id)` for the audited filtered
UPDATE, DELETE, and three-table chain, while rejecting target-only,
unfiltered, and target-first writes. Fsdb treats a `USING` column as an
indexed link when it exists on both sources and leads an index on the
earlier source.

An `ON`-filtered lookup treats a constant-true `WHERE` like no `WHERE`.
False or NULL conditions, including a false or NULL conjunct with an
unindexed predicate, cannot select a row and are accepted as no-ops.
False-OR and true-AND unindexed predicates still return 1175. Fsdb uses its
three-valued condition analysis to decide these cases without evaluating
row-dependent expressions.

The `sql_safe_updates` Expecto regression and the pinned MySQL 8.4.11
`safe-update-mode` contract cover the accepted and rejected forms, error
codes, and unchanged rows after rejection. All 794 safe-update steps passed
in run `20261010T095943924-52733`; the complete contract run had only the
nine documented identifier-case-policy differences on Linux mode 0. Other
multi-target access paths, filter shapes, and optimizer-dependent source
choices remain open.
