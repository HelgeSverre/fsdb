# HAVING aggregate source columns in updatable joined views

MySQL 8.4.11 marks a joined view updatable when its read-only derived source
groups rows and filters with `HAVING MAX(z)>0`, even when the SELECT expression
also exposes `MAX(z) AS z`. Reading the view returns the grouped rows; updating
an exposed column of the joined physical table changes that table once.

```sql
CREATE TABLE grouped_source (k INT, z INT);
CREATE TABLE grouped_target (id INT PRIMARY KEY, k INT, val INT);
INSERT INTO grouped_source VALUES (1,10),(1,5),(2,20),(3,-10),(3,-20);
INSERT INTO grouped_target VALUES (1,1,5),(2,2,7);
CREATE VIEW grouped_having_view AS
  SELECT t.id,t.val,d.z
  FROM (SELECT k,MAX(z) AS z FROM grouped_source
        GROUP BY k HAVING MAX(z)>0) d
  JOIN grouped_target t ON t.k=d.k;
```

The pinned MySQL image reports `IS_UPDATABLE='YES'`, view rows `(1,5,10)`
and `(2,7,20)`, and one affected row for
`UPDATE grouped_having_view SET val=val+1 WHERE id=1`. The final physical
rows are `(1,1,6)` and `(2,2,7)` in `(id,k,val)` order. Fsdb previously
reported function error 1305 for the derived `MAX(z)` in HAVING and wrapped
it as invalid-view error 1356 when reading or updating the view.

Inside an aggregate argument, MySQL resolves a name that exists in the source
against that source before a same-named output alias. Outside an aggregate,
the output alias remains available: `HAVING ABS(z)>15` after `MIN(z) AS z`
keeps groups 2 and 3. If the argument names only an aggregate output alias,
`HAVING MAX(mz)>0` after `MAX(z) AS mz` is a nested aggregate and MySQL
returns 1111. Fsdb now follows these three cases. The focused Expecto
regression covers the view read, write, metadata, alias precedence, nested
aggregate error, and final base rows. Other view compositions remain covered
only by their own audited cases.
