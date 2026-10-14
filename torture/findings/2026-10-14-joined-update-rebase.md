# Joined UPDATE waits and rebases its target row

MySQL 8.4.11 was the oracle for two sessions updating the same row through a
join. With `t(1, 0)` and `lookup_t(1)`, both sessions ran:

```sql
BEGIN;
UPDATE t JOIN lookup_t l ON l.id=t.id
SET t.n=t.n+1 WHERE t.id=1;
COMMIT;
```

The second UPDATE waited for the first transaction to commit. Both commits
succeeded, and the final value was `n=2`. Before this change, FSDB also waited,
but the second commit failed with error 1205 and left `n=1`. The same failure
occurred when the waiting joined UPDATE ran in autocommit mode.

The transaction write-target discovery now claims first-table rows for joined
UPDATE statements whose assignments all explicitly name that table. The
existing row wait and snapshot rebase path then lets the waiting statement
evaluate `n+1` against the committed value. An Expecto regression covers both
explicit transactions and a waiting autocommit statement.

Joined updates that assign to multiple tables, unqualified assignments, and
other transaction write shapes remain outside this targeted claim path and need
separate MySQL comparisons. The analogous single-target joined DELETE case is
covered [separately](2026-10-14-joined-delete-rebase.md).
