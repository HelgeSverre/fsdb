# Pending inserts in ordered secondary locking reads

An ordered secondary-index seek with a fixed equality prefix can encounter
an uncommitted insert before the first visible row. In this fixture, a
session holds an uncommitted insert at `(project_id,stamp,id) = (7,5,20)`.
A second transaction then runs the ordered seek.

```sql
CREATE TABLE jobs(
    id INT PRIMARY KEY, project_id INT NOT NULL, stamp INT NOT NULL,
    INDEX ix(project_id,stamp,id)
);
INSERT INTO jobs VALUES (10,7,10),(30,7,30),(50,8,50);

-- Session A
START TRANSACTION;
INSERT INTO jobs VALUES (20,7,5);

-- Session B
START TRANSACTION;
SELECT id FROM jobs FORCE INDEX(ix)
WHERE project_id=7 ORDER BY stamp,id LIMIT 1 FOR UPDATE NOWAIT;
```

With the insert still pending, MySQL 8.4.11 returns 3572 for NOWAIT and
row 10 for SKIP LOCKED. At `539b652d` Fsdb returned row 10 in both cases.
The ordered read now checks pending keys as it visits the committed index
slice. A pending key after the requested limit does not affect the result.
SKIP LOCKED counts successful visits toward OFFSET and LIMIT, and a lower
isolation level checks an OFFSET row without retaining its row lock.
The path applies to a complete numeric index order with the primary key
columns explicitly present in the secondary index.

The digest-pinned comparison matched 768 read outcomes in memory and with
WAL. The cases combine integer and DECIMAL sort keys, four isolation levels,
forward and reverse traversal, four pending insert positions, offsets,
FOR UPDATE and FOR SHARE, and forced or selected secondary access. A primary
key probe from a third session also agreed on row ownership after all 384
FOR UPDATE cases. Separate checks agreed for autocommit and for a reader
that had inserted its own row. An Expecto regression verifies that a
concurrent deletion reopens the seek and releases the stale row claim.
Covering FOR SHARE reads still differ in primary record ownership.
Secondary next-key gaps and general residual filtering remain open.

The blocking form of the same read is also open. Under READ COMMITTED
and REPEATABLE READ, MySQL waits for the pending insertion. It returns
the new first row after COMMIT and the original first row after ROLLBACK.
Fsdb returns the original first row immediately in both schedules.
