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
fixes the corresponding wait in audited autocommit joins.

## Warning order and retention

Add row `(4,7,5)` before starting either session, then use this update
in session B.

```sql
UPDATE IGNORE ai
SET k=CASE id WHEN 1 THEN 2 WHEN 2 THEN 1 WHEN 3 THEN 7 ELSE 8 END,
    qty=qty+1
WHERE id<9
ORDER BY id;
SHOW COUNT(*) WARNINGS;
SHOW WARNINGS;
```

If session A commits, row 3 encounters key 7 while the batch is
staged. The post-wait check finds duplicates for keys 2 and 1 in rows
1 and 2. Fsdb now records the late warnings at their original row
positions, so the order is 2, 1, 7 and row 4 still changes. With
`max_error_count=2`, MySQL and Fsdb both retain warnings 2 and 1 and
report three warnings in total. [MySQL SHOW WARNINGS reference](https://dev.mysql.com/doc/refman/8.4/en/show-warnings.html)

Thirty-two schedules matched MySQL 8.4.11 in memory and on disk.
They covered retention limits of 64, 2, 1, and 0, autocommit and
explicit transactions, and both outcomes for the key holder.
The variant with `qty=CASE id WHEN 1 THEN 1/0 ELSE qty+1 END`
also verified that the division warning stays ahead of the first
late duplicate.

The [assignment coercion follow-up](2026-11-27-ignored-update-coercions.md)
covers invalid SET values and warning order without contention.
