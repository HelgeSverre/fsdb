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
exposed column has exactly one known source owner. Unknown source columns do
not trigger this inference. The oracle's 49 UPDATE cases and 27 contended
rebases matched MySQL after the change. A focused transaction regression
independently holds both rows and checks the final increments.

## CTE source before both views

A CTE-first joined UPDATE exposes the same missing claim when the CTE precedes
both writable views:

```sql
WITH c AS (SELECT id FROM link)
UPDATE c JOIN va ON va.link_id=c.id
JOIN vb_unique vb ON vb.link_id=c.id
SET n=n+1, bonus=bonus+1 WHERE c.id=1;
```

MySQL again waits for both independently held physical rows and commits.
Fsdb previously reported two affected rows but failed COMMIT with 1205. The
ownership check treated the CTE's columns as unknown and declined to infer
the second view's bare `bonus` target. It now derives available CTE, derived,
and table-function source column names before deciding whether a bare column
has one owner. Unresolved source schemas still prevent the inference. The
focused transaction test covers both the direct and CTE-first paths.
The expanded oracle now matches 28 contended rebases.
