# Layered JSON_TABLE chain writes and grants

Native MySQL 8.4.11 keeps a physical target writable when two dependent
`JSON_TABLE` sources precede it in an inner join and a second definer view
renames the target's writable column. The inner view uses `SQL SECURITY
DEFINER`; its owner has `SELECT` and `UPDATE` on the target, while the outer
view's owner has those grants on the inner view.

The [native differential oracle](../scripts/layered-derived-view-oracle.py)
compares both engines' `IS_UPDATABLE` metadata, affected rows, error codes,
and final target data. Both report `YES` and update one row. Revoking the inner
owner's target `SELECT` returns 1143 for the join key; revoking target
`UPDATE` returns 1356. Revoking the outer owner's `UPDATE` on the inner view
also returns 1356. Each denied write leaves the target unchanged.

A focused `ViewTests` regression retains this two-function source order and
grant chain. This establishes the audited dependency shape; other source
reorderings and longer definer chains remain open.
