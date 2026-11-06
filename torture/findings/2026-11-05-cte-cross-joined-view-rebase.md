# CTE-first CROSS JOIN update through a writable view

MySQL 8.4.11 waits on the physical row for a joined UPDATE whose first source
is a CTE and whose writable view follows a `CROSS JOIN`. After the owner
commits, the view-addressed writer increments the committed value. The same
behavior holds when a physical read source sits between the CTE and the view.

The [native oracle](../scripts/cte-joined-write-oracle.py) pins both cases
under contention. Before this change, fsdb's CTE-first view promotion handled
`INNER JOIN` but stopped at `CROSS JOIN`. The writer therefore ran without a
pre-execution physical row claim. The new transaction regression failed its
wait assertion. The promotion now recognizes both join kinds while retaining
the original `CROSS JOIN` in the rewritten source tree. Both contended cases
wait, rebase, and commit with the expected final physical row values. The
full `just check` run passed all 3,883 tests, and the focused regression
passed again after the test was deduplicated.

```sh
uv run --with pymysql python torture/scripts/cte-joined-write-oracle.py
dotnet run --project tests/Fsdb.Tests -c Release -- --sequenced --no-spinner \
  --filter-test-case 'CTE-first cross joined view update waits for its physical target'
```

This covers the audited CTE-first order and one intervening physical source.
Other dependent source and outer-join reorderings remain open.

## STRAIGHT_JOIN follow-up

MySQL also waits and rebases when the CTE first source uses `STRAIGHT_JOIN`
to reach the writable view. Before this follow-up, fsdb returned 1146 for
the CTE name after the mutation wrapper removed declarations, because the
view promotion stopped at `STRAIGHT_JOIN`. The promotion now includes that
join kind and the physical row claim occurs before the writer's snapshot.

The native oracle and transaction regression also give the CTE two matching
rows with distinct assignment values, in both orders. Both engines update
the view target once using the first source value in the audited cases.
This checks a result-order risk from moving the view into the rewrite's first
position; it does not establish all `STRAIGHT_JOIN` multi-match behavior.

## NATURAL JOIN follow-up

MySQL also waits and rebases when the CTE and the writable view use a
two-source `NATURAL JOIN`. Fsdb initially returned 1146 for the CTE; simply
promoting the view then returned 1052 because the view's hidden physical
join exposed a second `id` to the natural-key resolver. The rewrite now
identifies common names from the view's exposed columns and the CTE source,
then carries their qualified equalities into the flattened physical join.
An unqualified `WHERE id=1` resolves to the merged column. A second control
shares both `id` and `n` but deliberately mismatches `n`, yielding no update
on both engines. Promotion is limited to a first natural edge immediately
before the writable view; earlier natural edges still need their merged-column
ownership traced before the same rewrite can be applied.

## Later read source after the natural join

The first natural edge can also precede an ordinary joined read source.
The native oracle updates `c NATURAL JOIN v JOIN tail x` under contention;
fsdb previously returned 1146 for the CTE and now waits, rebases, and
updates the physical view target. The rewrite resolves the first natural
edge against the view's exposed columns while leaving the later join's ON
condition in its original scope. A bare `id` remains valid when the later
source has only `k`, but MySQL and fsdb return 1052 when that source exposes
another `id`. The next section covers a natural read edge before the view.

## Natural read edge before the writable view

MySQL 8.4.11 also waits and rebases when `c NATURAL JOIN r` precedes an
ordinary join to writable `v`, and when `c NATURAL JOIN r NATURAL JOIN v`
merges the same `id` across all three sources. Fsdb formerly left the CTE
unresolved or reported an ambiguous `id`. The view promotion now turns the
earlier natural edge into equalities between its original read operands before
moving the view, and resolves the three-source merged column against the
view's exposed `id`. A mismatched key in `r` yields zero affected rows on
both engines. The native oracle and contended transaction regression cover
both forms.

## Longer natural read chains

MySQL 8.4.11 also waits and rebases for `c NATURAL JOIN r NATURAL JOIN s`
followed by an ordinary join or a natural join to writable `v`. The prefix
rewrite now resolves each natural edge against the original accumulated read
columns and carries its equalities into the promoted view write. A mismatched
key in the second read source yields zero affected rows. A separate control
introduces `n` in the later read source: the final natural join compares both
`id` and `n` with the view, and stops matching after `v.n` changes. The native
MySQL oracle and Fsdb regression agree on these cases. The next section
covers an ordinary read source after the natural view join.

## Read source after the longer natural chain

The native MySQL oracle also waits and rebases when either the three-source or
four-source natural chain ends at `v` and an ordinary read join follows it.
Fsdb now promotes that view while retaining the later source. Its bare `id`
still names the merged natural column when the later source exposes only `k`;
both engines report 1052 when that source exposes another `id`. The contended
transaction regression covers both successful forms and the ambiguity case.
Later natural or outer joins, mixed outer-join prefixes, and broader
multi-match ordering remain open.

## Natural read source after the writable view

MySQL 8.4.11 also waits and rebases for `c NATURAL JOIN v NATURAL JOIN t`,
and for `c NATURAL JOIN r NATURAL JOIN v NATURAL JOIN t`. The later `t` shares
`id` with the accumulated natural column. Fsdb previously returned 1052 for
the bare `WHERE id=1`: the view rewrite recognized the first natural edge,
but its flattened physical join exposed another `id` before the later edge
could merge it.

