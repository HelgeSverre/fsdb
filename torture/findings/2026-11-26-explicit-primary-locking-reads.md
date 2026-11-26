# Explicit primary-key locking reads

Simple integer primary-key locking reads now use the same point and
range access as audited SERIALIZABLE reads in an open REPEATABLE READ
or SERIALIZABLE transaction. `FOR UPDATE` acquires exclusive row
ownership. `FOR SHARE` and `LOCK IN SHARE MODE` acquire shared
ownership. A valid single-source `OF` clause keeps the same access.
An ordered limited range locks the rows it visits and the associated
gaps. A missing exact key protects its containing gap.

The oracle used the [pinned MySQL 8.4.11 image](../config/toolchain.json).
On a table with integer primary keys 10, 20, 30, and 40, one session
started a transaction and made the read below. A second session set
`innodb_lock_wait_timeout=1` and probed each row with `FOR UPDATE NOWAIT`,
row 20 with `FOR SHARE NOWAIT`, and inserts at 11, 25, 35, and 45. Each
probe ran in a separate transaction and rolled back.

| Isolation | Locking read | Returned keys | Rows rejecting an update-lock probe | Inserts waiting |
| --- | --- | --- | --- | --- |
| REPEATABLE READ | `id=20 FOR UPDATE` | 20 | 20 | none |
| REPEATABLE READ | `id=25 FOR UPDATE` | empty | none | 25 |
| REPEATABLE READ | `id=25 FOR SHARE` | empty | none | 25 |
| REPEATABLE READ | `id BETWEEN 12 AND 28 FOR UPDATE` | 20 | 20 | 11, 25 |
| REPEATABLE READ | `id BETWEEN 12 AND 28 FOR SHARE OF g` | 20 | 20 | 11, 25 |
| REPEATABLE READ | `id BETWEEN 12 AND 38 ORDER BY id LIMIT 1 FOR UPDATE` | 20 | 20 | 11 |
| REPEATABLE READ | `id BETWEEN 12 AND 38 ORDER BY id DESC LIMIT 1 FOR SHARE` | 30 | 30 | 25, 35 |
| READ COMMITTED | `id=25 FOR UPDATE` | empty | none | none |
| READ COMMITTED | `id BETWEEN 12 AND 28 FOR UPDATE` | 20 | 20 | none |
| SERIALIZABLE | `id BETWEEN 12 AND 28 FOR UPDATE` | 20 | 20 | 11, 25 |

Fsdb matched all ten schedules, including 3572 on row-lock probes,
1205 on blocked inserts, and release after rollback. A separate
three-session test used `PRIMARY KEY (tenant,id DESC)`. Explicit
locking reads waited for pending inserts and updates, then saw the
committed rows while ordinary reads in the same transaction continued
to use their existing snapshot. The acquired row locks lasted through
transaction completion, and `autocommit=0` retained a missing-key gap
until rollback. Binary prepared probes confirmed missing-key gaps
and exclusive existing-row locks above the signed 64-bit limit.
`LOCK IN SHARE MODE` protected the same gap, and an invalid
`OF` target still returned error 3568.

A timed-out range can retain locks in MySQL. With row 30 held by
another transaction, a forward read over 12–38 times out at row 30.
MySQL keeps the lock on row 20 and its preceding gap. Fsdb releases
those claims on the failed statement. This difference was reproduced
before and after the change for the audited explicit and implicit
locking reads. Gap handling for `NOWAIT` and `SKIP LOCKED`, secondary
indexes, and other read shapes remains open.
