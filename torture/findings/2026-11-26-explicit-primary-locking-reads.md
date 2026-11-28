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

## Inserts during a wait on an existing row

Before waiting on an existing row, a contended scan now narrows the
provisional range gap to include the gap at the blocked row. A writer
can insert beyond that boundary. When the blocker releases the row,
the reader reopens the range and claims any newly visible rows before
returning them. On timeout, the reader keeps the gaps it reached
before the blocked record, including the initial seek gap for reverse
scans where applicable.

Using the same four rows, a writer held row 30 while the reader
executed `WHERE id BETWEEN 12 AND 38` with the suffixes below.
An insert at 25 timed out while the reader waited in all three cases.
The other insert committed during the wait. The resumed reader held
an exclusive lock on that new row, checked by a separate `NOWAIT`
probe. All results matched the pinned MySQL image.

| Read suffix | Insert accepted | Returned keys |
| --- | --- | --- |
| `FOR UPDATE` | 35 | 20, 30, 35 |
| `ORDER BY id LIMIT 3 FOR UPDATE` | 35 | 20, 30, 35 |
| `ORDER BY id DESC LIMIT 3 FOR UPDATE` | 15 | 30, 20, 15 |

A forward read waiting successively on rows 20 and 30 also saw the
inserts at 25 and 35. Both servers returned the same set of rows.
A separate timeout after a later insert retained the visited row 25
and left the blocked row 30 and the new row 37 unlocked.
Two additional schedules using `PRIMARY KEY (tenant,id DESC)`
matched MySQL with both requested order directions on a fixed tenant.

## Waits on pending inserts

The range admission step now identifies the first pending primary
key in scan order. It protects the range up to that key and claims
the earlier committed rows before waiting. If the pending key is
published, the reader reopens the range. On timeout it retains the
locks acquired before reaching that key, including the initial seek
gap in the audited reverse scans.

Six timeout schedules matched MySQL 8.4.11 on the same table and
12–38 range. These included explicit and implicit SERIALIZABLE reads,
forward and reverse limits, and a pending key before the first
committed row. In the forward read waiting at an uncommitted key 25,
the reader retained row 20 and its preceding gap after the timeout.
The reverse read retained row 30 and the gaps probed at 25 and 35.

Three schedules with a committing insert at 25 matched during and after
the wait. The forward cursor blocked an insert at 23 and allowed one
at 27 in the ordered case. The reverse cursor blocked an insert at 35
and allowed one at 15. The resumed forward read locked the new row at
27. The reverse read stopped before row 15 and left it writable.
Two further schedules used a second pending key. Once the first
writer committed, both forward and reverse reads reached their
`LIMIT 3` without waiting on the later pending key. In a mixed
schedule, the forward read waited first on row 20 and then on the
pending key 25. It kept row 20 locked during the second wait and
returned both inserts after their transactions committed.

Reselection after a completed row-lock batch still rolls back the
statement's claims if it subsequently times out. Other scan shapes and
access paths still need separate gap coverage.

## Full primary scans

A locking read without a predicate, sort, or limit can scan the entire
primary index when it is forced or is the only visible B-tree index.
At REPEATABLE READ and SERIALIZABLE, the cursor protects the insertion
gap after the last row. An empty index also protects its gap. At READ
COMMITTED, these reads leave unrelated inserts free to proceed.

The pinned MySQL comparison matched 76 schedules covering both access
paths, empty and populated tables, shared and exclusive locks, `NOWAIT`,
`SKIP LOCKED`, and implicit SERIALIZABLE reads. Another 48 schedules
matched the rows, errors, and retained gaps when a nonblocking reader
encountered a locked row or pending insert. Twenty WAL-backed schedules
also matched which gaps a blocking scan protected while it waited.
Another 24 schedules matched across mixed-direction composite, collated
text, decimal, and binary keys. Eight explicit `READ ONLY` transactions
at SERIALIZABLE matched the implicit and explicit shared-read behavior.

MySQL also scans the primary index when the eligible secondary indexes
cannot cover the projection. `FORCE INDEX` on one non-covering secondary
index can still produce a primary scan, even when another index covers
the projection. The planner accounts for the primary key stored in
secondary records and requires whole, untransformed values for the
other projected columns.

The comparison matched 264 additional schedules. These cover unhinted
and forced non-covering scans, a competing covering index excluded by
the hint, prefix and functional indexes, composite primary keys,
qualified stars, and concurrent row locks and pending inserts. Covering
secondary scans require their own locking rules.
