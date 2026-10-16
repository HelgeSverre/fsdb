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

## Nested and dependent read sources

The native oracle now covers 26 writable layouts. A recursive CTE can feed a
nested derived table or a joined derived source inside a dependent CTE. It can
also be followed directly by a FROM-less dependent `LATERAL` projection or a
`JSON_TABLE` whose input reads the recursive source's exposed column. MySQL
marks those four views updatable and increments the physical target once. The
matching fsdb regression checks metadata and target updates for each.

A further MySQL case puts an independent derived table inside the dependent
`LATERAL` body. The recursive binding is used only through the left source's
exposed `s.k`, so the view remains writable. Fsdb now checks nested companion
source trees for direct references to the moved CTE instead of requiring the
lateral body to have no FROM clause.

Two further `LATERAL` layouts re-read the outer recursive CTE inside the
dependent body. MySQL keeps that view writable and uses one statement-local
rowset for both references: comparing a projected `RAND()` value across the
references still finds the target row. Fsdb now wraps the nested reference
in the same CTE scope and shares its materialized rows across both sources.
The regression checks metadata and one physical target update for each layout.

A nested derived table inside the lateral body can also re-read the CTE. The
ordinary and volatile-value versions both retain one shared rowset. A scalar
or `EXISTS` subquery that only reads the already exposed outer alias is safe
alongside the moved CTE scope, and MySQL marks that view writable.
CTE-dependent scalar, `EXISTS`, `IN`, and quantified-comparison expressions
now get nested read-only wrappers that retain the moved binding. A projected
volatile `RAND()` value verifies that a scalar re-read shares the outer source's
statement-local CTE rows.

The direct `LATERAL` and `JSON_TABLE` forms previously failed with 1288 in
fsdb because the view rewrite required every outer source to be a table
reference before wrapping recursive bindings. It now admits companion sources
that only depend on already bound columns. Subqueries inside companion
expressions with their own local CTE scope, and a JSON_TABLE input with a
subquery, still need a separate binding-scope audit before they can use this
rewrite. MySQL also accepts a local CTE inside `LATERAL`; that form remains
open in fsdb.

MySQL labels a recursive-only view `IS_UPDATABLE=YES` but rejects its UPDATE
with 1288. FSDB rejects that UPDATE too and currently reports `NO`; this
metadata difference remains. More complex recursive/dependent source
compositions, layered security, and other concurrent write shapes still need
separate audits.
