# AUTO_INCREMENT in ignored INSERT SELECT upserts through checked views

MySQL 8.4.11 checks the candidate row before allocating its generated ID.
If every candidate fails, the next statement can still obtain ID 1 on a
fresh table. Once an eligible row needs an ID, the server requests blocks
of 1, 2, 4, and so on. Two or three eligible generated rows leave ID 4 for
the next statement, and four or five leave ID 8.
[MySQL 8.4.11 interval requests](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/handler.cc#L3542-L3595)

If an accepted explicit ID moves the cursor beyond a partly used block,
InnoDB reserves the remaining attempt budget at the new position. Rows
that fail CHECK OPTION never reach the write attempt. The same rule also
applies to interrupted VALUES intervals.
[InnoDB row-attempt accounting](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/handler/ha_innodb.cc#L8681-L8695)
[InnoDB interval reservation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/handler/ha_innodb.cc#L18533-L18571)

The SQL fixture produced the same 34 output rows and empty error streams
on digest-pinned MySQL and both Fsdb storage modes. It uses omitted,
explicit, NULL, and zero IDs, including `NO_AUTO_VALUE_ON_ZERO` and a
prepared statement in a transaction that rolls back. A second run with
seed 41309 matched all 120 batches and 4,032 output rows in each mode,
including high explicit IDs interleaved with generated IDs. The WAL test
reopens the database without a checkpoint after autocommit, explicit
commit, and rollback.
[SQL fixture](2026-11-27-checked-upsert-select-auto-ids.sql)

The [candidate ID reuse follow-up](2026-11-27-checked-upsert-reused-auto-ids.md)
covers secondary-key duplicates in both input forms.
