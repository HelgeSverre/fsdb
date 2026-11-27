# Gap locks after missing primary-key writes

For an exact seek on a complete integer primary key, a missing UPDATE
or DELETE in MySQL 8.4.11 protects the gap between neighboring records
under REPEATABLE READ and SERIALIZABLE. Two missing seeks in the same
gap can both return zero affected rows. An insert or a move into that
gap waits for both holders. READ COMMITTED and READ UNCOMMITTED allow
the neighboring insert in the audited cases.
[MySQL InnoDB gap locks](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking.html#innodb-gap-locks)

To reproduce, start with `t(id INT PRIMARY KEY, n INT)` containing keys
1 and 9. In session A, start a repeatable-read transaction and run
`UPDATE t SET n=n+1 WHERE id=4`. In session B, repeat the UPDATE in a
transaction. Both report zero. In session C, set
`innodb_lock_wait_timeout=1` and try an INSERT at key 5. It times out
until both transactions have ended. An insert at key 10 can proceed.

Fsdb reuses its integer primary-key read gap logic for the refreshed
write view. The gap is installed before releasing the exact key probe.
Write searches can share a gap. Inserts and primary-key moves still
check for conflicting gap holders. Rolling back to a savepoint set after
the first table access keeps the write's gap in the audited case. Rolling
back to a savepoint set before the first table access releases it.

Twenty-four differential schedules matched in memory and on disk. They
covered a descending composite key, UPDATE, DELETE through a mergeable
view, all four isolation levels, and both savepoint states. Two further
schedules confirmed that a pending insert or move rolling back leaves the
waiting writer's gap protected. The destination-move, point-hit, and
`LIMIT 0` controls also matched.

## Decimal and binary primary keys

Missing exact writes also protect gaps in complete primary keys made of
integer, `DECIMAL`, `BINARY`, and `VARBINARY` columns. Fsdb decodes the
values in the lock key and uses the stored index order. Decimal values
retain their fractional digits above `2^53`, and binary values retain
their bytes, including an empty key.

The comparison ran six key fixtures at each isolation level, in memory
and with WAL enabled. Both engines let UPDATE and DELETE through a
mergeable view share the gap. Inserts and key moves inside it waited
under REPEATABLE READ and SERIALIZABLE. Inserts outside the gap could
proceed. Lower isolation levels let the neighboring insert proceed.
Two further schedules checked that a pending insert could roll back,
after which the waiting UPDATE missed and protected the refreshed gap.

For a decimal reproduction, substitute `id DECIMAL(20,4) PRIMARY KEY`
in the setup above and use `1.2500` and `9.7500` as boundary keys.
Seek `4.1250` and try to insert `5.1250`. For binary, use a
`VARBINARY(4)` primary key and substitute `X'10'`, `X'90'`,
`X'40'`, and `X'50'`, respectively.

## Text primary keys

With `PRIMARY KEY(k ASC, seq DESC)` and an accent-insensitive
`k`, MySQL orders equivalent spellings by the following key part.
A miss at `('E',30)` between `('é',50)` and `('e',10)`
blocks an insert at `('É',20)` while allowing `('ê',60)`. Before
the fix, Fsdb allowed both inserts.

Fsdb compares the canonical collation keys already present in write
claims. The boundary search uses the stored primary order, looking
within each equivalent spelling's run and choosing the nearest
complete key across those runs. It handles equivalent spellings in
the adjacent prefix too, where the boundary is the first or last key
in the adjacent group.

The comparison covered six text key fixtures at all four isolation
levels, including `CHAR` with PAD SPACE, case-sensitive ordering,
and composite keys with mixed collations and directions. Two additional
schedules covered a pending insert whose spelling differs from the
waiting UPDATE's literal. After the insert rolled back, the UPDATE
missed and protected the refreshed gap. The tests ran in memory and
with WAL enabled. The point-hit, `LIMIT 0`, and before- and
after-access savepoint controls also agreed.

The fixtures reset the index to isolate the boundary records.
Other primary-key types, secondary-key gaps, broader write ranges,
and boundaries influenced by delete-marked records remain open.

## Exact primary-key locking reads

An exact `SELECT ... FOR UPDATE` or `FOR SHARE` on a complete collated
text, decimal, or binary primary key now uses the same key identity as
writes. In a REPEATABLE READ transaction, a missing seek protects the
gap while a plain SELECT leaves it open. In SERIALIZABLE, all three
forms protect the gap. In READ COMMITTED and READ UNCOMMITTED,
neighboring inserts can proceed and two readers can miss the same key
without locking unrelated records.

To reproduce, reuse the descending text fixture above and start two
REPEATABLE READ transactions. Run `SELECT n FROM t WHERE k='E' AND
seq=30 FOR SHARE` in both. From a third session, an insert at `('ê',60)`
can proceed. An insert at `('É',20)` times out until both readers end
their transactions. The same schedules cover PAD SPACE collation and
decimal keys with distinct fractional digits above `2^53`.

When the exact key has a pending insert, `FOR UPDATE` waits for the
writer. If the writer commits, the reader locks the new row and leaves
the neighboring gap open. If the writer rolls back, the reader returns
no rows and protects the gap at REPEATABLE READ. At READ COMMITTED and
READ UNCOMMITTED, the reader also waits for the exact key, and the
neighboring gap remains open whichever way the writer finishes.
Secondary-key reads and collated text primary ranges remain outside this coverage.

All 108 schedules matched MySQL 8.4.11 in memory and with WAL enabled.
The matrix covered seven key fixtures at four isolation levels, with
`FOR UPDATE`, `FOR SHARE`, and plain SELECT. Another 24 schedules
covered pending inserts that committed or rolled back, including a
shared lock on the row published after the wait.

## Exact NOWAIT and SKIP LOCKED reads

With `FOR UPDATE` and `FOR SHARE`, an exact primary-key `NOWAIT` read
reports 3572 when another transaction holds the matching record or is
inserting it. `SKIP LOCKED` returns an empty result for either conflict.
The same conflict results apply to single statements in autocommit mode.
At REPEATABLE READ and SERIALIZABLE, a genuine miss protects the gap
around the search key with either wait option. When the read skips a
conflicting key, it protects the gap after that key in index order.
At READ COMMITTED and READ UNCOMMITTED, it leaves that gap open.
[MySQL locking-read options](https://dev.mysql.com/doc/refman/8.4/en/select.html)

For a reproduction, seed an integer primary key with 10 and 60. Leave
an insert at 30 uncommitted in session A. In session B, start a
REPEATABLE READ transaction and run
`SELECT id FROM t WHERE id=30 FOR UPDATE SKIP LOCKED`. It returns no
rows. Session C can insert 20. Inserting 40 times out until session B
ends, even if session A first rolls back. The rollback of the pending
insert also lets the protected gap expand to cover 30.

The exact lookup checks the matching key before taking any gaps. A
committed matching row can be locked without blocking an insert next to
it.

The 464 exact-key schedules matched MySQL 8.4.11 in memory and with WAL
enabled. They covered integer, collated text, decimal, and binary keys,
including a descending composite key and both outcomes of a pending
insert. The autocommit schedules covered every isolation level.

### Ordered range limits

`NOWAIT` on an integer primary-key range honors an ordered `LIMIT`
when the chosen primary scan can stop in index order. With keys 10,
20, 30, 40, and 50, hold a lock on 30 and run
`SELECT id FROM t WHERE id BETWEEN 15 AND 45 ORDER BY id LIMIT 1 FOR UPDATE NOWAIT`.
The query returns 20. Changing the limit to 2 reports 3572 and keeps
the lock on 20. A pending insert in the visited prefix also causes 3572.

At REPEATABLE READ and SERIALIZABLE the failed read keeps gaps it
already visited. At READ COMMITTED and READ UNCOMMITTED it checks
`OFFSET` rows without retaining their locks. An exhausted forward scan
at lower isolation also checks the first rejected record, including a
pending insert before the first committed record beyond the bound.

All 200 schedules matched MySQL 8.4.11 in memory and with WAL enabled.
They covered both scan directions and lock strengths, all four isolation
levels, `OFFSET`, and a descending composite key with `FORCE INDEX`.

Ordered integer primary-key ranges with `SKIP LOCKED` count only
admitted records toward `LIMIT` and `OFFSET`. With the same keys, hold
20 and replace `NOWAIT` with `SKIP LOCKED`. The query returns 30 and an
update to 40 can proceed. Fsdb evaluates only the keys selected by the
locking scan.

At REPEATABLE READ, hold 30 and use `LIMIT 2 ... SKIP LOCKED` over the
same range in ascending order. MySQL returns 20 and 40. An insert at 27
can proceed while an insert at 33 waits.

In a reverse equality-prefix scan, the predecessor outside the prefix
stays available while the gap before it is protected. The comparison ran
240 `SKIP LOCKED` schedules against MySQL 8.4.11 in memory and with WAL
enabled, including empty prefixes, pending inserts, and both directions
of composite primary keys. The queue schedules also verified that five
concurrent readers each acquired a different row and left a later row
available for updates.

Ranges that require a filesort or have competing access paths remain
outside the original ordered integer comparison. See the composite
filesort follow-up below.

## Decimal and binary primary-key ranges

The ordered locking cursor now handles primary keys formed from
integers, DECIMAL, BINARY, and VARBINARY. It uses exact stored key
values for the index bounds and the pending-write check, including
decimal fractions above `2^53`.

With DECIMAL keys `9007199254740993.00020`, `.00030`, and `.00040`,
hold the middle record in a writer transaction. An ascending range
read with `LIMIT 1 FOR UPDATE NOWAIT` returns the first key. With
`LIMIT 2`, the reader reports 3572 and retains the first record lock.
`LIMIT 2 FOR UPDATE SKIP LOCKED` returns the first and last keys,
leaving records beyond its scan available to other writers.

For the audited mixed-direction composite indexes, MySQL scans in
physical index order before filesort. A failure retains the rows the
cursor visited, and SKIP LOCKED filters out blocked keys before
sorting and applying the SQL offset. With a blocking ordered
`OFFSET` at READ COMMITTED, the cursor retains the skipped record
when it had to wait for it. An uncontended skipped record remains
available to writers.

The matrix improved from 136 to 576 exact matches in memory and with
WAL enabled. It covered all four isolation levels, both lock strengths,
both scan directions, composite keys, and pending inserts. Sixteen
blocking schedules also matched when a row appeared beyond a held
record or pending insert. The resumed statement returned and locked
the new row if it reached that part of the scan. Four blocking
offset schedules matched the retained locks. Binary prepared statements
with DECIMAL or VARBINARY bounds returned the first row at limit one
and the same NOWAIT error at limit two. The composite range and filesort
checks also matched after a checkpoint restart.
[MySQL locking reads](https://dev.mysql.com/doc/refman/8.4/en/innodb-locking-reads.html)

Collated text ranges, competing indexes, and other filesort shapes
remain outside this comparison.

## Locking reads with LIMIT 0

With a plain `LIMIT 0` or `LIMIT 0 OFFSET 0`, MySQL validates the query
and takes its normal table metadata locks. It skips row admission in
that query block and its inputs. Fsdb now does the same, including for
joins, derived tables, CTEs, and UNION branches. Invalid column names
and locking targets still report their ordinary errors.

To reproduce, let one transaction update key 2 in `t`. In another
transaction `SELECT id FROM t LIMIT 0 FOR UPDATE NOWAIT` returns a typed
empty result. Key 1 remains available to a third session. That session's
`ALTER TABLE t ADD COLUMN note INT` waits until the reader ends its
transaction. A prepared statement bound to limit zero has the same
row behavior. Executing it again with limit one reports 3572 if the
requested row is still locked.

The comparison matched MySQL 8.4.11 in 508 cases in memory and with WAL
enabled. It covered all four isolation levels, both lock strengths, each
wait mode, explicit transactions, autocommit, and validation errors.

`SQL_CALC_FOUND_ROWS` and a positive `OFFSET` can still scan and lock
records with `LIMIT 0`. The no-offset shortcut excludes those cases.

## FOUND_ROWS and OFFSET

With four matching rows, `LIMIT 1 OFFSET 2` returns one row and reports
three in `FOUND_ROWS()`. `LIMIT 0 OFFSET 9` reports four, the number of
rows it could consume. A nested query's offset contributes only to that
query block. `SQL_CALC_FOUND_ROWS` still counts the unbounded result.

An ordered integer primary-key locking read with `LIMIT 0 OFFSET n`
visits only the offset prefix when its plan can stop in index order.
`NOWAIT` can report a conflict in that prefix. `SKIP LOCKED` counts only
unlocked rows toward the offset and `FOUND_ROWS()`. At READ COMMITTED
and READ UNCOMMITTED it checks those records without retaining the locks.

The offset counts matched MySQL 8.4.11 across 411 schedules in memory and
with WAL enabled. These covered streaming and ordered reads, DISTINCT,
grouping, windows, prepared statements, nested queries, set operations,
and locking reads.

## Ordered projection assignments

In the audited MySQL 8.4.11 queries, a projection such as `@seq:=@seq+1`
uses the output order when the sort key is independent of the assignment.
`SELECT id,@seq:=@seq+1 FROM t ORDER BY id DESC LIMIT 1 OFFSET 1`
increments an initialized `@seq` once. The offset row and rows beyond the
limit leave it alone. Fsdb now evaluates these projections after the
filesort slice or while emitting rows from the ordered index.

`SQL_CALC_FOUND_ROWS` still counts matching rows outside the slice. A
division-by-zero warning from a projection appears only when the affected
row is projected in the audited example. The comparison covered 180
cases in memory and with WAL enabled, including arithmetic sort keys,
aliases, positions, assignment order within a row, and warnings. The
numeric comparison ignored the existing DECIMAL display-scale difference
for `@seq:=1/(id-1)`.

Sort keys and predicates that depend on the assigned variable, along
with DISTINCT and HAVING, can use plan-dependent evaluation. Those
shapes remain outside this coverage. MySQL leaves user-variable
evaluation order undefined in its manual.
[MySQL user-defined variables](https://dev.mysql.com/doc/refman/8.4/en/user-variables.html)
