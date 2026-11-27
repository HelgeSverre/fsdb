# Concurrent writes to moved unique secondary keys

An UPDATE claims its changed unique index destinations before publishing
the row. The claim uses the same collation, prefix, and expression
encoding as INSERT. It stays held through commit or rollback. This also
covers unique keys moved by referential updates.
[MySQL 8.4 InnoDB locking reference](https://dev.mysql.com/doc/refman/8.4/en/innodb-locks-set.html)

In the original race, Fsdb let the contender upsert ahead of an
uncommitted UPDATE. Both engines now make the upsert wait. If the
UPDATE commits, the contender updates the moved row and then reuses
the candidate ID. If the UPDATE rolls back, the contender inserts
both rows.
[Two-session setup](2026-11-27-checked-upsert-reused-auto-ids.md#concurrent-secondary-key-moves)

The reverse order also matters. On a table with `(id,k,qty) = (1,1,5)`,
session A starts a transaction and inserts `(9,2,7)`. Session B
runs `UPDATE ai SET k=2,qty=9 WHERE id=1` and waits. After A commits,
B returns 1062 with SQLSTATE 23000 and leaves the existing row alone.
If A rolls back, B changes the existing row to `(1,2,9)`. Fsdb
rechecks the staged destination after obtaining its lock, using the
latest catalog without re-evaluating the assignment.

Eleven schedules matched MySQL 8.4.11 in both Fsdb storage modes. The
audit included forward commit and rollback, a composite text-prefix key,
a functional key, a cross-database foreign-key cascade, disjoint keys,
nullable keys, and reverse commit and rollback in autocommit and an
open transaction.

The [UPDATE IGNORE follow-up](2026-11-27-ignored-update-key-waits.md)
fixes the original 1205 for single-table batches. Joined autocommit
writes and mixed warning ordering have separate gaps.
