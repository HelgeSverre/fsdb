# Correlated equality OR candidates

MySQL 8.4.11 was checked on a disposable native server. With outer keys
`1, 2, 3` and indexed inner keys `1, 2, 2, 3`, this query returned
`(1, 3), (2, 3), (3, 1)`:

```sql
SELECT u.id,
       (SELECT COUNT(*) FROM o
        WHERE o.user_id = u.id OR o.user_id = u.id + 1)
FROM u ORDER BY u.id;
```

Before the change, the focused 50-outer-row, 1,000-inner-row regression
produced the correct counts but evaluated its residual `TOUCH` function
3,031 times. A complete indexed OR candidate union now passes the same
regression with fewer than 2,400 residual evaluations. It deduplicates
physical row identities before running the original WHERE expression, so
overlapping branches do not double-count. If any branch lacks a safe equality
lookup, the existing candidate or scan path retains the full predicate. A one-row equality
candidate skips building the union.

This covers direct physical inner tables. Correlated OR predicates over
materialized derived sources and branches needing ranges or functional
lookups still use the general path.
