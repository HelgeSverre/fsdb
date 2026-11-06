# Dependent window and DISTINCT CTEs in a writable joined view

MySQL 8.4.11 accepts this view as updatable, changes the physical target
once, enforces its cascaded CHECK OPTION, and refuses INSERT and DELETE
with joined-view errors 1471 and 1395:

```sql
CREATE VIEW chained AS
WITH ranked AS (
  SELECT id, ROW_NUMBER() OVER (PARTITION BY id ORDER BY flag) AS flag
  FROM source_rows
), filtered AS (
  SELECT DISTINCT id, flag FROM ranked
)
SELECT target_rows.id, target_rows.n, filtered.flag
FROM filtered JOIN target_rows ON target_rows.id = filtered.id
WHERE target_rows.n >= 0
WITH CASCADED CHECK OPTION;
```

The earlier fsdb classifier could inline independent CTEs and wrap
recursive CTEs, but it left this dependent chain attached to the outer
SELECT. Its writable-shape check then treated the remaining CTEs as a
non-updatable view and rejected creation with 1368.

The metadata rewrite now recognizes references from a CTE to an earlier
CTE. It infers each referenced CTE's output columns in declaration order
and wraps the outer CTE source as a derived read source with the complete
CTE binding scope. The physical table remains the writable component.

The native composed-view oracle passes 38 shapes, including this chain,
an explicit-column variant that renames both CTE outputs, and a
`SELECT DISTINCT *` variant. The focused fsdb regression runs all three
and checks metadata, window results, one target update, atomic CHECK
OPTION rejection, and INSERT/DELETE refusal. All three pass; the
105-view and 67-CTE test groups passed after the production fix. More
dependent CTE arrangements and security contexts remain open.
