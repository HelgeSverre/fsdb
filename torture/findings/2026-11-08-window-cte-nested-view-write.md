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

A follow-up on the same pinned oracle joins the nested target to a window CTE
and a grouped CTE with successive `USING(id)` clauses. Both engines report the
outer view updatable, return two rows for target `id=1`, update that physical
row once, and reject an outer CHECK OPTION violation with 1369. The regression
keeps both join keys bound to the inner view's exposed `id` rather than its
hidden physical join columns.
