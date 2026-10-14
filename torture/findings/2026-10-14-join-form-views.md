# Writable CROSS, USING, and NATURAL join views

MySQL 8.4.11 reports `IS_UPDATABLE = YES` and accepts an update to a direct
base-table column through each of these views:

```sql
SELECT a.id, a.n, b.v FROM a CROSS JOIN b WHERE a.id = b.id;
SELECT a.id, a.n, b.v FROM a JOIN b USING (id);
SELECT id, n, v FROM a NATURAL JOIN b;
```

The native probe used `a(id PRIMARY KEY, n)` and `b(id PRIMARY KEY, v)` with
matching IDs 1 and 2. `UPDATE view_name SET n = 11 WHERE id = 1` succeeded for
each shape. FSDB previously reported the cross and natural forms as
non-updatable; after allowing their join kinds it still rejected the natural
form with error 1052 because the merged `id` was treated as ambiguous.

The view classifier and `information_schema.views` metadata now recognize
these inner-style join forms. View binding maps bare merged `USING` and
`NATURAL` columns to the left input; ordinary repeated columns in `ON` joins
remain ambiguous. `ViewTests` checks metadata, writes, and the final base row.
