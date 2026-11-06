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
set-operation nesting, duplicate arrangement, or definer/security context.

## Multiset follow-up

The pinned MySQL 8.4.11 oracle also accepts the same joined-view structure
with `EXCEPT ALL` feeding `INTERSECT ALL`. Starting with three `(1,7)` rows
in the first source, one `(1,7)` exclusion, and the original first source
as the intersection's right side, the view retains two `(1,7)` rows. A
separate `(1,8)` and `(2,9)` row remain. MySQL reports `IS_UPDATABLE=YES`;
`UPDATE v SET n=n+1 WHERE id=1` affects one physical target row despite
three matching view rows. Fsdb matches these rows, affected-row count, and
an atomic cascaded CHECK OPTION rejection in a focused regression.

This verifies duplicate multiplicity through two set operators in a read
source beside one writable target. Other set-operation orderings, nested
security contexts, and concurrent writes remain separate questions.
