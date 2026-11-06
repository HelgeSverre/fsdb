# Recursive CTE with mixed natural outer joins to two writable views

The pinned MySQL 8.4.11 and Release fsdb oracle covers a recursive CTE
followed by two `NATURAL LEFT JOIN` edges to writable joined views, and a
mixed `NATURAL RIGHT JOIN`/`NATURAL LEFT JOIN` chain. Both engines now report
the same affected rows and final base-table values. This brings the two-view
oracle to 59 matching UPDATE cases, plus 34 contended rebase checks.

The mixed outer chain initially diverged:

```sql
WITH RECURSIVE c(id) AS
  (SELECT 1 UNION ALL SELECT id+1 FROM c WHERE id<3)
UPDATE c NATURAL RIGHT JOIN va NATURAL LEFT JOIN vb_unique vb
SET va.n=va.n+1, vb.bonus=vb.bonus+1
WHERE va.id=1;
```

`va` and `vb_unique` are joined views of separate physical tables, each
exposing `id` and a writable numeric column. MySQL updates both targets
and reports two affected rows. Before the fix, fsdb returned 1146 for
`probe.vb_unique`. After the first view was expanded, the view-moving pass
stopped at an intervening CTE `NATURAL LEFT JOIN` and refused to resolve a
grouped natural-left prefix. The second view therefore reached the generic
physical-table write path. The pass now traverses the active CTE source and
resolves the prefix through its logical columns. The oracle checks the final
rows and an independent holder on each physical target; both waits and the
rebase match MySQL.
