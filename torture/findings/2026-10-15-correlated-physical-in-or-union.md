# Correlated physical IN probes inside complete OR branches

Native MySQL 8.4.11 returns four rows for a correlated count over 1,000 inner
rows. The outer row supplies `(a1,a2,b1,b2) = (1,2,1500,1501)`; the inner table
has maintained indexes on `a` and `b`, with `a=id` and `b=id+1000`.

```sql
SELECT (SELECT COUNT(*) FROM probe_rows d
        WHERE d.a IN(o.a1,o.a2) OR d.b IN(o.b1,o.b2))
FROM probe_bounds o;
```

FSDB already returned four, but the correlated physical OR candidate builder
omitted `IN` probes. A leading `TOUCH(d.id)=d.id` residual ran 2,002 times in
the direct-table fixture before the change. The builder now uses the existing
index-hint- and collation-aware correlated membership probe in each branch,
intersects it with any other safe branch candidates, and unions the complete
branch row identities before projection and residual evaluation. The focused
regression keeps residual calls below 20 for direct, pass-through derived,
and stable CTE sources.

An OR with an uncovered branch still scans and returns all 501 matching rows.
`IGNORE INDEX (ix_a,ix_b)` also retains the four-row result without using
the ignored keys. Those checks guard against silently dropping a branch or
bypassing index hints. This narrows a bounded access path; it is not a general
cost-based OR planner.

An additional MySQL 8.4.11 fixture puts an `IN` probe and an indexed numeric
range inside each OR branch. Both first-branch probes are broad on their own:
membership has 500 candidates and the range has 502, but only two overlap;
the second branch contributes two more rows. FSDB returned MySQL's count of
four, yet performed 504 leading residual calls because the first range was
rejected as too broad in isolation. When a branch has a safe membership probe,
the planner now admits a broad numeric range for row-identity intersection.
The regression keeps residual calls below 20 for direct, pass-through derived,
and stable CTE sources. This retains the range probe's existing type and index
hint checks.
