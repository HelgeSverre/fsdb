# Correlated IN over a materialized text expression

MySQL 8.4.11 accepts a correlated `IN` list against a text expression over
a stable derived table or CTE. For example:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT DISTINCT id,k FROM inner_rows) d
        WHERE LOWER(d.k) IN (o.x,'word10'))
FROM outer_bounds o ORDER BY o.id;
```

Fsdb already returned the right rows, but its statement-local text-expression
key served equality only. On a 1,001-row materialized source, an `IN` query
evaluated a leading residual function 5,010 times. The key now unions one
candidate group per bound and deduplicates row identities before the original
SQL predicate checks the candidates. Each bound uses its own effective
comparison collation, as the executor's `IN` comparison does. A
`COLLATE`-qualified literal is accepted as a stable bound. Unsupported bound
types, unstable expressions, and collation errors retain the general path.

The [native MySQL oracle](../scripts/correlated-materialized-text-range-oracle.py)
checks literal, outer-expression, duplicate, NULL, OR, and mixed explicit
accent-insensitive cases. The focused executor regression covers both a
materialized derived source and a CTE, verifies the same result rows, and
keeps each query under 80 residual calls.
