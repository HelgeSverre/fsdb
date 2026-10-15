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

On the preview.18 head, FSDB accepts the view definition but returns error
1146, `Table 'fsdb.v' doesn't exist`, for the joined UPDATE. The joined
mutation executor resolves sources as physical tables and does not enter the
single-source `executeViewWrite` path. Target discovery likewise cannot claim
the view's physical row before the statement. This is an open view-write and
transaction-scheduling gap, rather than an optimistic-merge failure on this
specific shape. A fix needs both view-aware joined-source rewriting with
authorization and physical-target acquisition before execution; accepting
the view name without the latter would still diverge under contention.
