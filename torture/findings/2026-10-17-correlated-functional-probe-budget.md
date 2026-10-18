# Correlated functional probes after a small ordinary lookup

A correlated equality on an ordinary index can identify two rows before a
functional predicate is evaluated. The executor previously probed the
functional index as well, even when its equality bucket covered nearly the
whole table. For the fixture below, each outer row has a two-row `grp` bucket
but all 1,000 rows share the same `LOWER(name)` key.

```sql
CREATE TABLE t (
  id INT PRIMARY KEY,
  grp INT,
  name VARCHAR(30),
  KEY ix_grp (grp),
  KEY ix_lower ((LOWER(name)))
);
-- 1,000 rows: id = 1..1000, grp = FLOOR(id / 2), name = 'common'.
SELECT SUM((SELECT SUM(i.id) FROM t i
            WHERE i.grp = o.grp AND LOWER(i.name) = LOWER(o.name)))
FROM t o;
```

The query returns `999999` on the fixture. In a native Release fsdb server on
the same Mac, warm samples before the two-row shortcut were 523, 515, 516,
and 544 ms. After the shortcut, warm samples were 166, 156, 161, and 158 ms.
The ordinary-equality-only control varied from about 25 to 80 ms across these
runs, so the raw times should not be interpreted as a controlled engine ranking.
The improvement shows that avoiding the redundant functional bucket removes a
substantial part of this query's work; residual `LOWER` evaluation and other
per-row costs remain.

MySQL 8.4.11 and fsdb both returned `(1,1), (2,1), (3,2), (4,2)` for a
four-row variant with two different names in one group. The Expecto regression
preserves that residual-filter behavior. Larger ordinary candidate sets still
use the existing candidate comparison; this change does not claim a general
functional-probe budget.
