# Error precedence for multi-table DELETE targets

MySQL 8.4.11 resolves target names against source aliases before binding
source bodies. It then opens the sources and checks whether each named
target can be deleted, in source order. An unknown target name reports
1109 even beside an invalid CTE. A missing table, invalid stored source,
or invalid derived projection reports its source error before a known
invalid target is rejected.

An inner join view, including a layer over one, returns 1395 as a delete
target. Outer join views, TEMPTABLE views, CTEs, and derived targets return
1288. These errors precede errors in the outer `ON`, `USING`, and WHERE
clauses. An eligible view still reports 1054 for an unknown join column.
Two invalid targets use source order even if the DELETE target list names
them in the opposite order.

Fsdb now binds the delete targets ahead of row matching, using the
source-metadata path on rejection. The probe preserves source-local
errors and keeps session and custom function calls inert. Direct SQL,
`EXPLAIN`, and `PREPARE` use the same target checks. A missing index
on an aliased physical table now reports the alias with SQLSTATE 42000.
Malformed JSON input to `JSON_TABLE` reports SQLSTATE 22032.

The [SQL fixture](2026-11-26-joined-delete-binding.sql) uses the
[digest-pinned MySQL 8.4.11 image](../config/toolchain.json). It checks
diagnostic priority, advisory locks in the outer condition and in a
derived projection, and a valid delete of two rows through a writable
view and a physical target. Both Fsdb storage modes match the 27 error
responses, including SQLSTATE and message, and all nine result rows.
Both base tables retain their rows after every rejection. The successful
delete leaves only ID 1 in each table and in the writable view after
reopening the durable Fsdb store.

## Temporary shadow follow-up

The [shadow fixture](2026-11-26-joined-delete-shadow.sql) gives the
permanent base of a two-layer writable view and a same-named session
temporary table overlapping primary keys. Fsdb previously returned
1146 for the audited joined DELETE through the view. Both Fsdb storage
modes now match MySQL 8.4.11 on all 37 output rows and the trigger's
1644 error. Foreign-key cascades reach the permanent child even when
a same-named temporary child supplies the join. A trigger rejection
restores both targets, and a temporary-only delete skips the permanent
trigger.

In a two-session check, a writer made an eligible row invisible to
the view while a second session attempted the joined DELETE. Both
engines waited and returned zero affected rows after the writer
committed. A separate mixed DELETE removed two rows and explicit
rollback restored both. The WAL-backed Fsdb server recovered the
expected permanent base and child rows after an abrupt stop. The
mixed path still claims permanent rows conservatively before binding
the join against the temporary catalog.

## Remaining view cases

The [joined UPDATE follow-up](2026-11-26-joined-update-shadow.md) covers
permanent view bindings and simultaneous updates to the same-named
temporary table.

`DROP TEMPORARY TABLE base` can still return 3730 when a permanent
foreign key references the same-named permanent base. MySQL drops the
temporary table and retains both permanent tables.

Assignment to a user variable in the surrounding statement can make
MySQL materialize an UNDEFINED view. The audited joined DELETE then
returns 1288 instead of the join-view error. A directly defined MERGE
view retains the join-view error, while an outer MERGE view over an
UNDEFINED component can still return 1288. Fsdb's shape-based
updatability does not yet account for this merge decision.
[MySQL view and derived-table merge resolution](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L3108-L3123)
