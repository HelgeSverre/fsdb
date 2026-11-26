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

## Remaining view cases

A separate fixture created a permanent `base` with row `(1,10)`, a
two-layer writable view over it, and a session temporary `base` with
row `(1,91)`. Both engines handled a simple DELETE through the view
and a joined write targeting only the temporary alias correctly.
When the same joined statement also named the view as a target, Fsdb
returned 1146 for DELETE. A joined UPDATE targeting only the view
changed the temporary row where MySQL changed the permanent row.
A two-target UPDATE changed both rows in MySQL and only the temporary
row in Fsdb. Both Fsdb storage modes reproduced these outcomes.
Stored-view reads resolve through the permanent catalog. The joined
UPDATE rewrite loses that distinction when it lowers the target to a
table name, while the joined DELETE rewrite excludes a shadowed base
from its physical-target mapping.

Assignment to a user variable in the surrounding statement can make
MySQL materialize an UNDEFINED view. The audited joined DELETE then
returns 1288 instead of the join-view error. A directly defined MERGE
view retains the join-view error, while an outer MERGE view over an
UNDEFINED component can still return 1288. Fsdb's shape-based
updatability does not yet account for this merge decision.
[MySQL view and derived-table merge resolution](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L3108-L3123)
