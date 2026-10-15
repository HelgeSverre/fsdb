# Correlated affine equality over materialized sources

A stable derived table or CTE previously re-scanned its materialized rows for
each outer row when the dependent predicate was `d.k + 1 = o.x`, `d.k - 1 =
o.x`, `1 + d.k = o.x`, or `1 - d.k = o.x`. For signed `TINYINT`, `SMALLINT`,
`MEDIUMINT`, and `INT` columns with a bounded signed integer literal offset,
each form has one inverse equality key. The planner now looks up that key in
the statement-local equality map and retains the original predicate as the
final filter. A NULL bound or inverse outside signed 64-bit range has no
candidates. Other arithmetic types and unstable expressions keep the normal
evaluation path.

The [MySQL 8.4.11 oracle](../scripts/correlated-affine-oracle.py) checks both
addition orders and subtraction orders, including offsets `1`, `2`, and `-1`,
over derived and CTE sources with NULL and signed INT edge
values. FSDB returns the same counts. A focused regression also puts a
`TOUCH` residual before the affine predicate: the old main branch evaluated
8,032 rows for the first form and failed its 60-call bound, while the new
lookup passes. A complete OR with a direct equality branch also remains
bounded. The full regression suite passes.
