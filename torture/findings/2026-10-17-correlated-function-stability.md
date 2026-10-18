# Correlated probe keys must preserve function calls

MySQL 8.4.11 evaluates a nondeterministic stored function for each candidate
row in this correlated subquery:

```sql
CREATE TABLE outer_t(id INT PRIMARY KEY);
CREATE TABLE inner_t(id INT PRIMARY KEY, k INT, KEY ix_k(k));
INSERT INTO outer_t VALUES (1),(2),(3);
INSERT INTO inner_t VALUES
  (1,1),(2,1),(3,2),(4,2),(5,3),(6,3),(7,4),(8,4),(9,5),(10,5);
CREATE FUNCTION bump(x INT) RETURNS INT NOT DETERMINISTIC NO SQL
  RETURN x + (@calls := @calls + 1)*0;
SET @calls=0;
SELECT o.id, (SELECT COUNT(*) FROM inner_t i WHERE i.k=bump(o.id))
FROM outer_t o ORDER BY o.id;
SELECT @calls;
```

Both engines return `(1,2),(2,2),(3,2)` from the correlated SELECT. The
digest-pinned MySQL 8.4.11 container reports `@calls=30`; fsdb previously
reported `@calls=3` because it treated the function as a stable outer key and
probed `ix_k` once per outer row. That changed observable session state.

The planner now accepts a scalar as statement-stable only when it is the
unmodified built-in callback, an explicitly marked pure session wrapper, or
a deterministic registered extension. Stored SQL functions, unannotated
custom scalars, and advisory-lock functions retain row-by-row evaluation.
Focused regressions cover the stored-function call count and an unannotated
custom scalar; existing indexed-correlated cases continue to check the
fast path for pure expressions.
