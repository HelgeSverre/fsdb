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
