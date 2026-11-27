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
ownership probes now match in all 256 schedules in both modes.

## Rolled-back key changes at OFFSET

At READ COMMITTED, a pending secondary-key move can make the cursor wait
on a record it will skip for OFFSET. When the writer rolls back, the cursor
returns the next row and releases the skipped record. A payload-only update,
an update to a different index, or a key change reversed by a second UPDATE
allows the skipped record to remain locked. At REPEATABLE READ the skipped
record remains locked after a key change rolls back.

Fsdb previously retained the skipped record in eight schedules covering
both directions, integer and DECIMAL keys, and moves within and out of
the equality slice. The cursor now checks the blocking owner's last row
image when it resumes and reopens the seek after dropping its new claim
on a rewritten OFFSET record. Reopening preserves locks the cursor
already held. Additional MySQL comparisons covered reversals in the same
transaction, updates to a different index, and rollback to a savepoint.

## Next-key locks

At REPEATABLE READ and SERIALIZABLE, an ordered exact-numeric secondary
cursor protects the physical gap before each visited record. A reverse
cursor also protects the gap above its starting record. When the cursor
exhausts its bounded slice before the limit, it visits the stopping gap
and can claim the neighboring record without returning it. For example,
the pinned MySQL 8.4.11 image reports `X` on a rejected numeric
range successor within the equality prefix, and `X,GAP` on the next
prefix after an exhausted forward equality seek.

Fsdb admits destination index keys and secondary gaps under the same
lock monitor. Both inserts and staged UPDATE key moves wait on gaps
held by another transaction. READ COMMITTED retains its row-only path.
Reversing a pending move back to the stored key keeps the original
clustered-record wait behavior.

An INSERT with a known duplicate still claims the matching record.
Its proposed secondary key does not enter the index and does not wait
on a gap. REPLACE still checks the replacement key because it can enter
the gap. Destination claims for the update branch of a duplicate-key
upsert remain open.

A pending uncommitted key can bound the gap after `SKIP LOCKED`.
In the audited case, the cursor returns score 10 while a pending score
5 bounds the preceding gap. A writer can insert score 2 and must wait
for score 7. Rolling back the pending insertion widens the protected
gap to include score 2. Committing it keeps score 5 as the boundary.

The MySQL 8.4.11 replay matched all 183 writer probes in memory and WAL
mode, including inserts, key moves, empty and exhausted ranges, a
descending DECIMAL index, shared reads, and the READ COMMITTED controls.
Separate waits resumed after the reader committed or rolled back.
Rolling the reader back to a preceding savepoint also released its gap
in the audited case.

The audited path requires whole exact-numeric key parts and explicit
primary-key columns in the secondary index. Implicit suffixes, mixed
residual predicates, purge of ordinary secondary boundary records, and
covering shared-record ownership remain open.
