# Composite row-IN probes over stable materialized sources

Native MySQL 8.4.11 returns counts `(2, 1, 1)` for three outer rows whose
two candidate pairs include NULL components:

```sql
SELECT o.id,
       (SELECT COUNT(*)
        FROM (SELECT id,x,y FROM inner_rows LIMIT 100) d
        WHERE (d.x,d.y) IN ((o.a,o.b),(o.c,o.d)))
FROM outer_rows o ORDER BY o.id;
```

A stable CTE in place of the derived source returns the same counts. The
[native oracle](../scripts/correlated-row-in-oracle.py) checks both forms.

FSDB already resolved the correct rows, but its materialized correlated `IN`
candidate path only keyed one inner column. With 1,004 materialized rows and a
leading `TOUCH(d.id)=d.id` residual, the derived query evaluated that residual
4,020 times. The candidate path now uses the existing tuple-aware `IN` probe
parser and a statement-local composite equality key. It checks each component
against its stored key domain and effective comparison collation, discards
NULL-bearing candidate tuples for WHERE-true matching, and deduplicates rows
found by multiple candidate tuples. Unsupported bounds retain residual
evaluation. The focused regression covers both derived and CTE sources and
keeps residual evaluations below 20.

This is a bounded optimization for stable materialized sources, not a general
row-expression planner or a change to three-valued SQL results.
