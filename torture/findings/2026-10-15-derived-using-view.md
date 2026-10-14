# Updatable derived-first views joined with USING

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` and updates the physical
component when a read-only derived source precedes it through `JOIN ...
USING(id)`:

```sql
CREATE TABLE a(id INT PRIMARY KEY,n INT);
CREATE TABLE b(id INT PRIMARY KEY,flag INT);
INSERT INTO a VALUES(1,4),(2,20);
INSERT INTO b VALUES(1,7),(3,9);
CREATE VIEW v AS
  SELECT a.id AS aid,a.n,d.id AS did
  FROM (SELECT id FROM b GROUP BY id) d JOIN a USING(id);
UPDATE v SET n=n+1 WHERE aid=1; -- one affected row
UPDATE v SET n=n+1 WHERE aid=2; -- zero affected rows
```

The same oracle accepts `SELECT id,n` from this join, so the merged `id`
column can filter an update to `n`. MySQL also reports an updatable view and
one affected row when two or three grouped derived sources are chained by
`USING(id)` before `a JOIN ... USING(id)`. FSDB previously returned
`IS_UPDATABLE=NO` and error 1288 for all these updates.

For one leading derived source, the inner-join reorder now retains the
`USING` key when it moves the writable table to the front. Equality is
symmetric, while the view's original source expressions continue to own its
projected and merged columns. For multiple leading derived sources, the
reorder is admitted only when every leading `USING` join merges exactly the
same key names as the target join. This keeps one logical merged key at each
step after the move; a chain with additional or different merged keys could
make an originally unambiguous left operand ambiguous and remains unsupported.

The focused regression checks metadata, qualified and merged projections,
one, two, and three leading derived sources, the physical update result, and
an unmatched target row. Broader mixed-key, `NATURAL`, lateral, and outer-join
write shapes are not established by this case.
