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

## Blocking reads

The same ordered seek without NOWAIT waits on a pending insert or locked
row if the cursor reaches it before filling the limit. A wait uses the
transaction lock graph and the session's lock wait timeout. The cursor
reopens on the current index after the blocker resolves. In the fixture,
both engines return the new first row after COMMIT and the original first
row after ROLLBACK. Fsdb at `a1d11837` returned the original row immediately
in both schedules.

A timeout returns 1205 and retains the earlier visited row locks. A row
that changes secondary key during the wait is rechecked at its new
position. The read releases the claim on the obsolete index entry.
Successive waits each get the session's configured timeout. With a
one-second timeout and the first blocker released after 650 ms, both
engines timed out on the second blocker after about 1.65 seconds total.
A two-session cycle between pending insert keys returned 1213 for one
reader and let the other reader complete.

The pinned MySQL comparison matched the wait decision and result in all
256 integer and DECIMAL schedules in memory and with WAL. It covered
pending inserts and existing row locks before and after the limit,
secondary-key changes, commit and rollback, both traversal directions,
and OFFSET under READ COMMITTED and REPEATABLE READ. The primary row
ownership probes matched in 248 schedules. In the other eight, a
READ COMMITTED OFFSET consumed a row whose pending secondary-key change
was rolled back. Fsdb retained that row lock and MySQL released it.
