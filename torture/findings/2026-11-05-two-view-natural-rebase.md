# Two writable views under a contended NATURAL JOIN

The maintained [two-view oracle](../scripts/two-joined-view-outer-oracle.py)
compared joined UPDATEs on Release fsdb and native MySQL 8.4.11. Two writable
views expose `id` and `link_id` as shared NATURAL JOIN keys, while `n` belongs
only to the first view and `bonus` only to the second. Qualified and bare
assignments update both physical targets. A read-only grouped source before
the views and a parenthesized natural prefix also match MySQL.

The independent-holder control starts separate transactions updating the two
physical target rows. A third transaction runs:

```sql
UPDATE va NATURAL JOIN vb_unique vb
SET n=n+1, bonus=bonus+1 WHERE id=1;
```

MySQL waits for both holders, reports two affected rows, and commits. Before
the fix, fsdb reported two affected rows but failed `COMMIT` with error 1205
after both holders released their rows. The write-preparation path claimed the
first view's physical target for the bare `n` assignment, but skipped the
second view's uniquely owned bare `bonus`. It could rebase after the first
claim while the second holder was still pending, leaving a stale transaction
base for commit.

Write preparation now resolves a bare assignment to a joined view when its
exposed column has exactly one known source owner. Unknown derived or CTE
source columns do not trigger this inference. The oracle's 49 UPDATE cases
and 27 contended rebases match MySQL after the change. A focused transaction
regression independently holds both rows and checks the final increments.
