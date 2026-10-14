# Correlated functional and mixed-index OR candidates

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
source. Additional queries mix plain `code` equality or range branches with
functional `LOWER(code)` equality or range branches. Equality-only mixtures
return two rows; the range mixtures return four. An unindexable
`MOD(id,1000)=1` OR branch must retain its extra row.

Before functional OR candidate planning, FSDB returned the right count but
evaluated the residual `TOUCH` function 1,011 times on the first two-outer-row
case. A separate mixed plain/functional probe also evaluated the residual
1,011 times before the follow-up change. Each complete OR branch now compares
available plain and functional equality or range candidates, then uses the
smallest one. Physical row IDs are unioned before projection and residual
evaluation. The focused regression requires fewer than 30 residual evaluations
for the functional and mixed predicates on both source shapes. An uncovered
branch keeps the general path, preserving its rows.

A further MySQL 8.4.11 probe uses 1,000 inner rows. `user_id=1` matches IDs
1–500, `ABS(band)=7` matches IDs 499–898, and a second OR branch matches IDs
950–951. MySQL returns four rows for both the physical source and a
pass-through derived source. Before branch intersection, FSDB returned four
but evaluated the residual 404 times: the first branch selected the smaller
400-row functional slice. Complete OR branches now intersect all compatible
ordinary and functional candidates by physical row ID before unioning their
results. The focused regression requires fewer than 20 residual calls for
both source shapes. As with other physical candidate intersections, functional
lookups participate only when read rows retain stored-row identity.

Two functional predicates can also narrow each other before a branch is
unioned. A separate native MySQL 8.4.11 case has 1,000 rows with `ABS(a)=7`
on IDs 1–450 and `ABS(b)=9` on IDs 449–848. Their intersection has two rows;
an OR branch for IDs 950–951 makes four. MySQL returned 2, 4, and 2 for the
conjunctive equality, OR, and two-functional-range forms, respectively, on
both direct and pass-through projected sources. FSDB previously passed 402
candidates to a residual function for the first and range forms. Its
functional equality and range helpers now intersect every compatible indexed
candidate list by physical row ID, leaving fewer than 20 residual calls in
the focused regression. This does not cover functional bounds that cannot be
mapped to stored columns or comparisons with incompatible collations.
