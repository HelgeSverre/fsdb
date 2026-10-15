# Outer-join view updateability on MySQL 8.4.11

A native, disposable MySQL 8.4.11 server reports an inner-join view as
updatable, but reports these outer-join views as non-updatable:

```sql
CREATE TABLE a(id INT PRIMARY KEY,n INT);
CREATE TABLE b(id INT PRIMARY KEY,flag INT);
CREATE VIEW v_inner AS
  SELECT a.id,a.n,b.flag FROM a JOIN b ON b.id=a.id;
CREATE VIEW v_left AS
  SELECT a.id,a.n,b.flag FROM a LEFT JOIN b ON b.id=a.id;
CREATE VIEW v_right AS
  SELECT a.id,a.n,b.flag FROM b RIGHT JOIN a ON b.id=a.id;
CREATE VIEW v_left_only AS
  SELECT a.id,a.n FROM a LEFT JOIN b ON b.id=a.id;
CREATE VIEW v_derived_left AS
  SELECT a.id,a.n,d.id AS did
  FROM a LEFT JOIN (SELECT id FROM b GROUP BY id) d ON d.id=a.id;

SELECT TABLE_NAME, IS_UPDATABLE
FROM information_schema.VIEWS
WHERE TABLE_SCHEMA = 'fsdb_view_probe'
ORDER BY TABLE_NAME;
```

| View | `IS_UPDATABLE` |
|---|---|
| `v_inner` | `YES` |
| `v_left` | `NO` |
| `v_right` | `NO` |
| `v_left_only` | `NO` |
| `v_derived_left` | `NO` |

`UPDATE v_left_only SET n=n+1 WHERE id=2` returns error 1288 (`HY000`):
`The target table v_left_only of the UPDATE is not updatable`. Omitting the
right-hand columns from the projection does not make the outer join writable.
FSDB already reports `NO` and error 1288 for the direct left-join case in
`ViewTests.fs`. This oracle rules out outer-join view UPDATE as a parity target
for these shapes; it does not describe direct multi-table UPDATE semantics.
