# Indexed correlated IS NULL candidates

The [native MySQL 8.4.11 oracle](../scripts/correlated-null-oracle.py) returns
`(5, 5, 0)` for `d.k IS NULL AND d.a=o.a` and `(11, 11, 11)` for
`d.k IS NULL OR d.id=o.id`. It checks a direct indexed table, a pass-through
derived table and CTE, stable materialized forms, and an `IGNORE INDEX`
control.

FSDB already returned those counts. For a 1,000-row physical source with a
non-unique secondary index on nullable `k`, however, the first correlated
query evaluated a leading `TOUCH(d.id)=d.id` residual 1,004 times. The
planner now reads the index's NULL bucket separately from ordinary equality
probes, maps pass-through columns to their physical key, intersects NULL and
other safe row-ID candidates, and includes the bucket in complete OR unions.
The focused regression keeps residual calls below 30 for the conjunction and
below 50 for the OR query. Ignoring the `k` index leaves more than 1,000
residual calls while preserving the result.

The storage lookup uses only visible, allowed non-unique single-column B-tree
keys. Nullable unique keys and broader composite-prefix NULL access are not
covered by this path. The original predicates remain the final result check.
