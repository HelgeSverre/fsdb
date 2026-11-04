# Writable joined view after a UNION ALL CTE

MySQL 8.4.11 allows a read-only `UNION ALL` CTE to precede a writable
physical table in a joined view. The CTE can yield multiple rows for one
target. UPDATE changes that target once, and a cascaded CHECK OPTION using
the CTE column rejects a write when any joined row fails the check. The
[composed-view oracle](../scripts/composed-join-view-oracle.py) verifies both
orders of duplicate source rows.

Fsdb previously rejected the view at creation with error 1368. Its writable
view resolver treated only plain SELECT CTE bodies as read-only derived
sources. It now inlines independent UNION bodies without a trailing ORDER BY,
LIMIT, or OFFSET. Explicit CTE column names are applied to the first UNION
branch, which defines the derived output names. The focused view regressions
verify metadata, one physical update, duplicate rows, and atomic CHECK OPTION
rejection.

This covers independent UNION bodies in this joined-view shape. Ordered or
limited UNION bodies, dependent branches, nested CTEs, and broader security
contexts remain to audit.

```sh
uv run --with pymysql python torture/scripts/composed-join-view-oracle.py
```
