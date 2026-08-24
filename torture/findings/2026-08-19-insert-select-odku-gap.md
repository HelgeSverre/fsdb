# 2026-08-19 — INSERT ... SELECT ON DUPLICATE KEY UPDATE: bare select-column refs

Status: resolved on 2026-08-24.

Phase 2 write probe (disposable MySQL 8.4.11, port 3316, scratch db `p2probe`):

```sql
INSERT INTO dst2 (k, v) SELECT k, v FROM src AS s
ON DUPLICATE KEY UPDATE v = s.v;          -- accepted by MySQL 8.4.11
```

MySQL allows the ODKU clause on `INSERT ... SELECT` to reference columns from
the SELECT source (`s.v` above) in addition to `VALUES(col)`. fsdb now carries
those typed source values through duplicate handling without rerunning the
SELECT. Qualified projected and unprojected columns work through direct,
derived, and joined sources, including correlated assignment subqueries.
Internal source bindings do not participate in `DISTINCT`, so they do not
change source cardinality.

Source/target ambiguity returns 1052. Projection aliases and grouped source
references return 1054, matching the pinned MySQL 8.4.11 results. No known-gap
signature was enrolled for the original divergence.

Remaining pinned probe results (recorded in the Phase 2 Expecto tests):

- Per-row affected counts match plain ODKU: insert = 1, changing update = 2,
  no-op update = 0; in-batch duplicate source rows collide with their own
  fresh insert (src `(1,10),(2,20),(1,30)` into empty dst → 4 affected,
  last dup wins).
- `LAST_INSERT_ID()` after an insert-path upsert reports the first generated
  id; an update-only run leaves it unchanged.
