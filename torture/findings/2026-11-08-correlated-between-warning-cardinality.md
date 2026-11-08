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

## Access-path follow-up (2026-11-08)

On the same pinned oracle, `EXPLAIN` reports `range` on `ix_k` with `Using
index condition; Using where`. `IGNORE INDEX(ix_k)` changes the inner access
to `ALL` with `Using where`. `FORCE INDEX(ix_k)` retains the range path and
the same warning count; reversing the two WHERE conjuncts does not change it.

| Inner access | Outer rows evaluated | Inner rows | 1292 warnings |
| --- | ---: | ---: | ---: |
| `range` on `ix_k` | 3 | 2 | 14 |
| `range` on `ix_k` | 1 | 2 | 6 |
| `range` on `ix_k` | 0 | 2 | 2 |
| `range` on `ix_k` | 3 | 0 | 2 |
| `ALL`, index ignored | 3 | 2 | 6 |
| `ALL`, index ignored | 1 | 2 | 2 |
| `ALL`, index ignored | 0 | 2 | 0 |

For this fixture, the indexed count is `2 + 4 × outer rows`, and the scanned
count is `2 × outer rows`. Replacing the upper truncated string with numeric
`13` yields seven warnings on the indexed three-row case, one per half of
the two-bound pattern. The same counts occur when the scalar subquery has
`LIMIT 1`, which prevents fsdb's grouped count path.

The two indexed warnings with zero outer rows or an empty inner table are
range-setup diagnostics, not row-filter diagnostics. A fix must represent
both phases and choose them from the actual access path; adding warnings per
materialized grouped row would fail the zero-row controls. These observations
are bounded to the fixture and do not establish a general warning formula for
all predicates or indexes.
