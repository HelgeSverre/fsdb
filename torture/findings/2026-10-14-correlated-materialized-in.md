# Correlated IN over a stable materialized source

Native MySQL 8.4.11 is the result oracle. For a materialized derived table
projecting integer `k`, outer rows `(lo, hi)` of `(1, 999)`, `(2, NULL)`,
`(NULL, NULL)`, and `(1, 1)` produce counts `2, 1, 0, 1` for
`k IN (lo, hi)`. Adding `OR k = hi + 1` produces `3, 1, 0, 2`.
The oracle's relevant setup and query are:

```sql
CREATE TABLE outer_rows(id INT PRIMARY KEY,lo INT,hi INT);
CREATE TABLE inner_rows(id INT PRIMARY KEY,k INT);
INSERT INTO outer_rows VALUES(1,1,999),(2,2,NULL),(3,NULL,NULL),(4,1,1);
INSERT INTO inner_rows VALUES(1,1),(2,2),(3,999),(4,1000),(5,NULL);
SELECT o.id,
  (SELECT COUNT(*) FROM (SELECT id+0 AS id,k+0 AS k FROM inner_rows LIMIT 1000) d
   WHERE d.k IN(o.lo,o.hi)),
  (SELECT COUNT(*) FROM (SELECT id+0 AS id,k+0 AS k FROM inner_rows LIMIT 1000) d
   WHERE d.k IN(o.lo,o.hi) OR d.k=o.hi+1)
FROM outer_rows o ORDER BY o.id;
```

The repeated bound contributes one row, and NULL bounds do not make a row
qualify. A numeric literal list `k IN (lo, 999)` produces `2, 2, 1, 2` and
uses the same lookup. A mixed numeric/string list `k IN (lo, '999')` produces
`2, 2, 1, 2`; it is intentionally left on the general evaluator because
the materialized numeric equality key is not a safe model for that coercion.
For `DECIMAL(10,2)` keys `1.10`, `2.20`, `3.30`, and NULL, MySQL returns
`2, 1, 0` for outer bounds `(1.10, 3.30)`, `(2.20, NULL)`, and `(NULL, NULL)`.

The focused regression uses 1,001 materialized rows, including a NULL key,
through both a derived table and a stable CTE. A separate decimal regression
checks exact decimal keys and candidate narrowing. Before candidate planning,
the `TOUCH` residual ran 3,006 times on an initial three-outer-row version of
the derived-table query, despite only three matching rows. A statement-local
equality lookup now unions the numeric IN keys by materialized row identity
and feeds the full SQL predicate only those candidates. The regression requires fewer
than 25 residual calls for the IN and complete OR forms on both source shapes.
Unsupported bound expressions and comparison domains retain the scan path.

This adds a common correlated membership shape; it does not turn arbitrary
materialized expressions into indexed keys or change MySQL's physical plan.
