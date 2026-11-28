# Repeatable reads after a write refresh

MySQL 8.4.11 keeps the committed catalog visible to the first ordinary
read in a repeatable-read transaction. A write can use a newer row.
Subsequent ordinary reads combine the original read view with only the
transaction's own changes. A locking read can see the current row without
changing the plain SELECT view.
[MySQL consistent nonlocking reads](https://dev.mysql.com/doc/refman/8.4/en/innodb-consistent-read.html)

Fsdb previously refreshed its write and read catalogs together. The
original failure used a checked upsert waiting for a duplicate. The
holder committed `n=9`, so the duplicate increment failed the `n<10`
check. MySQL still showed `n=5` to the waiting transaction's plain
SELECT. Fsdb showed `n=9`. Both returned warning 1369 and inserted the
second candidate. The lock schedule now matches in memory and on disk.

To reproduce the same visibility rule without a lock wait, create
`t(id INT PRIMARY KEY, n INT)` with rows `(1,1)` and `(2,2)`. In session A,
begin a transaction and run `SELECT * FROM t ORDER BY id`. In session B,
run `UPDATE t SET n=10` under autocommit. In session A, run
`UPDATE t SET n=n+1 WHERE id=1` and repeat the plain SELECT. It returns
`(1,11)` and `(2,2)`. After A commits, a new read sees `(1,11)` and `(2,10)`.

The read view also persists through savepoint rollback. In the indexed
case, a concurrent primary-key move can leave the older key visible
beside the transaction's own update at the new key. A reused unique
secondary key can have two visible rows in a range scan. A unique point
lookup returned the first clustered record in the audited case.

Four additional two-session schedules compared the initial read, UNION
as the first read, a write before the first read, and a READ COMMITTED
control. Both modes matched all 58 result lines. A further join with a
temporary table kept the same permanent-table view.

The [current point-write follow-up](2026-11-27-current-point-writes.md)
fixes an UPDATE targeting a key moved after the read view opened. The
indexed overlay regression reaches the moved row directly.

## Explicit consistent snapshots

`START TRANSACTION WITH CONSISTENT SNAPSHOT` pins the committed view at
the start of a REPEATABLE READ transaction. A writer that commits before
the reader's first query is still outside this view. Locking reads can
see the current row, and a write can overlay its own change without
replacing the pinned view. Other isolation levels return warning 138
and apply their ordinary read rules.

An explicit start can combine the snapshot request with `READ ONLY` or
`READ WRITE` in either order. The wire tracker includes the request in
its replay text. `COMMIT AND CHAIN` and `ROLLBACK AND CHAIN` retain the
effective isolation and access mode and drop the one-shot snapshot
request. Preparing a transaction start returns error 1295.

## Table definitions created after the read view

A repeatable-read transaction can establish its view by reading a separate
table. Another session can then change the target table before the first
session opens it. Pinned MySQL 8.4.11 returns error 1412 after adding or
dropping a column, rebuilding the primary key, or truncating and refilling
the target. Fsdb can return an old definition in paths that do not yet
check read-view visibility.

In the audited queries, MySQL returned 1412 for both a full scan and an
index seek that would have found no rows. `LIMIT 0` and a constant false
predicate avoided the scan and returned an empty result. A plain `INSERT`
could still succeed or return a duplicate-key error. Duplicate upserts
and `REPLACE` returned 1412 when they opened a row in the changed table.

Index creation has a narrower boundary. After adding `ix(p)` with
`ALGORITHM=INPLACE`, a covering scan and a seek on `p` returned 1412, while
a primary-key lookup remained available. Renaming a table, column, or
index, changing a column default or table comment, and removing an index
did not invalidate the audited MySQL reads. The renamed objects were
resolved using their current names. Fsdb can instead bind against its old
catalog and report a missing table, column, or index.

Renaming `ix` to `iy` and adding a new `ix` in the same ALTER left `iy`
usable from the old MySQL view, while forcing the new `ix` returned 1412.
Dropping and adding an identical index under its old name in one ALTER
left it usable. Changing a default with `ALGORITHM=INSTANT` preserved
both keys, whereas `ALGORITHM=COPY` and `OPTIMIZE TABLE` invalidated both.
After a 1412 error, another read of `anchor` still used the original view.
[MySQL index visibility check](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/storage/innobase/dict/dict0mem.cc#L604-L619)

SQL preparation and static `EXPLAIN` could resolve a new secondary index
while `EXECUTE` and `EXPLAIN ANALYZE` returned 1412 when they opened it.
After an instant column addition, static `EXPLAIN` of a constant primary
lookup already returned 1412. `EXPLAIN ANALYZE` with `LIMIT 0` succeeded in
both cases. `DESCRIBE` and `SHOW INDEX` still reported the old definitions
inside the transaction, even when statement binding resolved the new ones.

For example, create `anchor(id INT PRIMARY KEY)` and `t(id INT PRIMARY KEY,
p INT)`, with one row in each. In session A, begin a transaction and read
`anchor`. In session B, run `ALTER TABLE t ADD COLUMN extra INT,
ALGORITHM=INSTANT`. In session A, `SELECT p FROM t WHERE id=999` returns
1412 on MySQL, while `SELECT p FROM t WHERE FALSE` returns an empty set.

### Consistent SELECTs after a layout change

Fsdb now keeps a physical layout identity separate from its schema
revision. An ordinary repeatable-read SELECT binds the live table and
column names. A table renamed without a layout change still reads its
pinned rows. A statement that needs a replacement layout returns 1412
and leaves the original view available to later statements.

The pinned MySQL 8.4.11 comparison matched 93 cases in memory and with
WAL. It covered an instant column addition, a copy ALTER, and truncation.
The queries varied full scans, indexed misses, aggregates, `LIMIT 0` with
and without an offset, joins, scalar and `EXISTS` subqueries, and UNION.
A separate rename probe combined a newer row value with table and column
renames and verified the pinned value before rollback and the current
value afterward. Individual secondary-index changes and broader locking,
write, and prepared or EXPLAIN paths remain open.

An indexed integer contradiction skipped opening the replacement layout.
The same numeric comparison against a `VARCHAR` primary key still opened
the table and returned 1412. A missing outer key skipped the changed side
of an integer-to-text join, while the equivalent integer-to-integer join
returned 1412. A WAL-backed control preserved earlier writes, a savepoint,
and temporary-table shadowing across two 1412 errors and committed only the
writes preceding the savepoint.

When a secondary index is added in place, static `EXPLAIN` and actual
execution can choose different paths. In the audited `WHERE k=20` case,
both a covering projection and a projection including `payload` named
the new key in static `EXPLAIN`. The covering read returned 1412, while
`EXPLAIN ANALYZE` showed that the noncovering read used a clustered scan and
returned the row. The same distinction held for missing keys in the fixture.

### Secondary index lifetimes in consistent SELECTs

The read view now tracks index identities separately from names. Renaming
an inherited key preserves its pinned data. Reusing the old name for a
different key creates a separate identity. Supported single-source SELECT
plans check visibility when they select a B-tree for an equality probe,
range, ordered read, or forced covering scan. A qualifying clustered scan
can still return an old row, including when a newly created key exists.
After an insert by the reader, a clustered scan also returns the reader's
new row alongside the pinned rows. A lookup that opens the replacement
key still returns 1412.

The pinned MySQL comparison includes matching and missing keys, covering
and noncovering projections, forced and ignored keys, no-scan conditions,
and both a 24-row table and a 256-row table. Planning data for a new key
is built from pinned rows and reused for later reads of that definition.
Broader join and expression index opens, mutation, and prepared or
EXPLAIN paths remain open.

### Locking reads after in-place index creation

MySQL 8.4.11 binds hints against the current definition. After another
session changes a row and adds a B-tree key in place, a clustered locking
read can return the new value while a later consistent SELECT returns the
pinned value. A locking read that opens the new key returns 1412 before
acquiring row locks. This applies to UPDATE and SHARE locks, including
NOWAIT and SKIP LOCKED. READ COMMITTED can open the new key.

The comparison exercises equality hits and misses, range misses,
covering and noncovering scans, ORDER BY with and without LIMIT, and
empty predicates.

### Locking reads of new table layouts

Creating, recreating, or rebuilding a table can leave its current
definition visible to a repeatable-read transaction whose read view
cannot open that layout. MySQL 8.4.11 returns 1412 for locking reads
through either PRIMARY or a secondary key, even on a point miss. Table
and column binding still runs first. LIMIT 0, false predicates, and
contradictory index ranges can eliminate the read.

The comparison includes in-place key changes, instant column addition,
COPY, DROP and CREATE, and a rename that preserves the layout. Joined
and nested locking reads apply the same check. A metadata probe leaves
the actual subquery to acquire its locks or report a layout error.

### Direct updates and deletes

UPDATE and DELETE bind the current definition before planning row locks.
An update or delete that opens a layout created after its repeatable-read
view returns 1412. A forced probe through a new secondary index also
returns 1412, even if the target row is locked by another transaction.
Clustered writes can wait on that row and then modify its current version.
`LIMIT 0`, impossible ranges, and false predicates can eliminate the
write scan. Column binding and safe-update errors retain their precedence.

The comparison includes ordered UPDATE with LIMIT, DELETE with a single
source and an explicit target, and clustered writes using USE or IGNORE
hints.

### Joined updates, deletes, and writable views

Under REPEATABLE READ, joined UPDATE and DELETE bind current definitions
for the physical tables they open. The audited joins return 1412 after a
COPY rebuild, including primary-key misses, writes through mergeable
views, and statements that read a stored view as a join partner. A false
WHERE or a false ON condition on the right side of a LEFT JOIN can omit
the affected read. A forced probe through a secondary key added INPLACE
also returns 1412. READ COMMITTED can use the available table and key.

For the audited standalone view updates, the layout error takes precedence
over waiting for the rebuilt row. Missing columns and invalid index hints
retain their existing errors. An INVOKER view still reports a grant failure
first, with the same 1142 versus MySQL 1356 difference as the no-DDL control.
Multi-target UPDATE assignment ordering differs. The audited MySQL statements
process targets in written source order. Only changes to an immediate target
are visible to later targets. The optimizer can choose a different immediate
target when the query uses a different index.

### Plain joined read partners

Audited two-table UPDATE and DELETE joins on matching signed integer primary
keys acquire a shared lock on the matching read partner before applying the
write. An exclusive lock on the partner can delay a primary probe, a secondary
filter, or an `IGNORE INDEX` scan. A shared lock held by another transaction
allows the write to proceed. Disjoint primary probes and impossible joins
also proceed. Tests cover matching pending inserts, secondary-key changes,
and the committed row image after waiting under a pinned read view. Explicit
transactions retain matching partner locks until transaction end. Autocommit
releases them when the statement completes.

The audited scope uses one write target. Additional join shapes, full range
and gap boundaries, and multi-target write scheduling remain open.

### Duplicate-key writes

An ordinary INSERT that collides with a committed secondary unique key
can report 1062 or skip the row under IGNORE while another transaction
holds the clustered row for a payload update. A pending key move or
delete can change the duplicate result, so the insert waits for the
key. A duplicate on PRIMARY can still wait on the clustered row.

Upsert and REPLACE must open the conflicting row to change or remove
it. A committed conflict through a secondary key outside the read view
reports 1412 once the key is stable. A conflict through an inherited
PRIMARY can wait for the latest row and then proceed if the layout is
available. A new row can be inserted into a layout created after the
read view, while a later read of that layout still returns 1412.

### Insert and replace source queries

Under REPEATABLE READ, INSERT and REPLACE SELECT use current source
rows and wait for conflicting row writes. A selected secondary key or
layout outside the read view returns 1412 before a row wait. An ordinary
consistent SELECT in the transaction keeps its pinned rows. READ
COMMITTED source reads can copy committed rows while another transaction
holds an uncommitted update.

The clustered cursor protects visited primary gaps, including an empty
range or missing point. LIMIT 0 and an eliminated join source acquire
no locks on the skipped rows. The comparison includes joined and
scalar sources and checks the rows copied into a separate destination.

### Temporary-table source overlays

The overlay retains temporary names while refreshing the permanent
sources for an INSERT or REPLACE SELECT. A temporary source can shadow
a permanent table without contending for its locks. A permanent target
created after the snapshot can accept new rows, while a later consistent
SELECT of that target still returns 1412.

The schedules cover autocommit, read-only writes to a temporary target,
held permanent rows, newly created target layouts, and both READ
COMMITTED and REPEATABLE READ. A separate session checks that persistent
destinations remain empty after rollback of the transaction that wrote them.