The rewrite now resolves each edge of the contiguous natural prefix against
the view's exposed columns, carries qualified equalities into the physical
join, and retains the merged-column owner for the bare filter. Both contended
forms wait for the target row and increment its committed value. Moving `t.id`
away prevents the update; adding an ordinary later source with another `id`
still reports 1052. The maintained native oracle and focused transaction
regression cover these outcomes; the full `just check` run passed 3,884 tests.
Natural joins after an intervening ordinary
join, outer-join reorderings, and broader multi-match ordering remain open.

## Natural join after an ordinary read source

The [native oracle](../scripts/cte-joined-write-oracle.py) covers
`c NATURAL JOIN v JOIN tail x ON x.k=c.id NATURAL JOIN natural_tail t`.
MySQL 8.4.11 waits for the physical target row and rebases the view update
after the holder commits. Moving `t.id` away yields zero affected rows.
Fsdb previously returned 1052 (`Column 'id' in field list is ambiguous`)
for the bare `WHERE id=1`.

The joined-view rewrite now carries exposed column ownership across an
ordinary inner, cross, or straight join when that source introduces only new
column names. It can then resolve the later natural edge against the view's
exposed columns before flattening its physical sources. The focused fsdb
regression covers the successful update, mismatched key, and contended wait
and rebase. `just check` passed all 3,885 tests with zero build warnings.
An ordinary `JOIN link l` that introduces a second `id` before the later
natural edge returns 1052 on both MySQL and fsdb. The native oracle and the
focused fsdb regression pin that ambiguity, so the rewrite deliberately
stops carrying merged-key ownership at that source. Outer joins, other
duplicate-name arrangements, and longer mixed chains remain to audit.

## CTE-first natural outer join to a writable view

MySQL 8.4.11 waits and rebases when `c NATURAL LEFT JOIN v` or
`c NATURAL RIGHT JOIN v` updates the matched writable view row. An unmatched
left-side CTE row changes no view row; an unmatched CTE row under the right
join still updates the preserved view row. The
[native oracle](../scripts/cte-joined-write-oracle.py) covers both waits and
both unmatched cases.

Fsdb previously returned 1146 for the CTE name in the left-join case. When
the natural outer edge is the first join, view promotion now reverses its
direction along with the source order. The joined-view rewrite resolves the
natural keys against the view's exposed columns and turns the edge into an
explicit outer join before the view's physical sources are flattened. The
focused transaction regression covers both contended directions and the
unmatched cases. The full `just check` run passed 3,885 tests with zero
warnings before the final unmatched-right assertion was added; that focused
test passed again afterward.

An ordinary `JOIN` to a physical read source before the natural outer edge
also waits and rebases in both directions. MySQL and fsdb agree on the
unmatched cases: the left join changes no view row, while the right join
updates the preserved view row. Promotion converts the natural edge to an
explicit outer join against the preceding source group, retaining its shared
key and the bare merged-column binding. The native oracle and focused
transaction regression cover all four cases. If the preceding group exposes
two `id` columns, MySQL returns 1052 in the `from clause` for either outer
direction. Fsdb now promotes the writable view before checking the group, so
the same ambiguity wins over a missing-CTE error. Other duplicate-name
arrangements, outer-join orderings, and broader multi-match behavior remain
open.

## Earlier outer read join

MySQL also waits and rebases when the CTE has an earlier `LEFT JOIN` or
`RIGHT JOIN` to a physical read source before either natural outer edge to
the writable view. Fsdb now keeps that earlier join inside the moved source
group and builds the shared-key equalities against the exposed group columns.
The native oracle and focused regression cover both final directions after
both earlier directions, plus unmatched source cases. A nullable right side
that introduces the view's `link_id` key is included: when the read match is
removed, the final natural left join changes no view row, while the final
natural right join updates the preserved view row. All matched writes wait
for the physical target and rebase after the owner commits.

This covers a single earlier outer read join with distinct source columns.
A two-edge `LEFT JOIN` then `RIGHT JOIN` read prefix also matches MySQL. The
second source introduces `link_id`, so the natural view edge must compare
both the CTE's `id` and that nullable source's `link_id`. Both final outer
directions wait and rebase while matched. After the second read match is
deleted, the natural left edge changes no row and the natural right edge
updates the preserved view row. The two read sources both expose `k`; their
duplicate, unrelated name does not make the view's natural key ambiguous.
This case passed the native oracle and the focused fsdb transaction test.
Longer chains beyond this audited shape and broader multi-match behavior
remain to audit.

## USING read edge before the natural view edge

MySQL 8.4.11 also waits and rebases when the earlier read join merges `id`
with `USING(id)`. The native oracle covers inner, left, and right read joins
before a final natural left join, plus unmatched left and preserved-right
outcomes. Fsdb now asks the join group's logical-column resolver for each
shared key. That resolver retains the expression selected by `USING` and by
outer-join direction, rather than counting each physical source column as a
separate ambiguous name.

For a final natural left edge, bare merged references use the preceding
group's expression; for a final natural right edge, they use the view's
column. The multi-target control updates the `USING` read table when the
view has no matching row, while leaving the view untouched. MySQL and fsdb
both report one affected row. The focused transaction regression covers
these bindings and physical-row waits. Longer `USING` chains and other
mixed-name combinations remain to audit.
