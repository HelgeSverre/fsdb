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

## Timeouts on existing rows

When a qualifying row is held by another transaction, MySQL can keep
the locks acquired before the wait timed out. Fsdb previously released
them on error 1205. The uncontended range still claims rows in a batch.
A contended batch retries in index order. On timeout, the read keeps
the visited row locks and their gaps and releases the provisional
broader gap. The reverse cursor's initial seek gap can survive a
timeout before it acquires any row.

With rows 10, 20, 30, and 40, the reader scanned 12–38 while another
session held row 20 or 30. After error 1205, the blocker rolled back
and a third session tried row locks and inserts while the reader's
transaction remained open. Fsdb matched the pinned MySQL image on
all nine schedules, covering explicit shared and exclusive locks,
implicit SERIALIZABLE reads, and forward and reverse limits.

| Scan | Blocked row | Retained row | Inserts still waiting |
| --- | --- | --- | --- |
| Forward | 20 | none | none |
| Forward | 30 | 20 | 11 |
| Reverse | 30 | none | 35 |
| Reverse | 20 | 30 | 25, 35 |

Two further schedules with a row lock and a gap already held by the
reader confirmed that those earlier claims remain until rollback.
In an uncontended in-memory Debug control over IPv4 loopback, a
2,000-row locking range took 16.91, 16.29, and 16.34 ms median in
baseline, candidate, and baseline order. Each pass timed 60 runs,
including BEGIN, result consumption, and ROLLBACK.

When the initial gap check times out on a pending insert, Fsdb can
still release progress that MySQL keeps. For example, an uncommitted
key 25 inside the range leaves MySQL holding row 20 after a forward
timeout. A provisional read gap can also block an unvisited insert
while the range is waiting. In the forward scan blocked at row 30,
MySQL accepted an insert at 35 during the wait and fsdb returned
1205. Reselection after a completed row-lock batch still rolls back
the statement's claims if it subsequently times out. Gap handling
for `NOWAIT` and `SKIP LOCKED`, secondary indexes, and other read
shapes remains open.
