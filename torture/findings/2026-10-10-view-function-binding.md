# Function binding in view definitions and CTAS

MySQL 8.4.11 resolves an unqualified function while creating a view, before
publishing the view. It rejects a missing function with 1305 (`42000`), whether
the call appears in the projection, `WHERE`, or a derived source. `CREATE
TABLE ... AS SELECT` has the same diagnostic. Without a selected database,
both statements return 1046 (`3D000`) for a missing unqualified function, even when
their target names include a schema.

Fsdb now checks function names while binding a view definition, without
evaluating its query or changing ordinary prepared-statement binding. The
shared resolver accepts builtins, aggregate functions, extensions, and stored
functions. A view created in another schema still resolves an unqualified
function against the selected session schema, matching the MySQL oracle.
Focused Expecto cases cover missing calls, valid builtin and stored calls,
derived sources, and cross-schema lookup. The `missing-function-view-definitions`
contract in run `20261010T130756272-76037` matched MySQL on all eight steps.
The complete run retained only the nine known identifier-case differences.

This audit covers these text-protocol DDL forms. Other expression-bearing DDL
and stored-program definition contexts remain outside it.
