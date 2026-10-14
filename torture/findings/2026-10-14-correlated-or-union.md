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
lookup, the existing candidate or scan path retains the full predicate. A
one-row equality candidate skips building the union.

MySQL 8.4.11 returned the same `(1, 3), (2, 3), (3, 1)` rows when the inner
source was `(SELECT id,user_id FROM o)` or a `WITH` CTE of that projection.
The follow-up regression found the same 3,031 residual evaluations with a
pass-through derived table. The physical-row union now runs below projection,
and the derived-table and stable pass-through CTE cases both stay below 2,400
evaluations. The candidate union is deduplicated before its projection pass.
Materialized derived sources and branches needing ranges or functional lookups
still use the general path.
