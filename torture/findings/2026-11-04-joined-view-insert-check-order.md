# Joined-view INSERT CHECK OPTION depends on source rows

The [native MySQL 8.4.11 oracle](../scripts/composed-join-view-oracle.py)
now pins this isolated keyless-source case:

```sql
CREATE TABLE t(id INT PRIMARY KEY,n INT);
CREATE TABLE s(id INT,flag INT);
CREATE VIEW v AS SELECT t.id,t.n,s.flag FROM t JOIN s ON s.id=t.id
  WHERE s.flag>t.n WITH CASCADED CHECK OPTION;
```

With source rows `(1,7),(1,8)` in that insertion order, MySQL accepts
`INSERT INTO v(id,n) VALUES(1,7)`. After clearing both tables and inserting
the source rows as `(1,8),(1,7)`, it rejects the same insert with error 1369.
Fsdb currently checks every joined row and rejects both orders. The native
oracle's existing broader insert case also rejects `n=7` when the 8,7 rows
coexist with another join key. A trial that checked only the final joined row
passed one focused fsdb case but was reverted after the broader native case
contradicted that rule. The actual MySQL decision needs a plan-aware audit
before changing fsdb's insert validation. UPDATE through these views retains
the separate all-original-partners check.
