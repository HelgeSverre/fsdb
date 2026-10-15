# Correlated NOT BETWEEN with nullable outer bounds

Native MySQL 8.4.11 evaluates `NOT BETWEEN` with three-valued comparisons.
An unknown bound does not hide a decisive comparison against the other bound:

```sql
SELECT 5 BETWEEN NULL AND 3, 5 BETWEEN 10 AND NULL,
       5 BETWEEN NULL AND 10, 5 NOT BETWEEN NULL AND 3,
       5 NOT BETWEEN 10 AND NULL, 5 NOT BETWEEN NULL AND 10;
-- 0, 0, NULL, 1, 1, NULL
```

With indexed inner IDs 1–1,000 and outer `(id,lo,hi)` rows
`(1,10,990)`, `(2,NULL,990)`, `(3,10,NULL)`, this query returns counts
`(1,19)`, `(2,10)`, `(3,9)`:

```sql
SELECT o.id,
       (SELECT COUNT(*) FROM nums d
        WHERE d.id NOT BETWEEN o.lo AND o.hi)
FROM bounds o ORDER BY o.id;
```

Adding `OR d.id=500` inside the inner predicate gives counts 20, 11, and 10.
FSDB previously returned zero for the two NULL-bound rows because its
`BETWEEN` evaluator returned NULL whenever either bound was NULL. It now
combines both comparisons under SQL's three-valued AND rule, preserving a
decisive false comparison and its negation.

The same query also scanned broadly: an instrumented residual expression was
evaluated 4,004 times over the three outer rows before the planner change.
For indexed correlated candidates, `NOT BETWEEN` is now split into the union
of `id < lo` and `id > hi`; the original predicate remains the residual
filter. A focused regression checks the MySQL counts, fewer than 80 residual
calls, physical and pass-through sources, materialized derived and CTE sources,
an additional indexed OR branch, and an `IGNORE INDEX` scan control.
