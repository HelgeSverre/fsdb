# Functional equality over stable materialized rows

Native MySQL 8.4.11 returns `(1,1)`, `(2,1)`, `(3,0)` for outer values
`10`, `999`, and `NULL` in this query against 1,000 inner rows with `k=id`:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT DISTINCT id,k FROM inner_rows) d
        WHERE ABS(d.k)=o.x)
FROM outer_bounds o ORDER BY o.id;
```

FSDB returns the same rows. Its stable materialized lookup covers exposed
columns, but does not build a statement-local key for the functional inner
expression. The reproducible [profile](../scripts/correlated-materialized-functional-profile.fsx)
places a `TOUCH(d.id)=d.id` residual before the equality: `d.k=o.x` evaluates
that residual 6 times, while `ABS(d.k)=o.x` evaluates it 4,004 times on the
same rows. The count includes the engine's metadata probe. The source is
materialized by `DISTINCT`, so a physical functional index on `inner_rows`
cannot directly serve the exposed `d` rowset.

A fix needs a safe statement-local functional key, with result-domain and NULL
semantics retained for the residual predicate. This is a scale gap, not a
result-membership discrepancy on the audited corpus.
