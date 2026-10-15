# Correlated text NOT BETWEEN with a literal or NULL bound

MySQL 8.4.11 returns `(19,29)` for 1,000 binary-collated codes `a000`–`a999`
when the lower bound is the outer row's `a010` or `a020` and the upper bound is
the literal `a990`:

```sql
SELECT u.id,
       (SELECT COUNT(*) FROM text_or_rows o
        WHERE o.code NOT BETWEEN u.lo AND 'a990')
FROM text_or_bounds u ORDER BY u.id;
```

With `(NULL,'a990')` and `('a010',NULL)` as the two outer bound pairs, MySQL
returns `(9,10)`: an unknown comparison does not hide the decisive outside
range on the other side. The [native oracle](../scripts/correlated-text-range-or-oracle.py)
checks both forms over physical, pass-through derived, and stable materialized
derived sources.

FSDB already returned the right counts, but the mixed outer/literal form
scanned broadly. In the focused 1,000-row materialized fixture, the residual
`TOUCH` function ran 1,051 times before the change. The candidate planner now
accepts a literal or a stable outer value for each strict range produced from
`NOT BETWEEN`, including the statement-local materialized lookup. The original
predicate remains the final filter. The focused regression checks all three
source shapes, both bound forms, and fewer than 100 residual calls per shape.

The same mixed-bound form through `LOWER(code)` also matches MySQL's `(19,29)`
counts on physical and pass-through derived sources. Before the functional
range path accepted the literal bound, the physical fixture made 1,051
residual calls; the indexed candidate union now keeps the count below 100.
The expression's stored functional-index collation still has to match the
comparison collation, and the original predicate remains the final filter.
