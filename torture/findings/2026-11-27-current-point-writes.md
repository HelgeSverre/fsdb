# Point writes after a unique key moves

A repeatable-read transaction can update or delete a row that became
visible to writes after its first plain SELECT. Given a table
`t(id INT PRIMARY KEY, n INT)` with row `(1,5)`, session A can begin and
read `t`. Session B can then move the row with `UPDATE t SET id=4 WHERE
id=1`. Session A's `UPDATE t SET n=n+1 WHERE id=4` affects one row.
Within A, a plain SELECT can still see `(1,5)` beside its own `(4,6)`.
After A commits, a new read sees `(4,6)`.
[MySQL consistent nonlocking reads](https://dev.mysql.com/doc/refman/8.4/en/innodb-consistent-read.html)

Fsdb previously planned these writes using row identities from the old
catalog. A missing key produced an empty target and let the write return
early. For exact equality on a complete unique key, the plan now claims
the key even when its row is absent. If the claim finds a live row, Fsdb
takes that row's write lock before refreshing the working catalog. A later
transaction can hold the row under a different key, so waiting for the
first key claim alone could still leave a stale row image and error 1205.
If the refreshed key still has no row, Fsdb releases the new probe lock.
Two transactions can then issue the same missing-key update and each
return zero rows.

Twenty-four schedules matched MySQL 8.4.11 in memory and on disk. The
first eighteen covered UPDATE and DELETE through physical tables and a
renamed, mergeable view, with a composite primary key, a unique secondary
key, an insertion, and key moves. Each form covered a prior commit and
waiting through commit or rollback. Six additional schedules placed a
second writer on the row after its key move had already committed.

The [missing primary-key follow-up](2026-11-27-missing-primary-write-gaps.md)
adds the surrounding gap after a genuine miss. With integer primary keys
1 and 9, an `UPDATE ... WHERE id=4` can affect zero rows and block a
neighboring insert at key 5 until the repeatable-read transaction ends.
