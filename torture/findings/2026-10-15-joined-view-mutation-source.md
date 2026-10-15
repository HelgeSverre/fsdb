# Joined UPDATE addressed to an updatable view

MySQL 8.4.11 accepts a joined UPDATE whose first source is an updatable
single-table view:

```sql
CREATE TABLE t(id INT PRIMARY KEY, n INT);
CREATE TABLE l(id INT PRIMARY KEY);
CREATE VIEW v AS SELECT id, n FROM t;
UPDATE v JOIN l ON l.id=v.id SET v.n=v.n+1 WHERE v.id=1;
```

With matching rows, the statement affects one row and changes `t.n` from 0
to 1. The [disposable-server oracle](../scripts/joined-view-mutation-source-oracle.py)
also holds `t.id=1` in one explicit transaction while another starts the
joined view UPDATE. The second statement remains blocked for at least 200 ms,
then affects one row after the owner commits; both commits succeed and the
final base value is `n=2`.

The same oracle confirms that a joined UPDATE through a view with renamed
columns writes the mapped base column, and that a joined UPDATE violating a
view's `CASCADED CHECK OPTION` returns 1369 without changing the base row.

On the preview.18 head, FSDB accepts the view definition but returns error
1146, `Table 'fsdb.v' doesn't exist`, for the joined UPDATE. The joined
mutation executor resolves sources as physical tables and does not enter the
single-source `executeViewWrite` path. Target discovery likewise cannot claim
the view's physical row before the statement. This is an open view-write and
transaction-scheduling gap, rather than an optimistic-merge failure on this
specific shape. A fix needs both view-aware joined-source rewriting with
authorization and physical-target acquisition before execution; accepting
the view name without the latter would still diverge under contention.

The subsequent single-table-view fix rewrites the view source and its exposed
columns before the joined mutation, retains the view write authorization and
check scope, and claims the physical base rows before execution. The Fsdb
regression covers the blocked writer, the rebased value, renamed columns, and
CHECK OPTION, ambiguous bare columns, and caller permissions on the external
join. The rewrite currently applies to assignments explicitly qualified with
the first-source view name or alias. Joined views used as a source of another
joined mutation and bare view-target assignments remain outside this fix.
