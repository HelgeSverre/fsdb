# Missing primary-key probes in joined updates

MySQL 8.4.11 and Fsdb ran on separate disposable
loopback servers. The [schedule oracle](../scripts/joined-view-primary-gap-oracle.py)
creates its own schema with two target rows, their joined partners,
and a separate `gate` table. The update probes `gate.id=3` while
writing through a joined view. The key is absent, and the update
affects zero rows.

Fsdb progressed from 37 to 58 matches on the original 58 checks.
The earlier differences were insertions and key moves that succeeded
on Fsdb while MySQL returned lock wait timeout 1205. The oracle
rolls back each test insert so the next attempt sees the same data.
Both engines now match the 69 checks for these primary-key gaps,
including explicit read-view controls and both predecessor-purge cases.

## Isolation and transaction lifetime

With gate keys `-5, 1, 5, 9`, the missing key protects the open
interval from `1` to `5` at REPEATABLE READ and SERIALIZABLE.
Inserts at `2`, `3`, and `4` time out. Inserts at `0`
and `6` succeed. READ UNCOMMITTED and READ COMMITTED allow all
five inserts.

With fresh tables, a missing probe before the first key protects the
lower open-ended interval. A probe after the last key protects the
upper interval. A missing probe into an empty table blocks every
tested insertion. The oracle truncates the gate table between these
cases to give each lookup a fresh index.

