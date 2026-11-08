# Window CTE joined to a nested checked view

MySQL 8.4.11 treats a view over a checked, joined view and a window-function
CTE as updatable, regardless of whether the CTE or the writable view is the
first `NATURAL JOIN` source. Duplicate CTE rows for one target key still update
one physical row. Writes violating either the inner or outer `CASCADED CHECK
OPTION` fail with error 1369 and leave the target row unchanged.

Fsdb already handled the CTE-first order. In the target-first order, an update
failed with error 1052 (`Column 'id' in from clause is ambiguous`). Expanding
the inner view inserted its physical join before the outer NATURAL join, so the
outer join saw a second, hidden `id` column. The outer join now resolves its
shared keys against the view's exposed columns before the physical joins are
inserted. The regression covers both source orders, metadata, rows, affected
count, and failed-write atomicity.
