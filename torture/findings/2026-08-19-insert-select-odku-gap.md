# 2026-08-19 — INSERT ... SELECT ON DUPLICATE KEY UPDATE: bare select-column refs

Phase 2 write probe (disposable MySQL 8.4.11, port 3316, scratch db `p2probe`):

```sql
INSERT INTO dst2 (k, v) SELECT k, v FROM src AS s
ON DUPLICATE KEY UPDATE v = s.v;          -- accepted by MySQL 8.4.11
```

MySQL allows the ODKU clause on `INSERT ... SELECT` to reference the SELECT's
own columns by alias (`s.v` above) in addition to `VALUES(col)`. FSDB's
implementation deliberately supports only the `VALUES(col)` form — a bare
`s.v` in FSDB resolves as an (unknown) target-table column and errors, where
MySQL reads the select-derived value.

Deferred by design (Ast.fs `InsertSelect` ponytail comment names the ceiling):
`VALUES(col)` reaches every select-derived value, so alias refs add surface
without capability. If a torture run ever produces this divergence, minimize
it, review, and only then add its exact failure signature to
`support/known-gaps.json` per TORTURE-TESTING.md §"If deferring". No signature
exists yet — the ledger stays empty until a real run yields one.

Remaining pinned probe results (recorded in the Phase 2 Expecto tests):

- Per-row affected counts match plain ODKU: insert = 1, changing update = 2,
  no-op update = 0; in-batch duplicate source rows collide with their own
  fresh insert (src `(1,10),(2,20),(1,30)` into empty dst → 4 affected,
  last dup wins).
- `LAST_INSERT_ID()` after an insert-path upsert reports the first generated
  id; an update-only run leaves it unchanged.
