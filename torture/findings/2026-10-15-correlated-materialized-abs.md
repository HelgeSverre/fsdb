# Correlated ABS equality over materialized sources

For a stable derived table or CTE, `ABS(d.k) = o.x` previously scanned every
materialized row for each outer row even when `k` was a signed integer column.
The inverse of a nonnegative integer bound has at most two stored keys, `x`
and `-x`. The correlated lookup now probes those keys in its statement-local
equality map and leaves the original predicate as the final row filter. A
negative or `NULL` bound has no candidates. The path is restricted to signed
`TINYINT`, `SMALLINT`, `MEDIUMINT`, and `INT`; other types keep their existing
evaluation path.

The maintained [MySQL 8.4.11 oracle](../scripts/correlated-abs-oracle.py)
checks derived and CTE sources with `10`, `999`, `NULL`, `-10`, and
`2147483648` bounds, plus a stored `INT` minimum. Counts are `(2, 0, 0, 0,
1)` for the equality and `(3, 1, 1, 1, 2)` when an `id = 1` OR branch is
added. FSDB matches both forms. The focused regression uses a residual
`TOUCH` function to count candidate evaluations: on the previous main it
evaluated 6,012 rows and failed its 60-call bound; the new lookup passes.
This verifies narrower candidate work as well as the returned rows.
