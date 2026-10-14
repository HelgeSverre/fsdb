# Full-scan write scheduling and current-row rechecks

Oracle: native MySQL 8.4.11, October 14, 2026. The table in each probe was
`t(id INT, n INT)` with no primary or secondary index and one row `(1, 0)`.

One connection began a transaction and updated the row without committing.
A second connection issued a full-scan write. It blocked until the first
connection committed. When the second statement was `UPDATE t SET n=n+1 WHERE
id=1`, both increments took effect and the final `n` was 2. A waiting
`DELETE FROM t WHERE id=1` deleted the refreshed row (affected rows 1); a
waiting `DELETE FROM t WHERE n=0` rechecked the committed value and deleted
nothing (affected rows 0, final row `(1, 1)`).

A separate repeatable-read probe began a transaction, read `v=1`, let another
connection commit `v=1234`, then ran `UPDATE t SET v=v+1 WHERE id=1`. MySQL
reported one affected row, committed successfully, and stored `v=1235`.
The earlier snapshot read did not force that UPDATE to use the stale value.

FSDB's former full-scan transaction path did not claim row ownership and
could reject the waiting writer at commit. Its autocommit path refreshed the
row under the lock but tested selection by the old `Value[]` reference, so a
waiting UPDATE could affect zero rows. The current path claims scanned row
identities before an explicit transaction write; storage refreshes selected
row IDs after waiting and rechecks a changed row's predicate. An unchanged
row is not rechecked, preserving warning counts from its initial selection.

Expecto coverage is in `TransactionTests.fs`: the explicit and autocommit
full-scan UPDATE waits, the two DELETE outcomes, and the current-read
transaction UPDATE. This does not prove next-key/gap locking or scheduling
for CTE and multi-table writes.
