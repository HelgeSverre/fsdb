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

## Larger ordinary buckets

With eight rows per `grp` instead of two, the two-row shortcut does not apply.
The same 1,000-row query and shared functional key returned `3996972`. Warm
samples on the native Release server were 574, 538, 540, and 542 ms before
comparing functional row IDs with the ordinary candidate. After the change and
runtime warm-up, six samples were 83, 85, 84, 85, 82, and 82 ms. The
ordinary-equality-only control was about 29 ms before and 33 ms after.

The planner now intersects functional row-ID sets first. It loads functional
rows only when their combined set is smaller than the best ordinary candidate;
the full WHERE predicate still decides membership. A separate 16-row oracle
has one rare name and seven common names in an eight-row group. MySQL 8.4.11
and fsdb both return counts of one and seven, respectively. The focused
regression also checks that the selective functional bucket still bounds
residual evaluation.
