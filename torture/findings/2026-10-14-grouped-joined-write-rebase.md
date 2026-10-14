# Writes targeting a parenthesized join group

Native MySQL 8.4.11 was the oracle. With `a(1)`, `b(1,0)`, and `c(1)`, an
open transaction changed `b.n` from 0 to 1. A second transaction waited
when it ran each of these statements against the row inside the join group:

```sql
UPDATE a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id
SET b.n=b.n+1 WHERE a.id=1;

UPDATE a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id
SET n=n+1 WHERE a.id=1;

DELETE b FROM a JOIN (b JOIN c ON b.id=c.id) ON a.id=b.id
WHERE a.id=1;
```

In the unqualified `SET` case, `a` has `flag` but no `n`, so `b` uniquely
owns the assignment. After the first transaction committed, each waiting
statement affected one row and committed. The UPDATE cases left `b.n=2`;
the DELETE case removed `b`.

Fsdb previously let the grouped UPDATE proceed without a row claim, then
failed its later COMMIT with 1205. Transaction target discovery now visits
physical leaves inside join groups for named targets and for unqualified
assignment ownership. The focused regression covers the wait, rebase,
affected-row count, commit, and final row state for all three forms.
