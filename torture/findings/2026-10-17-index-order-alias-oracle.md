# ORDER BY duplicate-output alias oracle

Finding 124 in `security-findings.csv` claimed that `SELECT id, UPPER(name) AS id
... ORDER BY id` must return error 1052, and that a functional-index order plan
incorrectly bypasses that error. The claim does not match MySQL 8.4.11.

Using a disposable MySQL 8.4.11 container and the current fsdb server, both
engines accepted this setup:

```sql
CREATE TABLE t (
  id INT PRIMARY KEY,
  name VARCHAR(20),
  KEY ix_upper ((UPPER(name)))
);
INSERT INTO t VALUES (1, 'z'), (2, 'a');
SELECT id, UPPER(name) AS id FROM t ORDER BY id LIMIT 2;
SELECT *, UPPER(name) AS id FROM t ORDER BY id LIMIT 2;
```

Both SELECTs returned row 2 before row 1. The first projected `id` values
were therefore `2, 1`, while the computed `id` values were `A, Z`. This shows
that the computed alias controls ordering even when an unaliased column or
`*` produces another `id` output label. MySQL's EXPLAIN reported a table scan
with `Using filesort`; fsdb reported `ix_upper` index ordering. That physical
plan difference did not change the rows or their order.

The same two SELECTs also returned row 2 before row 1 on a table without the
functional index in both engines. The reported SQL case is an oracle-disproved
false positive, so no planner change is warranted from this finding alone.
