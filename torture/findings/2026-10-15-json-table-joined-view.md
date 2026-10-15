# Updating a physical table through a JSON_TABLE join view

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` for an inner-join view whose
physical target is joined with a read-only `JSON_TABLE` source. The table
function can follow the target, precede it, or depend on a preceding source:

```sql
CREATE TABLE a(id INT PRIMARY KEY,n INT);
INSERT INTO a VALUES(1,0),(2,0);

CREATE VIEW forward AS SELECT a.id,a.n,j.x FROM a
  JOIN JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j ON j.x=a.id;
CREATE VIEW reverse AS SELECT a.id,a.n,j.x FROM
  JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j
  JOIN a ON j.x=a.id;
CREATE VIEW dependent AS SELECT a.id,a.n,j.x FROM a
  JOIN JSON_TABLE(JSON_ARRAY(a.id),'$[*]' COLUMNS(x INT PATH '$')) j
    ON j.x=a.id;
CREATE VIEW chain AS SELECT a.id,a.n,j.x,k.y FROM
  JSON_TABLE('[1]','$[*]' COLUMNS(x INT PATH '$')) j
  JOIN JSON_TABLE(JSON_ARRAY(j.x),'$[*]' COLUMNS(y INT PATH '$')) k
    ON k.y=j.x
  JOIN a ON a.id=k.y;
```

Each view has `IS_UPDATABLE=YES`. On fresh rows, updating `n` through
`forward` for ID 1, `reverse` for ID 1, `dependent` for ID 2, and `chain`
for ID 1 changes only the corresponding physical `a` row. The reverse view
affects zero rows for ID 2 because its `JSON_TABLE` input contains only 1.
An attempt to update `j.x` returns 1288 (`HY000`), and an explicit-column
insert through `forward` returns 1471 (`HY000`).

FSDB formerly reported `IS_UPDATABLE=NO` and refused the first update with
1288. The regression now covers all four source orders and dependencies,
affected rows, final physical data, and the non-writable-source diagnostics.
The table function remains a read-only component; it does not make the joined
view insertable.
