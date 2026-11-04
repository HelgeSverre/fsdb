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

## Source-buffer follow-up

The native oracle now varies two and three keyless source rows. The result
tracks the final inserted flag in every tested permutation: ending in 8
accepts `n=7`, and ending in 6 or 7 rejects it. Adding an `(id,flag)` index
and defining the view with `FORCE INDEX` does not change these outcomes.
Reading or updating one source flag immediately before the view INSERT does
not change them either.

MySQL 8.4.11's [INSERT path](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_insert.cc)
calls `view_check_option()` after filling the target record. That
[method](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/table.cc)
evaluates the prepared condition directly; it does not iterate joined source
rows. In the isolated probe, `Handler_read%` session counters show no reads
at all during the view INSERT. Together with the row-order matrix, this
supports an inference that the expression observes a source table record
buffer rather than a freshly selected relational partner. The precise buffer
lifetime is not established by this probe.

Fsdb's all-partner CHECK OPTION validation remains the deliberate rule for
this case. Reproducing the observed MySQL outcome would require emulating
incidental buffer state and would make acceptance depend on prior source-row
activity. The divergence is retained as an explicit compatibility limit;
the broader joined-view INSERT paths still need their own MySQL audits.
