# CHECK OPTION with duplicate derived join partners

MySQL 8.4.11 marks an inner-join view as updatable when a nonmergeable derived
source returns two rows for one physical target. `UPDATE view SET n=n+1`
changes the target once and reports one affected row.

For a checked view with derived flags 7 and 8, MySQL rejects `UPDATE ... SET
n=7` with error 1369 when `WHERE d.flag>a.n`: the original flag-7 partner
would fail, even though the flag-8 partner would still satisfy the view. The
same rejection applies when `d.flag>a.n` is part of the join condition and
the candidate update removes the flag-7 partner from the join. Neither
rejected write changes the target. The [native oracle](../scripts/composed-join-view-oracle.py)
checks both source-row insertion orders alongside the existing composed
join-view matrix.

FSDB previously accepted the predicate case because its joined-view check
required only one retained candidate partner to pass. It now requires every
original partner to remain joined and satisfy the check for an UPDATE.

The same oracle finds a related explicit-column INSERT rule on a direct join
view. With source flags 7 and 8, inserting `n=7` fails with 1369 when
`WHERE b.flag>n` because one resulting join row fails; inserting `n=6`
succeeds. If that comparison is instead in `ON`, the flag-7 row is excluded
from the joined result and inserting `n=7` succeeds. FSDB previously accepted
the failing WHERE case because it required only one checked join row to pass.
It now requires a nonempty joined result whose every row passes. The focused
regression covers the ordinary update, both checked update rejections, and
these rejected and accepted INSERT forms.
