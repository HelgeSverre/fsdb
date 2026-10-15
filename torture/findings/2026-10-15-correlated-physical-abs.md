# Correlated ABS equality over indexed physical sources

An indexed physical table, pass-through derived table, or pass-through CTE
previously scanned its rows for `ABS(d.k) = o.x`, although a signed integer
column can match at most two inverse keys. The physical correlated lookup now
probes `x` and `-x` through the maintained equality index, unions stored row
identities, and leaves the original predicate as the final row filter. A NULL
or negative bound has no candidates. Each key outside the column's declared
signed range is omitted. Other types retain residual evaluation.

The [MySQL 8.4.11 oracle](../scripts/correlated-abs-oracle.py) covers direct,
pass-through, and materialized sources, operand order, complete OR branches,
and `TINYINT` through `INT` boundary values. FSDB matches its counts. The
focused regression uses `TOUCH` before the ABS predicate so a residual scan
cannot pass merely by short-circuiting; the pre-change head evaluated 6,012
rows and failed the 80-call bound, while the new lookup passes across 1,001
inner rows and five outer bounds. The existing materialized regression
also passes after sharing the inverse-key logic.
