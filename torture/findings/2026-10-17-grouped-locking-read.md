# Grouped join operands in locking reads

MySQL 8.4.11, using the digest-pinned image in `torture/config/toolchain.json`,
locks every physical source of this grouped join until transaction end:

```sql
START TRANSACTION;
SELECT b.id FROM a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id FOR UPDATE;
```

With one row in each table, a second transaction's `SELECT id FROM a`, `b`,
or `c` `FOR UPDATE NOWAIT` each returns 3572. Adding `OF b` to the grouped
query locks only `b`; the second transaction can lock `a` and `c`.

Fsdb previously collected only the top-level `FROM` and `JOIN` operands for
row-lock acquisition. The grouped operand was not a direct table reference,
so `FOR UPDATE` left `b` and `c` unlocked, and `FOR UPDATE OF b` returned 3568.
The locking-read source collection now visits physical leaves of grouped
operands. Its existing row-lock acquisition and `OF` selection then apply to
the same physical sources used by query execution. The focused regression
checks both the default and `OF b` cases with two sessions and `NOWAIT`.

Join-shaped locking reads still conservatively lock all rows of each selected
physical source; this change does not add MySQL's access-path or gap locks.
