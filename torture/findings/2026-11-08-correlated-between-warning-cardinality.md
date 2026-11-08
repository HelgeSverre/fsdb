# Correlated `BETWEEN` conversion warning cardinality

Pinned oracle: MySQL 8.4.11 (`torture/compose.yaml`). The rows below match in
fsdb, but the statement diagnostics do not.

```sql
CREATE TABLE outer_rows(id INT);
CREATE TABLE inner_rows(owner_id INT, k DOUBLE, payload INT, KEY ix_k(k));
INSERT INTO outer_rows VALUES (1),(1),(2);
INSERT INTO inner_rows VALUES (1,12.5,7),(2,12.5,8);

SELECT o.id,
       (SELECT COUNT(i.payload) FROM inner_rows AS i
        WHERE i.owner_id=o.id
          AND i.k BETWEEN '12.5x' AND '13x') AS c
FROM outer_rows AS o ORDER BY o.id;
SHOW COUNT(*) WARNINGS;
```

The result rows are `(1,1),(1,1),(2,1)`. MySQL emits 14 warning 1292 conditions,
seven for each truncated bound. Fsdb emits four: the grouped count plan
evaluates the local `BETWEEN` once for each inner row. Adding `LIMIT 1` to the
scalar subquery prevents that plan; MySQL still emits 14 warnings while fsdb
emits six. The standalone grouped inner query produces six MySQL warnings.

This is a low-impact observable divergence in `SHOW WARNINGS`, not a count
result error. The warning count depends on candidate scanning and predicate
ordering; a statement-wide deduplication rule would be wrong. The next step is
to characterize MySQL's access path across indexes, hints, row counts and
predicate order, then add an exact regression before changing the planner.

Without `ix_k`, the same oracle produces six warnings in each correlated
variant and four in the standalone grouped query. Fsdb's no-index grouped
path still emits only four warnings; its ordinary path emits six. The index
therefore changes MySQL's warning count even though the result rows stay the
same.
