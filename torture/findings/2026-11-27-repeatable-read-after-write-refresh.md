# Repeatable reads after a write refresh

MySQL 8.4.11 keeps the committed catalog visible to the first ordinary
read in a repeatable-read transaction. A write can use a newer row.
Subsequent ordinary reads combine the original read view with only the
transaction's own changes. A locking read can see the current row without
changing the plain SELECT view.
[MySQL consistent nonlocking reads](https://dev.mysql.com/doc/refman/8.4/en/innodb-consistent-read.html)

Fsdb previously refreshed its write and read catalogs together. The
original failure used a checked upsert waiting for a duplicate. The
holder committed `n=9`, so the duplicate increment failed the `n<10`
check. MySQL still showed `n=5` to the waiting transaction's plain
SELECT. Fsdb showed `n=9`. Both returned warning 1369 and inserted the
second candidate. The lock schedule now matches in memory and on disk.

To reproduce the same visibility rule without a lock wait, create
`t(id INT PRIMARY KEY, n INT)` with rows `(1,1)` and `(2,2)`. In session A,
begin a transaction and run `SELECT * FROM t ORDER BY id`. In session B,
run `UPDATE t SET n=10` under autocommit. In session A, run
`UPDATE t SET n=n+1 WHERE id=1` and repeat the plain SELECT. It returns
`(1,11)` and `(2,2)`. After A commits, a new read sees `(1,11)` and `(2,10)`.

The read view also persists through savepoint rollback. In the indexed
case, a concurrent primary-key move can leave the older key visible
beside the transaction's own update at the new key. A reused unique
secondary key can have two visible rows in a range scan. A unique point
lookup returned the first clustered record in the audited case.

Four additional two-session schedules compared the initial read, UNION
as the first read, a write before the first read, and a READ COMMITTED
control. Both modes matched all 58 result lines. A further join with a
temporary table kept the same permanent-table view.

The [current point-write follow-up](2026-11-27-current-point-writes.md)
fixes an UPDATE targeting a key moved after the read view opened. The
indexed overlay regression reaches the moved row directly.

## Explicit consistent snapshots

`START TRANSACTION WITH CONSISTENT SNAPSHOT` pins the committed view at
the start of a REPEATABLE READ transaction. A writer that commits before
the reader's first query is still outside this view. Locking reads can
see the current row, and a write can overlay its own change without
replacing the pinned view. Other isolation levels return warning 138
and apply their ordinary read rules.

An explicit start can combine the snapshot request with `READ ONLY` or
`READ WRITE` in either order. The wire tracker includes the request in
its replay text. `COMMIT AND CHAIN` and `ROLLBACK AND CHAIN` retain the
effective isolation and access mode and drop the one-shot snapshot
request. Preparing a transaction start returns error 1295.
