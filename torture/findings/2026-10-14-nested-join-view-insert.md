# INSERT through a joined view with a distinct-definer component

Native MySQL 8.4.11 was the oracle. An inner definer view exposes `id` and
`n` from an auto-increment base table. An outer definer view joins that
component to a second table, with no direct base-table grant on the inner
table for the outer definer.

With `SELECT` and `INSERT` on the component view for the outer definer and
`INSERT` on the base table for the inner definer, explicit-column `INSERT`
through the joined view writes the inner table. Revoking either INSERT grant
returns 1356 and leaves the table unchanged. Restoring a column-level
`INSERT(n)` grant permits `INSERT INTO joined(n) VALUES (...)` and
`INSERT INTO joined(n) SELECT ...`; including the ungranted `id` in either
form returns 1356. The target base table supplies the omitted auto-increment
identity. `ON DUPLICATE KEY UPDATE` additionally requires UPDATE on the
component view and the inner base table, even when the proposed key is fresh;
with the grants restored, an existing duplicate updates normally. A joined-view
INSERT without a field list retains its separate 1394 restriction.

FSDB previously checked nested read paths after switching to the inner
definer's registry. That rejected a valid insert even when both grant chains
were complete. The write path now checks the outer view's stored SELECT under
its definer, validates the component view's read and INSERT grants under that
identity, then checks the physical write under the inner definer. The
original view-column names are retained for column-level INSERT checks, so
an `INSERT(n)` grant cannot also authorize writing `id`. ODKU assignments
likewise check the component's UPDATE grant before attempting the insert, while
the inner definer must retain UPDATE on the base table.

The focused Expecto regression covers values and INSERT SELECT, grant
revocation, column grants, basic ODKU privilege behavior, error codes, and
final rows. It does not establish parity for deeper distinct-definer layers,
LOAD DATA, REPLACE, or broader ODKU expression and grant combinations through
joined views.
