# Limited ordered writes after a contended row changes

MySQL 8.4.11 and Fsdb agree on an audited concurrent write shape. Two rows
start with `(id,n)=(1,0),(2,0)`. The first transaction changes row 1 to
`n=-1` without committing. A second transaction runs either:

```sql
UPDATE target SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1;
DELETE FROM target WHERE n>=0 ORDER BY id LIMIT 1;
```

The second writer waits on row 1. After the owner commits, row 1 no longer
matches, so the limited write selects row 2. The UPDATE leaves
`(1,-1),(2,1)`; the DELETE leaves `(1,-1)`. Both report one affected row.
This holds with only the primary key and with a secondary index on `n`.

The same re-selection occurs when row 1 still matches but moves behind row 2
in the sort order. Starting from `(1,0),(2,1)`, the owner changes row 1 to
`n=2`. A waiting `UPDATE ... SET n=n+10 WHERE n>=0 ORDER BY n,id LIMIT 1`
then changes row 2 to `n=11`; the analogous DELETE removes row 2.

The [disposable MySQL oracle](../scripts/limited-ordered-write-rebase-oracle.py)
asserts the wait, affected count, and final rows for all combinations of
write verb, predicate-versus-sort-key change, and index presence at both
`LIMIT 1` and `LIMIT 2`. For the latter, a third row fills the vacated result
slot: when the owner makes row 1 fail the predicate, the waiting write changes
rows 2 and 3; when row 1 moves behind both in sort order, it again changes
rows 2 and 3. A further eight cases delete the first candidate in the owner
transaction; the waiting UPDATE or DELETE fills the vacated result slot at
`LIMIT 1` or `LIMIT 2`, with and without a secondary predicate index. MySQL
8.4.11 and the matching `TransactionTests` cases agree on all 24 combinations.
No engine change was required for these row-change audits.

## Pending inserted candidates

The same oracle pins a different outcome when the owner inserts an earlier
primary-key candidate. Starting with `(1,0),(2,0)`, the owner inserts `(0,0)`
without committing. A second transaction runs
`UPDATE target SET n=n+1 WHERE n>=0 ORDER BY id LIMIT 1`. MySQL waits for
the insert, then changes row 0, leaving `(0,1),(1,0),(2,0)`. A focused fsdb
reproduction completed before the owner committed and changed row 1 instead,
leaving `(0,0),(1,1),(2,0)` after both commits. The ordered-write scheduler
now inspects active integer PRIMARY-key claims before selecting its rows. It
waits and rebases when a pending key crosses the `LIMIT` frontier, including
when fewer rows currently match than the limit. An insert after the frontier
does not delay the write. MySQL 8.4.11 and `TransactionTests` agree across 36
UPDATE/DELETE cases covering `LIMIT 1` and `LIMIT 2`, ASC and DESC, INT and
BIGINT UNSIGNED, and both wait and no-wait outcomes. This is scoped to
single-column integer PRIMARY ordering; other order expressions and key types
still need separate audits.
