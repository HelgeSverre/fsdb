# Temporary tables shadow permanent views

Status: resolved

Oracle: MySQL 8.4.11, using the digest pinned in `torture/compose.yaml`.

A session can create a temporary table with the same name as a permanent view:

```sql
CREATE TABLE shadow_source(id INT);
INSERT INTO shadow_source VALUES(42);
CREATE VIEW shadow_view AS SELECT id FROM shadow_source;
CREATE TEMPORARY TABLE shadow_view(label VARCHAR(12));
INSERT INTO shadow_view VALUES('temporary');
SELECT * FROM shadow_view;
```

The creating session reads the VARCHAR temporary row; another session reads
INT 42 from the view. Dropping the temporary table reveals the view again.
A binary prepared handle follows the same lifecycle and refreshes its result
metadata when the visible definition changes.

Explicit view operations address the permanent object. `SHOW CREATE VIEW`
returns its definition and `ALTER VIEW` changes it, including the normal
implicit commit, while the temporary table remains visible to ordinary queries.
A view cannot read a temporary table: MySQL returns 1352/HY000. If the hidden
permanent object is a table, `CREATE VIEW` returns 1050/42S01 and `ALTER VIEW`
returns 1347/HY000.

Temporary `DESCRIBE` and `SHOW [FULL] COLUMNS` return the literal string `NULL`
for an otherwise empty Extra field. Nonempty Extra values, such as
`auto_increment` and `STORED GENERATED`, retain their usual spelling.

`ContractCatalog.temporaryViewShadowing` compares this lifecycle over independent
connections and a retained binary prepared handle. QueryHandler regressions
cover session isolation, metadata, view DDL, hidden permanent-table collisions,
and absence of shared commit events for temporary writes.

The prepared query uses `SELECT * FROM shadow_view ORDER BY 1`. Ordinals count
expanded output columns, including columns after a qualified star; they do not
count an entire star as one expression. Dedicated QueryHandler regressions
cover ordinary star expansion and an expression following `t.*`.

The differential manifest at
`artifacts/runs/20261006T132939436-99417/contracts/manifest.json` records parity
for the expanded contract, including numeric errors and SQLSTATEs.
