# Existing stored functions without a selected database

The pinned MySQL 8.4.11 oracle returns 1046 (`3D000`) for an unqualified
stored-function call when the session has selected no database, even if that
function exists in the schema named by a table, CTAS target, or view target.
The audited `SELECT`, `DO`, `SET`, `INSERT`, `UPDATE`, `DELETE`, CTAS, and view
definitions all follow this rule. An explicitly qualified function call works.

Fsdb now carries the session's selected-database state through expression
execution. An unset execution scope keeps direct Executor callers' existing
schema behavior; a wire session with no selected database does not fall back
to its internal default schema for stored-function lookup. The audited view,
defined and stored in the same schema, remains callable by its qualified name
from a session with no selected database. Focused Expecto regressions check the
errors, qualified calls, a qualified view read, and unchanged table rows after
rejected mutations.

The MySQL evidence was taken from the digest-pinned 8.4.11 image in
`torture/compose.yaml`. The audited statements cover text protocol; broader
prepared and stored-program contexts remain to be compared.
