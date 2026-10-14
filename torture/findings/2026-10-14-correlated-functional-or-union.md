# Correlated functional-key OR candidates

Native MySQL 8.4.11 returned four inner rows for each of two outer bounds in
both of these disjunctions over `LOWER(code)`:

```sql
LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi)
  OR LOWER(o.code) = LOWER(u.exact_code)

LOWER(o.code) BETWEEN LOWER(u.lo) AND LOWER(u.hi)
  OR LOWER(o.code) BETWEEN LOWER(u.lo2) AND LOWER(u.hi2)
```

The 1,000-row inner table has a functional index on `LOWER(code)`. The first
query combines a three-row range with one disjoint equality. The second
combines overlapping three-row ranges; deduplication leaves four rows. The
[oracle script](../scripts/correlated-text-range-or-oracle.py) checks both
predicates on physical and pass-through derived sources. It also checks the
corresponding plain-text predicates on those sources and a stable materialized
source. An unindexable `MOD(id,1000)=1` OR branch must retain its extra row.

Before functional OR candidate planning, FSDB returned the right count but
evaluated the residual `TOUCH` function 1,011 times on the first two-outer-row
case. Each complete OR branch now obtains functional equality or range row IDs;
the smallest usable candidate within a branch is selected, and physical row
IDs are unioned before projection and residual evaluation. The focused
regression requires fewer than 30 residual evaluations for both predicates on
both source shapes. An uncovered branch keeps the general path, preserving
its rows.
