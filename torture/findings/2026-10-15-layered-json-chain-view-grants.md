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

A focused `ViewTests` regression retains this grant chain and source order.

The same oracle also places the physical target between the two functions.
The later `JSON_TABLE` reads `t.id`, so its dependency crosses the writable
target. MySQL and fsdb again report `IS_UPDATABLE=YES`, update one row, return
1143 when the inner definer loses target `SELECT`, and return 1356 when either
definer loses the relevant `UPDATE`. Final rows agree. The regression covers
both source orders without changing the engine's view-write rules. Other
reorderings and longer definer chains remain open.
