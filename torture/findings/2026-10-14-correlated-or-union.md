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
MySQL 8.4.11 also returned those rows for materialized `LIMIT` derived tables
and CTEs with `id + 0` and `user_id + 0` projections. The materialized
regression initially had 3,031 residual evaluations; its statement-local
equality keys now union each complete OR branch and stay below 2,400. Array
identity deduplicates overlapping branches without collapsing distinct rows
that happen to contain equal values. Functional branches still use the general
path.

## Numeric range branches

The [native MySQL 8.4.11 oracle](../scripts/correlated-range-or-oracle.py)
also fixes results for mixed equality/range and overlapping range/range
disjunctions. For outer IDs 1 and 2, each with 20 matching `user_id` rows
among 1,000 orders, `user_id = u.id OR id BETWEEN u.id*20 AND u.id*20+2`
returns 23 rows per outer ID. Overlapping ranges `id BETWEEN u.id*20 AND
u.id*20+2 OR id BETWEEN u.id*20+1 AND u.id*20+3` return four rows each.
The oracle confirms both results for direct tables, pass-through derived tables
and CTEs, and stable materialized derived tables and CTEs.

Before range-aware union planning, the first mixed case evaluated a residual
`TOUCH` function 1,049 times for two outer rows. The regression now requires
fewer than 100 evaluations for mixed branches and fewer than 30 for the
overlapping range branches, across all five source shapes. The physical path
deduplicates stored row IDs before projection; the materialized path
deduplicates source row references. Every branch must provide a safe equality
or range candidate set. An unindexable branch falls back to the general path
and retains its rows; range costs can likewise reject an unhelpful index.
