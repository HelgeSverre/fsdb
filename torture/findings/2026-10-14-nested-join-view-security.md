# Joined view updates across distinct definers

MySQL 8.4.11 and FSDB were run against the same two-table fixture. The inner
`DEFINER` view exposes `a(id, n)` under `inner_owner`; an outer `DEFINER` view
joins it to `b` under `outer_owner`. The accounts have no direct grant on the
other owner's base table.

| Operation | MySQL 8.4.11 | FSDB regression |
|---|---|---|
| Update the inner view's `n` through the join | One row updated | One row updated |
| Update `b.n` through the join | One row updated | One row updated |
| Revoke `UPDATE` on `a` from `inner_owner`, then update inner `n` | 1142 | 1142 |
| With that grant still revoked, update `b.n` | One row updated | One row updated |
| Revoke `SELECT` on `a` from `inner_owner`, then update `b.n` | 1356 | 1356 |
| Restore that grant, revoke `UPDATE` on the inner view from `outer_owner`, then update either component | 1356 | 1356 |
| Grant `UPDATE(n)` on the inner view to `outer_owner`, then update `b.n` | One row updated | One row updated |
| Revoke `UPDATE` on `b` from `outer_owner`, then update `b.n` | 1356 | 1356 |

The first FSDB implementation classified a distinct-definer component as
read-only. Merely making it writable exposed an authorization bypass: the
flattened update could modify `b` after the inner definer lost SELECT on `a`.
The executor now checks the outer view's original joined sources and each
component view's privilege chain before applying the physical write. The
focused regression is in `ViewTests.fs`.

This evidence covers joined `UPDATE` with one mergeable component view and
one physical component. Distinct-context nested `INSERT`, more complex view
layers, and expression forms outside this fixture remain open.
