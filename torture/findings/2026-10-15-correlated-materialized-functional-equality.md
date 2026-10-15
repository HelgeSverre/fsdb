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

FSDB returns the same rows. The initial profile found that the stable
materialized lookup covered exposed columns but not `ABS(d.k)`: with a
`TOUCH(d.id)=d.id` residual before the equality, `d.k=o.x` evaluated the
residual 6 times and `ABS(d.k)=o.x` evaluated it 4,004 times. The count
includes the engine's metadata probe. The source is materialized by
`DISTINCT`, so a physical functional index on `inner_rows` cannot directly
serve the exposed `d` rowset.

The current implementation inverts `ABS` over supported signed integer
columns to one or two statement-local equality probes, retaining the original
predicate as a residual check. Replaying the
[profile](../scripts/correlated-materialized-functional-profile.fsx) on the
preview.16 head produces the expected rows and **6 residual calls for both
`d.k=o.x` and `ABS(d.k)=o.x`**. The executor regression also covers negative
inner keys, NULL outer values, and OR branches. Other functional expressions
without a safe inverse or statement-local functional key remain a scale gap.
