# Multi-target joined writes claim all target tables

Native MySQL 8.4.11 was the oracle. With `a(1,0)` and `b(1,0)`, one transaction
ran `UPDATE a SET n=n+1 WHERE id=1` while another ran:

```sql
UPDATE a JOIN b ON b.id=a.id
SET a.n=a.n+1,b.n=b.n+1 WHERE a.id=1;
```

The second statement waited for the first commit, updated two rows, committed,
and left `a.n=2`, `b.n=1`. Before this change, FSDB returned two affected rows
but failed the second commit with error 1205. MySQL also waited and committed
when a transaction updated `b` while a joined `DELETE a,b FROM a JOIN b ...`
targeted both tables; both tables ended empty. A `DELETE b FROM a JOIN b ...`
waited on `b` and removed only that row.

Transaction target discovery now returns every named physical target. Claims
for aliases of the same table are combined, and table groups are acquired in
name order before the transaction snapshot is rebased once. Expecto regressions
cover two-target UPDATE, two-target DELETE, and DELETE targeting only the
joined table. A joined UPDATE whose write target is physical and whose lookup
source is derived also waits and rebases, matching the MySQL probe. Joined
UPDATE with unqualified assignments, targets inside nested join groups, and
other write shapes remain outside this claim path.
