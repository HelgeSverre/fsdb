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
Other multi-target access paths, broader filter shapes, and optimizer-dependent
source choices remain open. The
`sql_safe_updates` Expecto regression and `safe-update-mode` differential
contract cover the accepted and rejected forms, error code, and unchanged
target rows after rejection.

The expanded `safe-update-mode` contract passed every step against the pinned
MySQL 8.4.11 image. The complete contract run reported nine differences,
all in the previously documented identifier-case-policy cases on Linux mode
0; none belonged to safe-update mode.
The expanded contract passed all 386 safe-update steps at
`torture/artifacts/runs/20261010T091140255-45246/contracts`; the complete run
again differed only in the nine identifier-case-policy steps.
The three-table extension passed all 446 safe-update steps at
`torture/artifacts/runs/20261010T091904371-46091/contracts`; the full run
again retained the same nine identifier-case-policy differences.
MySQL also accepts an indexed equality placed in `WHERE` for ordinary JOIN,
CROSS JOIN, comma join, and a three-table chain. It rejects the equality
inside an `OR` and an equality on unindexed columns in the audited fixture.
Fsdb now treats top-level conjunctive `WHERE` equalities as possible indexed
links between joined sources. The expanded pinned contract passed all 518
safe-update steps at
`torture/artifacts/runs/20261010T092632539-47054/contracts`; the full run
still differed only on the nine documented identifier-case steps.
MySQL also accepts a source-local lookup filter supplied only by `ON`, even
through a three-table indexed chain. When the same statement has a `WHERE`
filter only on the target, it returns 1175 in the audited fixture. Fsdb now
checks `ON` filters when `WHERE` is absent and keeps the existing `WHERE`
filter rule otherwise. The pinned contract passed all 578 safe-update steps
at `torture/artifacts/runs/20261010T093429487-49404/contracts`; the complete
run still retained only the nine identifier-case-policy differences.
For an `ON`-filtered lookup, MySQL treats an audited constant-true `WHERE`
like no `WHERE`; constant false or NULL produces a safe no-op. A plain
constant-true `WHERE` without a key still returns 1175, and adding a
target-only condition to the true constant does not make the joined write
safe. Fsdb uses its three-valued condition analysis for these conditions.
The pinned contract passed all 644 safe-update steps at
`torture/artifacts/runs/20261010T094036048-50439/contracts`; the complete run
again retained only the nine identifier-case-policy differences.
Native MySQL 8.4.11 also accepts an unindexed predicate conjoined with a
constant false or NULL because the condition cannot select a row; it rejects
the corresponding false-OR and true-AND unindexed predicates. A true
disjunct makes an `ON`-filtered keyed join behave like an absent `WHERE`.
Fsdb's existing three-valued condition analysis now recognizes these
boundaries without evaluating row-dependent expressions. The pinned contract
passed all 722 safe-update steps at
`torture/artifacts/runs/20261010T094631593-50877/contracts`; the complete run
retained only the nine documented identifier-case-policy differences.
