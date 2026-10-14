# Stable outer expressions in materialized correlated IN probes

MySQL 8.4.11 returns the same correlated membership results when an outer
column is wrapped in stable arithmetic. A native-server probe used outer rows
`(1,1,3)`, `(2,2,NULL)`, and `(3,NULL,1)` for `(id,a,b)`, with inner `k` values
`1`, `2`, `3`, and `NULL`:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT id+0 AS id,k+0 AS k FROM inner_rows LIMIT 100) d
        WHERE d.k IN(o.a+0,o.b+0))
FROM outer_bounds o ORDER BY o.id;
```

The result was `(1,2)`, `(2,1)`, `(3,1)`. Replacing the derived source with a
stable CTE produced the same rows. `NULL` bounds did not match the inner NULL.

FSDB already evaluated stable outer expressions as correlated equality and
range probe values, but its materialized `IN` lookup restricted bounds to
direct columns and literals. On a 1,000-row materialized source, both query
forms returned the correct rows but scanned broadly: a leading residual
`TOUCH(d.id)=d.id` was evaluated 4,004 times for the derived form before the
change. The lookup now accepts any bound that the existing outer-only,
statement-stable value checker can resolve. Unsupported or unstable expressions
still use the general execution path. A focused regression covers the derived
and CTE forms, verifies MySQL's result, and keeps residual calls below 20.
