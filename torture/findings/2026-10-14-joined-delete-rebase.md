# Joined DELETE waits for an updated target row

MySQL 8.4.11 was the oracle. One transaction updated `t(1, 0)` and retained
its row lock. A second transaction ran:

```sql
DELETE t FROM t JOIN lookup_t l ON l.id=t.id WHERE t.id=1;
```

The DELETE waited for the first commit, removed one row, and committed. The
final table was empty. The equivalent `DELETE FROM t USING t JOIN lookup_t ...`
in autocommit mode also waited, then removed the row. Before the fix, FSDB's
explicit-transaction DELETE returned one affected row but its commit failed
with error 1205, leaving the row present.

Write-target discovery now claims first-table rows when a joined DELETE names
that table as its sole target. The existing row wait and snapshot rebase path
handles the concurrent update. A regression covers the explicit transaction
and the autocommit `USING` form. Deletes naming multiple targets or a joined
table other than the first remain outside this targeted claim path.
