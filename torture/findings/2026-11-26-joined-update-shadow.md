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
In a separate schedule, both Fsdb storage modes waited for a writer on a
different primary key where MySQL completed the update without waiting.

Standalone UPDATE through a view can still raise 1054 for a qualified
caller reference inside a correlated subquery. Referencing a temporary
table twice in a join can also succeed in Fsdb where MySQL returns 1137.
