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

The expansion is currently restricted to one joined inner view as the first
outer source, or as the first writable source after one leading read-only
source. Conflicting inner/outer aliases, an inner LATERAL or JSON_TABLE join,
inner subqueries requiring scoped qualifier rewriting, and a LATERAL source
whose local aliases shadow the expanded view alias remain read-only rather
than risking a wrong write. Additional layered source orders and further
security-context combinations still need differential coverage.
