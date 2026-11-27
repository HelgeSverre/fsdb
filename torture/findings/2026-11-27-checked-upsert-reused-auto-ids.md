# Generated ID reuse in ignored upserts through checked views

When a generated candidate hits a secondary-key duplicate, the
duplicate update can store that generated ID. Otherwise the next
candidate can reuse it. An explicit assignment to a different ID in
the current reserved interval fails with 1869 even under IGNORE.
The reserved interval survives reuse, and an accepted explicit
update above the interval advances the table counter.
[MySQL duplicate update implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_insert.cc#L1732-L1809)

Fsdb processes the checked candidates in order in a transaction
snapshot. Each candidate claims the keys for its actual ID and
refreshes the matching row before applying the duplicate update.
A regression covers a concurrent insert that commits the duplicate
key while the upsert waits.

The fixture matched 26 output rows, warning 1369, and error 1869
with SQLSTATE HY000 against MySQL 8.4.11 in both Fsdb storage modes.
It covers VALUES, a prepared INSERT SELECT, rejected duplicate
updates, and rollback. A seeded run (77681) matched 150 batches
and 2,191 output rows in each mode. An additional regression verifies
that error 1869 discards rows already written by the statement.
[SQL fixture](2026-11-27-checked-upsert-reused-auto-ids.sql)

## Concurrent secondary-key moves

The destination of a unique secondary-key UPDATE still lacks the
same wait behavior as a pending insert. Prepare this schema once.

```sql
CREATE TABLE ai(id INT AUTO_INCREMENT PRIMARY KEY,k INT UNIQUE,qty INT);
CREATE VIEW checked_ai AS SELECT * FROM ai WHERE qty>0 WITH CHECK OPTION;
INSERT INTO ai VALUES(1,1,5);
```

In session A, run the UPDATE and keep the transaction open.

```sql
START TRANSACTION;
UPDATE ai SET k=2,qty=7 WHERE id=1;
```

Start the upsert in session B before committing session A.

```sql
START TRANSACTION;
INSERT IGNORE INTO checked_ai(k,qty) VALUES(2,5),(3,5)
    ON DUPLICATE KEY UPDATE qty=qty+1;
COMMIT;
```

MySQL waits in session B. After A commits, B updates the moved row,
inserts the next row with the reused ID, and reports three affected
rows. The final rows are `(1,2,8)` and `(2,3,5)`. In the Fsdb run,
B completed before A committed and reported two affected rows. A
then returned 1205 on COMMIT. The final rows were `(1,1,5)`,
`(2,2,5)`, and `(3,3,5)`.
