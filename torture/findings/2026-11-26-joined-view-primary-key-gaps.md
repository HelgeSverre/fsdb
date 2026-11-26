# Missing primary-key probes in joined updates

MySQL 8.4.11 and Fsdb ran on separate disposable
loopback servers. The [schedule oracle](../scripts/joined-view-primary-gap-oracle.py)
creates its own schema with two target rows, their joined partners,
and a separate `gate` table. The update probes `gate.id=3` while
writing through a joined view. The key is absent, and the update
affects zero rows.

MySQL matched all 58 checks. Fsdb matched 37. The remaining differences
are insertions and primary-key moves that succeed on Fsdb while
MySQL returns lock wait timeout 1205. The oracle rolls back each
test insert so the next attempt sees the same data.

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

## Changes to neighboring keys

| Gate change while the missing lookup is protected | Observed MySQL behavior |
| --- | --- |
| Delete the lower record at `1` | Inserts at `0` and `1` succeed. Inserts at `2`, `3`, and `4` still time out. |
| Delete the upper record at `5` | Inserts at `2` through `6` time out as protection reaches the next record at `9`. |
| Insert `6` before the lookup and keep the insertion pending, starting from keys `0` and `10` | The missing-key lookup proceeds. Inserts at `2` and `4` time out, and an insert at `7` succeeds. |
| Commit the pending `6` | The insert at `7` still succeeds. |
| Roll back the pending `6` | The insert at `7` now times out. |
| Insert `2` before the lookup and keep the insertion pending, starting from keys `0` and `10` | The missing-key lookup proceeds. Inserts at `3` and `5` time out, and an insert at `1` succeeds. |
| Commit the pending `2` | The insert at `1` still succeeds. |
| Roll back the pending `2` | The insert at `1` now times out. |

A pending insertion can bound the gap on either side. When it rolls
back, protection extends to the next surviving record on that side.
A deletion of a previously committed lower record follows the narrower
boundary observed above. The Fsdb joined-read path tracks retained row
IDs and waits on the exact probed key.

### Consistent read views

A consistent read view opened before the deletion of the upper record
at `5` delays the change in the gap. With that view open, an insert at
`4` times out and an insert at `6` succeeds after the deletion commits.
This holds when the gap holder owns the read view and when an independent
session owns it. After the independent session ends, the insert at `6`
eventually times out while the gap holder stays open. The oracle allows
purge time after the reader ends.
[InnoDB multi-versioning and purge](https://dev.mysql.com/doc/refman/8.4/en/innodb-multi-versioning.html).

## Prepared XA and restart

A branch with only the zero-row update allows gap inserts after
`XA PREPARE`. A branch that also changes `parent.id=3` retains
the gap until `XA ROLLBACK` while the server runs.

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

The recovered branch retains its written-row protection. The
gap from the read probe has process lifetime in this fixture.

Run the live schedules against disposable native servers. Omit
`FSDB_PORT` for the MySQL reference alone.

```sh
MYSQL_PORT=3316 FSDB_PORT=3307 uv run --with pymysql python torture/scripts/joined-view-primary-gap-oracle.py
```
