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

The audit also covers `LIMIT 2` over three rows. The owner can make row 1
stop matching, move it between rows 2 and 3, or move it behind both. After
the owner commits, the waiting writer reselects two matching rows in the
current order. A further four-row case has two independent owners change
the first two candidates to negative values. The limited writer waits for
both owners to commit, then updates or deletes rows 3 and 4. MySQL and Fsdb
agree with and without a secondary index on `n`.

The [disposable MySQL oracle](../scripts/limited-ordered-write-rebase-oracle.py)
asserts the waits, affected counts, and final rows for 28 combinations of
write verb, limit, row-change scenario, and secondary-index presence. The
matching `TransactionTests` cases verify Fsdb's waits and re-selection.
No engine change was required. This closes these scheduling audits; it does
not measure write throughput or establish parity for other limited mutation
shapes.
