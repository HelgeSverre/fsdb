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

The native oracle now covers 38 writable layouts. A recursive CTE can feed a
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
that only depend on already bound columns. Non-shadowing local CTEs inside
`LATERAL` now carry the outer recursive binding through one or two dependent
layers. A volatile comparison against the outer source confirms that the
nested wrapper shares the statement-local rowset. Local recursive CTEs,
complex shadowing scopes, and UNION order/limit expressions with subqueries
still need separate binding-scope audits. MySQL 8.4 rejects a JSON_TABLE
input with a scalar subquery during view creation (1210), so that shape is
not a writable-view parity target.
The oracle also confirms scalar recursive CTE re-reads inside a local CTE,
including a volatile comparison, and a source-free local CTE that shadows the
outer recursive name. Fsdb now keeps the local binding separate from the
wrapped outer source while preserving the physical target's updateability.
Two declaration-order cases put a `copied` CTE that reads the outer `seq`
before a local `seq` shadows it. Both remain writable in MySQL. Fsdb now
rewrites each CTE body using only the names visible at its declaration and
groups the earlier outer re-read with the statement-local recursive rowset;
the volatile `RAND()` case detects a second evaluation.
`UNION ALL` and deduplicating `UNION` bodies inside a dependent local CTE
also leave the physical target writable. Fsdb now wraps their
branches in the outer recursive scope and discovers re-reads across both
branches as one statement-local rowset. The deduplicating case compares a
volatile value to catch independent CTE evaluation. Ordered and limited local
UNION bodies also keep the physical target writable when their order/limit
expressions contain no subqueries; the oracle includes a volatile
`LIMIT 1 OFFSET 0` case.

MySQL labels a recursive-only view `IS_UPDATABLE=YES` but rejects its UPDATE
with 1288. An additional MySQL 8.4.11 check showed the same metadata rule for
a direct column projection from a nonrecursive CTE whose body is an aggregate
or UNION; a projected expression instead reports `NO`. FSDB now bases this
single-CTE metadata case on the outer projection and reports `YES` for the
recursive-only view while still rejecting its UPDATE with 1288. More complex
recursive/dependent source compositions, layered security, and other concurrent
write shapes still need separate audits.
