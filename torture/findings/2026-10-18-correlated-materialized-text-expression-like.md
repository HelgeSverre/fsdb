# Correlated LIKE prefix over a materialized text expression

MySQL 8.4.11 accepts a correlated binary-collated `LIKE` pattern against a
text expression over a stable derived table:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT DISTINCT id,k FROM inner_rows) d
        WHERE LOWER(d.k) LIKE o.p)
FROM like_bounds o ORDER BY o.id;
```

Fsdb returned the right rows but scanned the 1,001-row materialized source
for each outer pattern. Its statement-local expression range lookup now
narrows a nonempty literal prefix when the effective comparison collation is
`utf8mb4_bin` or `utf8mb4_0900_bin`. A stable NULL outer pattern yields an
empty candidate set, including during metadata probing. Complete OR branches
union their candidates, and the original `LIKE` expression decides membership
after narrowing. Patterns without a literal prefix and unsupported collations
retain the general evaluation path.

The [native MySQL oracle](../scripts/correlated-materialized-text-range-oracle.py)
checks selective, broad, NULL, and wildcard-only patterns, plus an OR branch.
The [profile](../scripts/correlated-materialized-text-profile.fsx) records
leading-residual evaluations per outer row. In that profile, `word99%`,
`word1%`, and NULL fell from 1,024, 1,111, and 2,002 calls to 24, 111,
and 2 calls respectively. The focused executor regression checks the results
and bounds residual evaluations for the selective prefixes.
