# Joined UPDATE through a temporary shadow

The [SQL fixture](2026-11-26-joined-update-shadow.sql) gives a nested
writable view's permanent base and a same-named temporary table overlapping
primary keys and different column types. The earlier Fsdb implementation
could bind the view's write to the temporary table or skip the row after
testing the view predicate against that table.

Both Fsdb storage modes match pinned MySQL 8.4.11 on 37 output rows and
three errors, including each error's SQLSTATE and message. The cases cover
simultaneous writes to two views and a temporary table, trigger and CHECK
OPTION rejection, a duplicate key on the temporary side, and explicit
transaction rollback. An indexed ngram phrase uses the permanent document
index. Correlated subqueries in the view's JOIN and WHERE clauses read the
permanent table. The same expression written by the caller sees the
temporary table.

A concurrent writer hid a base row from the view while the joined UPDATE
waited. Both engines then affected zero rows and retained the temporary
row unchanged at REPEATABLE READ and READ COMMITTED. WAL recovery after
an abrupt stop reproduced the expected permanent rows and view results.
Referencing a temporary table twice in a join can still succeed in Fsdb
where MySQL returns 1137.

## Primary-key locking follow-up

Both Fsdb storage modes previously waited for a writer on a different
primary key. With a complete primary-key equality, the planner now claims
the matching permanent row and key. It uses the temporary source's actual
column types to propagate bounds across inner joins. It handles both
direct view-column bounds and bounds propagated from the temporary source.

MySQL 8.4.11 and both Fsdb modes completed the disjoint UPDATE and DELETE
while another connection held the other composite key. The schedules
cover signed keys, a BIGINT UNSIGNED leading key above the signed range,
and an UPDATE through a view that contains its own join. A DELETE aimed
at a key absent from the snapshot still waits for a pending move into
that key and deletes the row after the move commits. Missing keys retain
broader row claims, and incomplete or unsupported bounds fall back to
the existing scan-shaped claims.

## Standalone UPDATE follow-up

The [standalone fixture](2026-11-26-standalone-view-correlations.sql)
compares MySQL 8.4.11 with both Fsdb storage modes. All 20 result rows
and the CHECK OPTION rejection match, including its code, SQLSTATE, and
message. The update binds qualified outer view columns inside caller
subqueries without capturing names declared by those subqueries.
Caller subqueries can read a temporary table while the view reads and
writes the permanent catalog. A prepared execution and explicit rollback
exercise the same path.

In a two-session check the update waited for the permanent row and
affected zero rows after the writer committed a value that stopped
matching the caller's WHERE clause. The temporary row stayed unchanged.
The committed data matched after replaying WAL following an abrupt stop.

### Exposed columns in caller subqueries

The [projection fixture](2026-11-26-view-subquery-columns.sql) matches
MySQL 8.4.11 on 24 result rows and the expected 1054 error in both Fsdb
storage modes. Caller subqueries resolve local columns first and then
look for exposed view columns, including renamed and computed columns.
A base column omitted by the view remains out of scope. The fixture also
covers nested scalar subqueries, IN and ANY comparisons, a view with its
own join, and a prepared update while a differently shaped temporary table
shadows the base. Preparation describes the stored view against the
permanent catalog. Subquery memoization checks local column names as well
as aliases, so a qualified fallback to an enclosing row remains correlated.

The earlier qualified fixture still matches, including the lock-wait
schedule. WAL replay after an abrupt stop recovered matching committed
rows from both fixtures.

### Caller subqueries in joined UPDATE

The [joined fixture](2026-11-26-joined-view-caller-subqueries.sql) matches
MySQL 8.4.11 on 25 result rows and two errors in both Fsdb storage modes.
Caller subqueries in SET, WHERE, and JOIN ON see the exposed columns of
writable views along with their other joined sources. The fixture covers
multiple writable views, a temporary source, hidden base columns,
ambiguous output names, and merged USING and NATURAL keys. The merged
key cases include a writable view on the right of the join.

Prepared execution and explicit rollback match in the fixture. A separate
two-session check waited for the permanent writer, then affected zero rows
after the committed value stopped matching. WAL replay after an abrupt
stop recovered matching permanent rows. Text PREPARE accepts the multiline
joined statement, and EXECUTE accepts its arguments on separate lines.
A separate regression checks that a newline in a nested string literal
survives preparation and execution.
