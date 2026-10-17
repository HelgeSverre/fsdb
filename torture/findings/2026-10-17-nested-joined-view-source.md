# A joined view as the writable source of another joined view

MySQL 8.4.11 accepts an updatable inner-join view as a source of another
updatable inner-join view. A same-host differential probe covered outer
LATERAL, JSON_TABLE, derived, and derived-USING sources. The inner view joined
the physical target to either a table or a grouped derived source. Each outer
view used CASCADED CHECK OPTION.

For all four shapes, both MySQL and fsdb now create the outer view, report
`IS_UPDATABLE=YES`, update one physical target row, reject a write that violates
the check with error 1369, and leave the accepted value committed. A separate
probe confirms that an exposed column of the inner view's other physical
component can also be updated. An unchecked outer view permits a row to leave
its own filter while still enforcing the inner view's CHECK OPTION. A
distinct-definer MySQL oracle and regression confirm that revoking the outer
definer's UPDATE grant on the inner view blocks the layered write with 1356;
restoring only a grant on the inner view's renamed exposed column permits it.

Previously fsdb treated a joined inner view as a read-only source in the
outer view. The resolver now carries its physical source, inner joins,
exposed-column mappings, and checks into the outer joined update. It rewrites
LATERAL and JSON_TABLE references to exposed inner columns. When a read-only
derived source precedes the target, it moves the physical source first and
turns the original USING or NATURAL key into a qualified ON comparison so the
newly inserted inner join cannot make the key ambiguous.

The expansion allows one joined inner view as the first outer source or as
the first writable source after read-only derived sources. A MySQL 8.4.11
oracle covers two leading derived sources: the outer view is updatable,
updates the nested physical target, and rejects a CHECK OPTION violation.
The physical target and its inner joins move before the derived sources so
their conditions remain bound. Inner join aliases that collide with outer
aliases are renamed while inlining; a MySQL 8.4.11 oracle confirms that the
outer view remains updatable, can write either joined component, and enforces
the inner CHECK OPTION after the rename.

An inner JSON_TABLE join whose source expression uses qualified columns can
also be inlined into a joined outer view. The differential oracle uses
`JSON_TABLE(JSON_ARRAY(t.id), '$[*]' ...)` and reuses the inner JSON_TABLE
alias for an outer derived source. MySQL 8.4.11 and fsdb agree that the outer
view is updatable, one row is updated, both inner and outer CHECK OPTION
violations return 1369, and the accepted value remains committed. Revoking
the inner definer's base-table SELECT or the outer definer's component-view
UPDATE makes the layered write fail with 1356 in both engines. A second
oracle composes an inner two-JSON_TABLE chain with a joined outer view and
also agrees on writable metadata, one update, and error 1369. Bare
columns or subqueries in the JSON_TABLE source still retain the read-only
path because rebinding them requires the original inner join scope.

An inner LATERAL join with qualified outer references can also be inlined.
The MySQL 8.4.11 differential oracle uses distinct definers, an inner
dependent LATERAL source, and an outer derived join. Both engines report the
outer view writable, update one row, reject inner and outer CHECK OPTION
violations with 1369, and retain the accepted value. Revoking the inner
definer's SELECT on the dependent source or the outer definer's UPDATE on
the inner view yields 1356 in both engines. Inner LATERAL bodies
with bare references, subqueries requiring scoped qualifier rewriting, or
local aliases that shadow the expanded view alias remain read-only rather
than risking a wrong write. Additional layered source orders and security
context combinations still need differential coverage.
