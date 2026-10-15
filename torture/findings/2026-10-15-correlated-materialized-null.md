# Correlated NULL candidates over stable materialized rows

MySQL 8.4.11 returns counts `(5, 5, 0)` for three outer rows when a stable
materialized source has 1,000 rows, ten nullable `k` values, and the inner
predicate is `d.k IS NULL AND d.a=o.a`. A second predicate,
`d.k IS NULL OR d.id=o.id`, returns `(11, 11, 11)`. The [native
oracle](../scripts/correlated-null-oracle.py) verifies both a derived table
and a stable CTE with computed output columns.

FSDB already returned those rows, but the materialized correlated planner had
no candidate set for `IS NULL`. With a leading `TOUCH(d.id)=d.id` residual,
the first derived query evaluated 1,004 rows before the change. It now caches
the source rows whose direct column is NULL once per statement, intersects
that bucket with equality, membership, and range candidates, and includes it
in complete OR-branch unions. The focused regression keeps residual calls
below 30 for the conjunction and below 50 for the OR query, while checking
the MySQL counts for both source forms. Unsupported expressions retain the
general evaluation path.
