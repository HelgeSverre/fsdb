# Indexed correlated IS NULL candidates

The [native MySQL 8.4.11 oracle](../scripts/correlated-null-oracle.py) returns
`(5, 5, 0)` for `d.k IS NULL AND d.a=o.a` and `(11, 11, 11)` for
`d.k IS NULL OR d.id=o.id`. It checks a direct indexed table, a pass-through
derived table and CTE, stable materialized forms, and an `IGNORE INDEX`
control.
The oracle also checks nullable unique and leading composite keys, with
`IGNORE INDEX` selecting each surviving key in turn.
It also checks `a=o.a AND k IS NULL` against a composite `(a,k)` key, where
the NULL part follows a correlated bound.

FSDB already returned those counts. For a 1,000-row physical source with a
non-unique secondary index on nullable `k`, however, the first correlated
query evaluated a leading `TOUCH(d.id)=d.id` residual 1,004 times. The
planner now reads the index's NULL bucket separately from ordinary equality
probes, maps pass-through columns to their physical key, intersects NULL and
other safe row-ID candidates, and includes the bucket in complete OR unions.
The focused regression keeps residual calls below 30 for the conjunction and
below 50 for the OR query. Ignoring the `k` index leaves more than 1,000
residual calls while preserving the result.

The storage lookup uses the direct NULL bucket of a visible, allowed non-unique
single-column key when present. Otherwise it reads the leading NULL slice of
a visible, allowed ordered key. This covers nullable unique keys and composite
keys whose first column is NULL; ignoring both keys restores the scan. When
earlier composite parts have exact stored-key equality bounds, the lookup also
reads their NULL suffix slice. On the `(a,k)` corpus, that keeps residual calls
below 30; ignoring the key restores more than 1,000 calls. The original
predicates remain the final result check. Expression keys and later NULL parts
without exact bounds for all preceding parts remain outside this path.
