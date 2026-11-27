# Batched UPDATE IGNORE after a unique-key wait

Fsdb rechecks the staged row images against the current unique indexes
after acquiring their destination locks. It processes those images in
statement order. If it skips a row, that row keeps its original key,
so a later change that would have taken the key can also be skipped.
The publication and WAL events include only the accepted updates.

To reproduce, prepare one table.

```sql
CREATE TABLE ai(id INT PRIMARY KEY,k INT UNIQUE,qty INT);
INSERT INTO ai VALUES(1,1,5),(2,3,5),(3,5,5);
```

In session A, hold a pending insert.

```sql
START TRANSACTION;
INSERT INTO ai VALUES(9,2,7);
```

In session B, begin this statement before ending session A's transaction.

```sql
UPDATE IGNORE ai
SET k=CASE id WHEN 1 THEN 2 WHEN 2 THEN 1 ELSE 6 END,
    qty=qty+1
WHERE id<9
ORDER BY id;
SHOW WARNINGS;
```

If A commits, B produces 1062 warnings for keys 2 and 1 and only
changes row 3 to `(3,6,6)`. If A rolls back, B applies all three changes.
Twelve schedules matched MySQL 8.4.11 in memory and on disk, covering
autocommit and open transactions, a cross-database foreign key, and
a session counter that confirmed exactly one assignment evaluation
per candidate. The regression also checks the committed rows after
WAL replay.

The [joined UPDATE follow-up](2026-11-27-joined-update-key-waits.md)
fixes the corresponding wait in audited autocommit joins. If an
initial duplicate and a newly committed duplicate affect the same
batch, Fsdb can report the initial duplicate first even when its row
came later in the statement. The tested mixed batch retained the
correct rows and warning contents.
