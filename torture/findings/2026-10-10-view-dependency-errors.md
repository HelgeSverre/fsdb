# Invalid stored-view dependencies

MySQL 8.4.11 returns 1356 (`HY000`) when a stored view is read after its base
table is dropped or a column used by its projection or predicate is removed.
The error names the view, not the missing table or column. For nested views,
it names the outer view requested by the client.

Fsdb now maps missing table, column, and function errors raised during view
expansion to the same view diagnostic. A direct-view merge checks the saved
definition's bindings before rewriting it into the outer query; an invalid
definition follows the view-expansion path. Focused Expecto regressions cover
all three dependency changes and a nested view. The MySQL evidence uses the digest-pinned 8.4.11
image in `torture/compose.yaml`.
