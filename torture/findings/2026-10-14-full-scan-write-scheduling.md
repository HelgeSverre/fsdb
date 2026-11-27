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

## Insertion gaps and pending inserts

MySQL 8.4.11 protected the insertion gaps for unindexed single-table
`UPDATE` and `DELETE` under REPEATABLE READ and SERIALIZABLE, even if
the filter matched no rows. An empty table retained its final gap. A
constant false filter and an unknown target or predicate column left the
gaps open. The audited write retained its gaps after rollback to a
savepoint. [InnoDB statement locks](https://dev.mysql.com/doc/refman/8.4/en/innodb-locks-set.html) [MySQL savepoints](https://dev.mysql.com/doc/refman/8.4/en/savepoint.html)

With a pending insert, the higher-isolation writer waited and rechecked
the row after the insert committed or rolled back. At READ COMMITTED,
the audited `UPDATE` skipped the pending insert and the `DELETE` waited
and rechecked it. The same lower-isolation distinction held in the
audited keyless READ UNCOMMITTED cases.

Fsdb uses its primary-index locking cursor for the audited integer-key
tables. Tables without an explicit primary key coordinate inserts
with full write scans. Independent inserts can still proceed together.
The audited `INSERT`, `INSERT IGNORE`, `INSERT SELECT`, duplicate-key
insert, and `REPLACE` forms respect the scan gaps.

The 112 wire scenarios matched the digest-pinned MySQL 8.4.11 image
in both in-memory and WAL-backed Fsdb. The controls include temporary
tables that shadow permanent tables and commits to an independent table
during an autocommit SERIALIZABLE scan. Keyless cursor lock order,
additional primary-key types, and limited and joined scan shapes remain
open.

The insert-throughput control alternated native in-memory Debug builds
of `1be869e4` and the updated engine on the same host after the test
suite completed. Each worker sent 1,200 individual autocommit inserts
over loopback, with one warm-up and five measured runs per build.

| Table | Workers | Before rows/s | After rows/s |
| --- | ---: | ---: | ---: |
| Keyless | 1 | 6,535 | 6,403 |
| Keyless | 4 | 14,788 | 14,447 |
| Integer primary key | 1 | 5,845 | 5,824 |
| Integer primary key | 4 | 12,562 | 12,662 |
