# Joined-view `INSERT SET` and nested CHECK OPTION

Native MySQL 8.4.11 was run in a disposable local server. For a direct inner
join view over `a(id, n)` and `b(id, a_id)`, with `aid` projecting `a.id`, MySQL
accepted `INSERT INTO joined SET aid=1,n=10` with one affected row. A repeated
`INSERT IGNORE ... SET` affected zero rows, and `INSERT ... SET ... ON DUPLICATE
KEY UPDATE n=VALUES(n)` affected two rows. Inserting without a column list or
`SET` assignments returned error 1394. FSDB's focused regression matches
these results and the final physical row.

The second probe used a filtered component view owned by `filter_owner` and an
outer joined view owned by `join_owner`:

```sql
CREATE DEFINER='filter_owner'@'%' VIEW filtered AS
  SELECT id,n FROM a WHERE n>0 WITH CASCADED CHECK OPTION;
CREATE DEFINER='join_owner'@'%' VIEW joined AS
  SELECT f.id AS aid,f.n,b.id AS bid
  FROM filtered f JOIN b ON b.a_id=f.id;
```

`filter_owner` had SELECT, INSERT, and UPDATE on `a`; `join_owner` had those
grants on `filtered`, SELECT/INSERT/UPDATE on `b`, and CREATE VIEW on the
schema. With `(1,10)` in `a` and `(1,1)` in `b`, MySQL returned:

| Statement | MySQL result |
|---|---|
| `UPDATE joined SET n=-1 WHERE aid=1` | error 1369 |
| `INSERT INTO joined SET aid=2,n=-1` | error 1369 |
| `INSERT INTO joined SET aid=2,n=20` | one affected row |

The final `a` rows were `(1,10)` and `(2,20)`. FSDB matches these outcomes in
`ViewTests.fs`. A first probe without INSERT/UPDATE on `b` returned 1356 for
joined-view INSERT, so the grant-complete setup is necessary to isolate the
component CHECK OPTION.

This audit confirms existing behavior; it did not require an engine change.
