# Recursive CTE with mixed natural outer joins to two writable views

The pinned MySQL 8.4.11 and Release fsdb oracle now covers a recursive CTE
followed by two `NATURAL LEFT JOIN` edges to writable joined views. Both
engines report the same affected rows and final base-table values. This
brings the two-view oracle to 58 matching UPDATE cases, plus 33 contended
rebase checks.

A mixed outer chain remains divergent:

```sql
WITH RECURSIVE c(id) AS
  (SELECT 1 UNION ALL SELECT id+1 FROM c WHERE id<3)
UPDATE c NATURAL RIGHT JOIN va NATURAL LEFT JOIN vb_unique vb
SET va.n=va.n+1, vb.bonus=vb.bonus+1
WHERE va.id=1;
```

`va` and `vb_unique` are joined views of separate physical tables, each
exposing `id` and a writable numeric column. MySQL updates both targets
and reports two affected rows. fsdb returns 1146, `Table
'probe.vb_unique' doesn't exist`, although the view exists and is used
successfully in adjacent inner and left-join cases. The oracle pins this
outcome at its end, after the matching cases, so the difference is
reproducible without leaving divergent state in the rest of the suite.

The likely area to inspect is view expansion or dependency resolution after
the `NATURAL RIGHT JOIN` has changed the merged key owner. That is a lead,
not an established cause.
