# Duplicate-key updates through checked join views

MySQL 8.4.11 checks the proposed insert before looking for a duplicate.
After coercion, it runs CHECK OPTION again only if the duplicate update
changes the stored row. A visible proposal can therefore leave an
unchanged hidden duplicate in place with no rows affected. A changed
duplicate can also become visible when it takes the proposed insert's
join key. Moving away from the proposed partner can raise error 1369
even if the new key would find another row in the joined table.
[MySQL 8.4.11 duplicate update path](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_insert.cc#L1797-L1810)

Fsdb previously rejected all duplicate updates through a checked join
view. It now evaluates the assignments and uses the proposed insert's
join partners for the changed-row check. Ordinary UPDATE through the
same view checks its original joined row. An ignored violation emits
warning 1369 and leaves the stored row unchanged while processing the
remaining insert candidates.

The [SQL fixture](2026-11-27-checked-join-upserts.sql) matched 28 output
rows and four error lines against MySQL 8.4.11 in memory and on disk. It
covers VALUES, SET, INSERT SELECT, prepared execution, transaction
rollback, a layered LOCAL check, a cross join, and an attempt to switch
to another valid join key. A seeded run of 144 writes matched affected
counts, warnings, and stored rows in both modes.

## Waiting for a duplicate row

Autocommit upserts through a view run inside an implicit statement
transaction. They now refresh the target row after waiting for a
competing writer. The duplicate update and its check use the committed
row, or the previous row if the holder rolled back.

The checked cross-join probe used `WHERE t.k=1 AND t.n<10`. One session
updated the duplicate and held its transaction open. Another upserted
two rows with `INSERT IGNORE ... ON DUPLICATE KEY UPDATE`. Four lock
schedules matched MySQL in memory and on disk for both VALUES and
INSERT SELECT. They included a valid changed row, a post-wait check
failure followed by a valid insert, a newly visible duplicate, and a
rollback by the competing writer.

The equijoin variant also matched those four schedules in an explicit
transaction after reading the joined table before opening the transaction.
When the read occurs inside the transaction before a wait, MySQL keeps
the old row visible to plain SELECT after a rejected duplicate update.
The [consistent-read follow-up](2026-11-27-repeatable-read-after-write-refresh.md)
closes the case where Fsdb previously showed the holder's committed value.

## Read-side record buffers

The existing [read-side record-buffer divergence](2026-11-04-joined-view-insert-check-order.md)
also applies to the initial insert check in an upsert. For example, the
three-key setup from the [earlier audit](2026-10-14-checked-join-view.md)
rejects a proposed first-key row in MySQL even when a matching partner
exists. Fsdb continues to validate the joined rows produced for each
proposal. The fixture uses a single eligible read-side row where
possible and exercises the last read-side key in the two-key case.

It also reproduces across connections with one read-side row. A fresh
MySQL session warns twice and discards a checked two-row upsert through
`t JOIN g ON g.k=t.k WHERE t.n<g.ceiling`. In the same session, reading
`g` before repeating the upsert allows the duplicate update and the new
insert. Fsdb evaluates the joined rows in both cases.
