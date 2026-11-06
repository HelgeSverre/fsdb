# DISTINCT and windowed read sources beside a writable joined view target

The native MySQL 8.4.11 composed-view oracle now includes two additional
read-only derived sources before a physical target:

```sql
SELECT a.id,a.n,d.flag
FROM (SELECT DISTINCT id,flag FROM b) d
JOIN a ON a.id=d.id;

SELECT a.id,a.n,d.flag
FROM (
  SELECT id,ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag
  FROM b
) d JOIN a ON a.id=d.id;
```

For both shapes, MySQL reports the outer view as updatable, updates the
physical target once despite multiple joined rows, rejects an invalid
`WITH CASCADED CHECK OPTION` update with 1369, refuses `INSERT` with
1471, and refuses `DELETE` with 1395. The maintained native oracle passes
all 35 composed-view shapes after these additions.

Focused fsdb regressions confirm the same updatable metadata, derived
rowset, single target update, and atomic CHECK OPTION rejection for both
new sources. Both focused tests pass. This closes the audited DISTINCT
and window-derived source shapes; other source orders and compositions
remain to verify.