Moving the existing key `5` to `4` times out. Changing its
non-key value succeeds. Two transactions can hold the same gap
when their updates use separate writable parent rows. An insert
stays blocked after the first holder rolls back and succeeds
after the second rolls back. Rolling back to a savepoint from
before the missing-key lookup releases the gap in this fixture.
[InnoDB gap and insertion-intention locks](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking.html#innodb-gap-locks).

In a separate two-session cycle, the gap holder waited for a row
held by the transaction trying to insert into the gap. Both engines
aborted one transaction with 1213 and let the other proceed. MySQL
aborted the insert, and Fsdb aborted the row update.

The autocommit probes waited about one second before returning 1205
for both an insert into the gap and a key move into it, using
`innodb_lock_wait_timeout=1` in the contender session.

## Changes to neighboring keys

| Gate change while the missing lookup is protected | Observed MySQL behavior |
| --- | --- |
| Delete the lower record at `1` while a read view retains it | Inserts at `0` and `1` succeed. Inserts at `2`, `3`, and `4` still time out. Later probes can observe a wider gap after the reader ends and purge runs. |
| Delete the upper record at `5` | Inserts at `2` through `6` time out as protection reaches the next record at `9`. |
| Insert `6` before the lookup and keep the insertion pending, starting from keys `0` and `10` | The missing-key lookup proceeds. Inserts at `2` and `4` time out, and an insert at `7` succeeds. |
| Commit the pending `6` | The insert at `7` still succeeds. |
| Roll back the pending `6` | The insert at `7` now times out. |
| Insert `2` before the lookup and keep the insertion pending, starting from keys `0` and `10` | The missing-key lookup proceeds. Inserts at `3` and `5` time out, and an insert at `1` succeeds. |
| Commit the pending `2` | The insert at `1` still succeeds. |
| Roll back the pending `2` | The insert at `1` now times out. |

A pending insertion can bound the gap on either side. When it rolls
back, protection extends to the next surviving record on that side.
Fsdb coordinates missing-key interval claims and primary-key writes
under the same table monitor. The signed composite-key probes also
matched MySQL with both ascending and descending order on the
second key part.

### Consistent read views

A consistent read view opened before the deletion of the upper record
at `5` delays the change in the gap. With that view open, an insert at
`4` times out and an insert at `6` succeeds after the deletion commits.
This holds when the gap holder owns the read view and when an independent
session owns it. After the independent session ends, the insert at `6`
eventually times out while the gap holder stays open. The oracle allows
purge time after the reader ends.
[InnoDB multi-versioning and purge](https://dev.mysql.com/doc/refman/8.4/en/innodb-multi-versioning.html).

The lower boundary can also widen after purge. With a lookup for `3`
and a lower record at `2`, a separate snapshot can keep `2` in place
after its deletion. Inserting `1` succeeds while that snapshot is
open. In MySQL, the insert of `1` eventually waits after the reader
ends. This also applies when the lower record was pending at lookup
time and committed before deletion. Fsdb widens the gap on the next
affected write once the read view ends. MySQL widens it when its
background purge removes the deleted record.

## Cross-schema foreign keys

With a qualified foreign key from `gate.id` to a key in another
database, both engines return 1205 for a direct move of `gate.id`
into the protected gap and for a parent key update that moves it
via `ON UPDATE CASCADE`. Both allow the moves after the holder
rolls back. All 75 schedule checks match on both engines.

An autocommit cascade also waited while another connection
committed disjoint rows in both databases. Both engines published
the cascade after the gap holder rolled back and preserved the
disjoint rows. In a cycle between the cascade's parent row lock
and the destination gap, both engines returned 1213 to one
transaction and allowed the other to proceed. MySQL aborted
the row update, and Fsdb aborted the cascade.

With Fsdb WAL enabled, a timed-out cascade left both keys at
`5`. The subsequent successful cascade moved both to `4`.
The committed values survived both a clean restart and a forced stop.

## Unsigned primary keys

The digest-pinned MySQL 8.4.11 image and Fsdb agree on direct and
projected `BIGINT UNSIGNED` probes above the signed range, including a
gap whose upper boundary is `18446744073709551615`. The planner carries
exact integer equality bounds through same-typed join columns.
Storage reads both signed and unsigned components from the existing
numeric lock-key encoding before comparing the ordered key.
[MySQL integer ranges](https://dev.mysql.com/doc/refman/8.4/en/integer-types.html).

The maintained oracle matches all 99 schedules in both Fsdb modes,
including the original 75 signed-key checks.

At REPEATABLE READ and SERIALIZABLE, an insert or key move into the
missing interval returns 1205 while an exterior insert succeeds.
READ UNCOMMITTED and READ COMMITTED allow both writes. A pending
predecessor bounds the interval until rollback, after which the
interval widens. Contradictory predicates leave the interval free.
When the looked-up row exists, the update reads its current value and
retains a shared row lock. A derived-table probe with a small integer
literal also gives the same gap behavior as MySQL.

A concurrent insert at the exact unsigned probe key makes the
joined update wait. After commit, the update sees the published row
and affects one target. After rollback, it affects zero targets.

A separate composite check used an unsigned leading key above
`Int64.MaxValue` and a signed trailing key, in both ASC and DESC
order. Both engines blocked inserts and key moves between the
neighbors and allowed inserts outside that interval.

## Prepared XA and restart

A branch with only the zero-row update allows gap inserts after
`XA PREPARE`. A branch that also changes `parent.id=3` retains
the gap until the branch commits or rolls back in MySQL and Fsdb.
In-memory Fsdb now uses the transaction's recorded row rollback work
to detect surviving writes when no event stream has been buffered.
A matched no-op update or rollback to a savepoint before the only
write still lets prepare release the locks.
[MySQL XA transaction states](https://dev.mysql.com/doc/refman/8.4/en/xa-states.html).

In-memory Fsdb and MySQL also keep row locks for writing branches at
READ UNCOMMITTED and READ COMMITTED, with the gap free at both levels.
At REPEATABLE READ and SERIALIZABLE, a writing branch keeps the row
and gap locks. An in-memory SERIALIZABLE branch can still commit
after another connection updates a disjoint row in the same table.
The WAL-backed SERIALIZABLE branch currently returns 1205 in that
separate control because its commit validates the full snapshot.

A two-row CJK ngram control also agrees with MySQL in memory mode.
The prepared row and its posting stay invisible to other sessions
while a duplicate-key insert waits. Commit publishes both the row and
posting. Rollback leaves the original row and posting visible.

Separate row-lock checks at REPEATABLE READ and SERIALIZABLE matched
on MySQL and Fsdb. A locking read and a matched no-op update both
released their row locks at prepare. A branch that changed the row
kept its lock. All six branches remained visible in `XA RECOVER`
and completed successfully.

Separate runs prepared the writing branch, confirmed both waits,
and reopened the same MySQL data directory after SIGTERM and
after SIGKILL. `XA RECOVER` listed the branch after both restarts.

| Probe | Before restart | After either restart |
| --- | --- | --- |
| `INSERT INTO gate VALUES (2,0)` | 1205 | One row inserted |
| `UPDATE parent SET n=99 WHERE id=3` | 1205 | 1205 |

Fsdb matched both restart checks. The recovered branch retains its
written-row protection. The gap from the read probe has process
lifetime in this fixture.

Run the live schedules against disposable servers. Use `--data-dir`
to run Fsdb with WAL. Omit `FSDB_PORT` for the MySQL reference alone.

```sh
MYSQL_PORT=3316 FSDB_PORT=3307 uv run --with pymysql python torture/scripts/joined-view-primary-gap-oracle.py
```
