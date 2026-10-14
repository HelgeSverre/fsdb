# Correlated text IN over a stable materialized source

Native MySQL 8.4.11 is the result oracle. An inner `VARCHAR` key with
`utf8mb4_0900_ai_ci` matches `ALPHA` to both `alpha` and `ALPHA`. Three outer
rows with bounds `('ALPHA', 'beta')`, `('ALPHA', NULL)`, and `(NULL, NULL)`
produce `IN` counts `3, 2, 0`; using the literal `'BETA'` as the second bound
produces `3, 3, 1`.

```sql
CREATE TABLE ci_outer(
  id INT PRIMARY KEY,
  lo VARCHAR(20) COLLATE utf8mb4_0900_ai_ci,
  hi VARCHAR(20) COLLATE utf8mb4_0900_ai_ci);
CREATE TABLE ci_inner(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_0900_ai_ci);
INSERT INTO ci_outer VALUES(1,'ALPHA','beta'),(2,'ALPHA',NULL),(3,NULL,NULL);
INSERT INTO ci_inner VALUES(1,'alpha'),(2,'ALPHA'),(3,'BETA'),(4,'gamma'),(5,NULL);
SELECT o.id,
  (SELECT COUNT(*) FROM (SELECT id,k FROM ci_inner LIMIT 100) d
   WHERE d.k IN(o.lo,o.hi))
FROM ci_outer o ORDER BY o.id;
```

With both sides declared `utf8mb4_bin`, outer bounds `('ALPHA', 'beta')`,
`('alpha', NULL)`, and `(NULL, NULL)` produce `2, 1, 0`. Replacing the second
bound with literal `'beta'` produces `2, 2, 1`. When the materialized key is
`utf8mb4_0900_ai_ci` but the outer bounds are `utf8mb4_bin`, MySQL compares
using the binary collation and returns `1, 1, 0` for those outer rows.

The focused regression materializes each source with additional unrelated
rows. Before text candidate planning, the first case evaluated the `TOUCH`
residual 323 times. The statement-local equality key now unions matching
text rows for direct outer-column and literal bounds only when every bound's
effective comparison collation matches the materialized column's key. The
regression requires fewer than 20 residual evaluations for both matching
collations and their literal forms. A different effective collation retains
the full evaluator, which protects row membership when its equivalence
classes differ from the stored key.

This extends the [numeric materialized IN path](2026-10-14-correlated-materialized-in.md);
other bound expression shapes still use the general path.
