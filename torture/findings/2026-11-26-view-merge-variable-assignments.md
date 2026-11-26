# View writes with user-variable assignments

MySQL 8.4.11 treats a view declared with `ALGORITHM=UNDEFINED` as
materialized for a write when the statement assigns a user variable.
The write can fail with 1288 for UPDATE or DELETE and 1471 for INSERT
or REPLACE. A view declared with `ALGORITHM=MERGE` can still merge.
A valid `MERGE(v)` hint also overrides the heuristic for the referenced
root view. Each nested view gets its own merge decision.
[MySQL merge resolution](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L3108-L3123)
[MySQL merge heuristic](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_lex.cc#L3719-L3729)

The [SQL fixture](2026-11-26-view-merge-variable-assignments.sql) produced
the same 26 output lines and 31 error lines on the pinned MySQL image
and in both Fsdb storage modes. It covers direct writes, joined writes,
EXPLAIN, and SQL PREPARE. The unit test also checks binary prepare.

- In the audited mixed view, the statement can update the merged physical
  partner. An UPDATE targeting the materialized component returns 1288
  before it changes the variable or the base row.
- Missing write sources retain 1146 precedence in the audited UPDATE and
  INSERT SELECT. Only a referenced mutation CTE contributes its variable
  assignment to the merge decision.
- `INFORMATION_SCHEMA.VIEWS.IS_UPDATABLE` remains `YES` for eligible
  definitions. The next ordinary write to the same view succeeds.

In a two-session probe, one transaction held a base-row lock while a
second submitted an UPDATE, single DELETE, and joined DELETE against
the materialized view. Each statement assigned a user variable. MySQL
returned 1288 while the lock was held. Fsdb previously let the lock
wait expire with 1205. Both Fsdb
storage modes now return 1288 while the lock remains held and leave the
session variable at zero.

General read-time merge planning, independently hinted occurrences of
the same stored view, and early lock rejection with a temporary-table
overlay remain open.
