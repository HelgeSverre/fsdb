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
B-tree key is equated to a column of the other source and a qualified
source-local filter applies to that source. Native MySQL 8.4.11 accepts lookup filters
using `BETWEEN`, literal `IN`, ranges, `IS NOT NULL`, a self-comparison,
`NOT`, `<>`, or an OR whose branches both belong to the lookup source. It
rejects a cross-source OR and an arithmetic `l.value+0=0` filter in the
audited fixture. The fsdb rule follows that source-local boundary and still
requires the lookup row count to be no larger than the target row count,
matching the audited source choices.

Multi-target writes, broader filter shapes, and other optimizer-dependent
source choices are still outside this rule. The
`sql_safe_updates` Expecto regression and `safe-update-mode` differential
contract cover the accepted and rejected forms, error code, and unchanged
target rows after rejection.

The expanded `safe-update-mode` contract passed every step against the pinned
MySQL 8.4.11 image. The complete contract run reported nine differences,
all in the previously documented identifier-case-policy cases on Linux mode
0; none belonged to safe-update mode.
