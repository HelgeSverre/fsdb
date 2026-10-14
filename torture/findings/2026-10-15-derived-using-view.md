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
reorder now resolves each `USING` key against the original join's logical left
columns and converts it to a qualified equality before moving the target.
This also covers different keys in one chain, where retaining `USING` after
the move would give a key to the wrong left operand.

MySQL 8.4.11 reports `IS_UPDATABLE=YES` and updates `a` in both of these
mixed-key views:

```sql
CREATE TABLE a(id INT PRIMARY KEY,x INT,n INT);
CREATE TABLE b(id INT PRIMARY KEY,x INT);
CREATE TABLE c(x INT PRIMARY KEY);
INSERT INTO a VALUES(1,999,100);
INSERT INTO b VALUES(1,10);
INSERT INTO c VALUES(10);
CREATE VIEW v1 AS SELECT a.id AS aid,a.n,d1.x AS bx
  FROM (SELECT id,x FROM b) d1 JOIN (SELECT x FROM c) d2 USING(x)
  JOIN a USING(id);
CREATE VIEW v2 AS SELECT a.id AS aid,a.n,d2.x AS bx
  FROM (SELECT x FROM c) d1 JOIN (SELECT id,x FROM b) d2 USING(x)
  JOIN a USING(id);
UPDATE v1 SET n=101 WHERE aid=1;
UPDATE v2 SET n=102 WHERE aid=1;
```

`a.x` deliberately differs from the derived `x`: the `USING(x)` match
belongs to the two derived sources. In `v2`, the target's `id` key belongs
only to the second derived source. Both writes reach `a` on MySQL and FSDB.

The focused regression checks metadata, qualified and merged projections,
one, two, and three leading derived sources, mixed-key ownership, the physical
update result, and an unmatched target row. Broader `NATURAL`, lateral, and
outer-join write shapes are not established by this case.
