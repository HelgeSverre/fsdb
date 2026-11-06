# Set-operation CTEs beside a writable joined target

The pinned MySQL 8.4.11 container accepts a joined view whose read side
chains `EXCEPT` and `INTERSECT` through two CTEs:

```sql
CREATE VIEW v AS
WITH chosen AS (
    SELECT id,flag FROM b EXCEPT SELECT id,flag FROM c
), matched AS (
    SELECT id,flag FROM chosen INTERSECT SELECT id,flag FROM b
)
SELECT a.id,a.n,matched.flag
FROM matched JOIN a ON a.id=matched.id
WHERE a.n>=0 WITH CASCADED CHECK OPTION;
```

With `a = [(1,0),(2,0)]`, `b = [(1,7),(1,8),(2,9)]`, and
`c = [(1,8)]`, MySQL reports `IS_UPDATABLE=YES` and view rows
`(1,0,7)` and `(2,0,9)`. `UPDATE v SET n=3 WHERE id=1` changes one
physical row. Setting `n=-1` then fails with error 1369. Fsdb matches
these results in the focused `ViewTests` regression, including the unchanged
physical target after the rejected update.

This extends the audited read-source compositions. It does not prove every
set-operation nesting, duplicate multiplicity, or definer/security context.
