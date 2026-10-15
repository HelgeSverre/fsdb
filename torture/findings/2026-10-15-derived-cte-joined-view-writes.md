# Derived sources inside a CTE-backed joined view

MySQL 8.4.11 marks a joined view updatable when its read-only CTE contains an
independent derived source, including an aggregate over that source and two
nested derived levels. `UPDATE` reaches the joined physical target once even
when the CTE contributes duplicate matches. `WITH CASCADED CHECK OPTION`
rejects an invalid target value with error 1369, while `INSERT` into the
joined view fails with error 1471.

FSDB previously rejected the simplest audited view at creation with error
1368 because writable-view normalization accepted CTE bodies sourced only
from physical tables. The normalizer now recognizes independent plain derived
sources recursively within a CTE body. The saved view definition still drives
reads; the normalized form is used to resolve the joined write and metadata.

The [native MySQL oracle](../scripts/derived-cte-view-oracle.py) and focused
regression cover simple, aggregate, and two-level nested bodies, explicit CTE
column names, metadata, UPDATE, CHECK OPTION, INSERT refusal, and final base
rows. Recursive or dependent CTEs, nested query expressions, and starred
explicit-column CTE projections remain outside this normalization.
