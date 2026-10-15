# Intersecting correlated physical IN and range candidates

Native MySQL 8.4.11 returns a count of two for each of the direct, pass-through
derived, and stable CTE forms of this correlated subquery. The inner table has
1,000 rows and indexes on `id` and `a`. Rows 1–500 have `a=1`; the remaining
rows have distinct other values. The outer row supplies `(a1,a2,lo,hi) =
(1,999,499,1000)`.

```sql
SELECT (SELECT COUNT(*) FROM probe_rows d
        WHERE d.a IN(o.a1,o.a2)
          AND d.id BETWEEN o.lo AND o.hi)
FROM probe_bounds o;
```

The `IN` candidate has 500 rows and the range candidate has 502, but only IDs
499 and 500 satisfy both. FSDB already returned the correct count. Its planner
considered a broad range too expensive in isolation and chose one access path;
with a leading `TOUCH(d.id)=d.id` residual, the focused regression observed
1,002 calls before the change.

The physical correlated planner now intersects compatible membership and range
candidates by stored row identity before projecting or evaluating residuals.
It may consider a broad range when a membership predicate can narrow the joint
result. The same focused test covers direct, pass-through derived, and stable
CTE sources and requires fewer than 20 residual calls for each. The existing
membership probe still enforces index hints, exact key compatibility, and
effective text-collation safety; the range probe also respects source index
hints. An `IGNORE INDEX (PRIMARY, ix_a)` regression preserves the count and
confirms that neither ignored key narrows the scan. The new intersection uses
the existing membership probe rather than adding a separate interpretation
of `IN`.

This is a bounded access-path improvement. It does not add a general
cost-based join planner or change MySQL's query plan output.
