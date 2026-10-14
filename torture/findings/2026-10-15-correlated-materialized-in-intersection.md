# Intersecting correlated IN probes over materialized rows

Native MySQL 8.4.11 returns two rows for a correlated count over 1,000 inner
rows when one `IN` predicate selects 500 rows, another selects 400, and their
sets overlap at IDs 499 and 500. The inner source is materialized with a
`LIMIT`; the outer row supplies both bounds for each predicate.

```sql
SELECT (SELECT COUNT(*)
        FROM (SELECT id+0 AS id,a+0 AS a,b+0 AS b
              FROM inner_rows LIMIT 1000) d
        WHERE d.a IN(o.a1,o.a2) AND d.b IN(o.b1,o.b2))
FROM outer_bounds o;
```

The focused regression uses `a=1` on IDs 1–500 and `b=1` on IDs 499–898;
all other values are distinct from the outer bounds `(1,999,1,999)`. It
checks both a derived table and a stable CTE. Before the change, FSDB used
only the first compatible `IN` group and evaluated a leading residual
`TOUCH(d.id)=d.id` 502 times. The materialized planner now builds every
compatible statement-local `IN` group and intersects their source-row
identities before residual evaluation. Both queries return MySQL's count of
two while keeping residual calls below 20.

This applies to compatible direct outer-column or literal bounds on stable
materialized sources. Other correlated expressions retain the general
execution path.
