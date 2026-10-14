# Updatable join views with multiple leading derived sources

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` and updates the physical
target when two read-only derived sources precede it in an inner-join view:

```sql
CREATE TABLE a(id INT PRIMARY KEY,n INT);
CREATE TABLE b(id INT PRIMARY KEY,flag INT);
CREATE TABLE c(id INT PRIMARY KEY,aux INT);
INSERT INTO a VALUES(1,0);
INSERT INTO b VALUES(1,7);
INSERT INTO c VALUES(1,9);
CREATE VIEW v AS
  SELECT a.id AS aid,a.n,d1.flag,d2.aux
  FROM (SELECT id,SUM(flag) AS flag FROM b GROUP BY id) d1
  JOIN (SELECT id,SUM(aux) AS aux FROM c GROUP BY id) d2 ON d2.id=d1.id
  JOIN a ON a.id=d1.id AND a.id=d2.id;
UPDATE v SET n=n+1 WHERE aid=1;
SELECT n FROM a; -- 1
```

The same result holds with pass-through derived sources instead of the two
aggregates. A further MySQL probe used `a.n<d2.aux` in the final join:
the update changed `n` from 4 to 5, while a later update after setting `n=20`
affected zero rows. The target join's predicate must therefore still filter
the write after the source order changes.

For a `SQL SECURITY INVOKER` version of the aggregate view, MySQL returned
1356 while the updater lacked `SELECT` on either derived source. Granting
access to only the first source still returned 1356; after granting both,
the update affected one row. The FSDB regression checks the same grant
sequence.

FSDB previously returned `IS_UPDATABLE=NO` for these views. Its write-target
classifier could move one leading derived source behind a physical target,
but not two. The metadata classifier had the same limit. Both now accept
multiple leading derived sources connected by plain inner or cross joins.
The update starts from the physical table, introduces the derived sources in
their original order, and applies the moved target-join predicate only after
its referenced sources are bound. Authorization uses the same reordered source
list. The focused regression checks metadata, pass-through and aggregate
sources, a predicate referring to the second derived source, a row that
becomes invisible to that predicate, and both derived-source read grants.

This does not establish support for arbitrary source reorderings, lateral
dependencies, outer joins, or moved `STRAIGHT_JOIN`, `NATURAL`, and `USING`
semantics.
