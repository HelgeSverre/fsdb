# Three-layer definer grants for joined-view INSERT

Native MySQL 8.4.11 was the oracle. A base table's `n` is exposed as `value`
by an inner definer view, renamed to `metric` by a middle definer view, and
joined with another table by an outer definer view. Each definer has only the
grants needed at its layer.

An explicit-column insert through the outer view reaches the base table when
both intervening views grant INSERT and the inner definer can insert into the
base table. Revoking the middle definer's INSERT grant on the inner view makes
the outer insert fail with error 1356. Restoring only `INSERT(value)` permits an
outer `INSERT(metric)` but rejects `INSERT(id,metric)` with error 1356. The
accepted values and final auto-increment identities match fsdb's focused
Expecto regression in `ViewTests.fs`.

`ON DUPLICATE KEY UPDATE` exposes a distinct rule. With only `UPDATE(value)` on
the inner view for the middle definer, MySQL accepts both an update to
`metric` and an update to `id`, as well as a fresh-key insert carrying the
ODKU clause. Fsdb initially returned 1356 for the `id` assignment because it
checked the intermediate view grant against that specific column. After
grant validation was corrected, the rewritten upsert still referred to the
joined alias `m.id` outside its scope and returned 1054. The executor now
checks exact INSERT columns and the existence of an UPDATE grant at the
intermediate view, then resolves target-column references against the physical
upsert row. The base-table definer's privileges remain checked separately.
The same any-column UPDATE validity rule applies to the outer definer's grant
on the middle component view: `UPDATE(metric)` permits an ODKU assignment to
`id`, while the INSERT column grants are still checked for each supplied
column.

Fsdb initially checked the physical name `n` when authorizing an INSERT
against the inner view's exposed `value`, rejecting a grant MySQL accepts.
Retargeted INSERT and INSERT SELECT authorization now maps physical columns
back to each intervening view's exposed names.

The disposable native oracle is
`torture/scripts/deep-joined-view-insert-oracle.py`. This case confirms an
additional supported distinct-definer layer; it does not establish parity for
arbitrary depth or every joined-view write form.
