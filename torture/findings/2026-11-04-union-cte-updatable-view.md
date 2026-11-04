# Writable joined view after a UNION ALL CTE

MySQL 8.4.11 allows a read-only `UNION ALL` CTE to precede a writable
physical table in a joined view. The CTE can yield multiple rows for one
target. UPDATE changes that target once, and a cascaded CHECK OPTION using
the CTE column rejects a write when any joined row fails the check. The
[composed-view oracle](../scripts/composed-join-view-oracle.py) verifies both
orders of duplicate source rows.

Fsdb previously rejected the view at creation with error 1368. Its writable
view resolver treated only plain SELECT CTE bodies as read-only derived
sources. It now inlines independent UNION bodies, including trailing ORDER BY,
LIMIT, and OFFSET. Explicit CTE column names are applied in an outer
projection after the UNION has sorted and limited its rows. Renaming the
first branch instead made CHECK OPTION examine the wrong two rows: with
flags 7, 8, and 9, MySQL accepts `n=7` and rejects `n=8` when the CTE keeps
the highest two flags. The native oracle and fsdb regression pin both
outcomes. The other focused cases verify metadata, one physical update,
duplicate rows, and atomic CHECK OPTION rejection.

This also covers duplicate first-branch names when an explicit CTE column
list supplies distinct output names and the UNION has no internal ORDER BY.
MySQL accepts a similarly named view with `ORDER BY 2`, reports it writable,
then rejects UPDATE with ambiguous-column error 1052; the native oracle pins
that boundary and verifies the physical row remains unchanged. Fsdb now
preserves the explicit CTE output names while resolving writable metadata,
then diagnoses the duplicate original name at UPDATE time before modifying
the target. Both direct and joined UPDATE forms return 1052 in the native
oracle and focused regression; the target row remains unchanged.
Dependent branches, nested CTEs, and broader security contexts remain to
audit.

```sh
uv run --with pymysql python torture/scripts/composed-join-view-oracle.py
```
