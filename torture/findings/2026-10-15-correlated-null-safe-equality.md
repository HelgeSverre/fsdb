# Correlated NULL-safe equality candidates

MySQL 8.4.11 treats `d.k <=> o.k` as an ordinary equality when the outer
bound is non-NULL and matches inner NULL keys when it is NULL. A native probe
used indexed inner rows `(1,NULL),(2,NULL),(3,1),(4,2)` and outer bounds
`NULL` and `2`. The correlated counts were `(2,1)`; adding
`OR d.id=3` produced `(3,2)`. A stable materialized derived source with
`id+0` and `k+0` projections returned the same counts.

FSDB already returned the correct rows but scanned the whole inner source
for these correlated predicates. Candidate planning now uses ordinary indexed
equality for a non-NULL bound and an indexed NULL bucket for a NULL bound.
The same branches can participate in complete OR candidate unions; `<=>`
remains the final residual predicate. A focused regression seeds 1,000 inner
rows, including 20 NULL keys and one selective non-NULL key. It checks counts
and keeps the `TOUCH` residual below 80 calls for direct, pass-through, and
stable materialized sources with each bound and OR branch. `IGNORE INDEX`
restores over 900 calls for the selective direct probe.

The native probe and regression cover integer keys and these source shapes.
Broader mixed-type coercion, collation, and functional-index cases remain
subject to their separate planner rules.
