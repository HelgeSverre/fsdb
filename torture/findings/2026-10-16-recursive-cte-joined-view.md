# Recursive CTE as a read source in an updatable view

Native MySQL 8.4.11 marks a view updatable when its joined query reads from a
recursive CTE and exposes writable columns from one physical table. A focused
[oracle](../scripts/recursive-cte-joined-view-oracle.py) checks the recursive
source before or after the target, a parenthesized join operand, and inferred
versus explicit CTE column names. Each `UPDATE ... WHERE id=2` changes the
physical target once. A `CASCADED CHECK OPTION` rejects an invalid update
with error 1369.

FSDB previously left `WITH RECURSIVE` in the outer view query, causing both
`INFORMATION_SCHEMA.VIEWS.IS_UPDATABLE=NO` and UPDATE error 1288. The view
rewrite now wraps a single referenced recursive CTE as a read-only derived
source, retaining its binding inside that source. The outer joined query can
then identify the physical target and apply its existing write and CHECK OPTION
rules. The executor regression pins the four writable layouts and both
rejection cases.

## Dependent and repeated recursive sources

MySQL 8.4.11 also updates the physical target through a second CTE derived
from the recursive one, an aggregate over it, two references to the same
recursive binding, and `SELECT *` projections across one or two dependent
CTE layers. Repeated references also share a volatile projected `RAND()`
value, as they do in MySQL. The expanded oracle covers ten writable layouts. FSDB now infers
the exposed names of CTEs in declaration order, using earlier bindings to
expand dependent stars, and wraps every directly referenced CTE with the full
binding scope. The joined target remains the only writable component and is
updated once even when the recursive rowset appears twice.

An audited contended UPDATE through the dependent CTE view waits for a writer
holding the matching physical row, then rebases after that writer commits.
Both engines finish with two increments. This verifies that source rewrite
keeps the existing physical row-lock path for that shape.

MySQL labels a recursive-only view `IS_UPDATABLE=YES` but rejects its UPDATE
with 1288. FSDB rejects that UPDATE too and currently reports `NO`; this
metadata difference remains. More complex recursive/dependent source
compositions, layered security, and other concurrent write shapes still need
separate audits.
