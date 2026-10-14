# Three-layer definer grants for joined-view INSERT

Native MySQL 8.4.11 was the oracle. A base table is exposed by an inner
definer view, renamed by a middle definer view, and joined with another table
by an outer definer view. Each definer has only the grants needed at its layer.

An explicit-column insert through the outer view reaches the base table when
both intervening views grant INSERT and the inner definer can insert into the
base table. Revoking the middle definer's INSERT grant on the inner view makes
the outer insert fail with error 1356. Restoring only `INSERT(n)` permits an
outer `INSERT(metric)` but rejects `INSERT(id,metric)` with error 1356. The
accepted values and final auto-increment identities match fsdb's focused
Expecto regression in `ViewTests.fs`.

The disposable native oracle is
`torture/scripts/deep-joined-view-insert-oracle.py`. This case confirms an
additional supported distinct-definer layer; it does not establish parity for
arbitrary depth or every joined-view write form.
