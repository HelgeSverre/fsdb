# Updating a target after dependent lateral sources

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` when one or two read-only
dependent lateral sources precede a physical write target in an inner-join
view. In the two-source case, the second lateral body refers to the first
lateral result:

```sql
CREATE VIEW lateral_chain AS
  SELECT t.id,t.n,d.flag,e.flag AS second_flag
  FROM anchor a
  JOIN LATERAL (
    SELECT b.id,b.flag FROM source1 b WHERE b.id=a.id LIMIT 1
  ) d ON TRUE
  JOIN LATERAL (
    SELECT c.id,c.flag FROM source2 c WHERE c.id=d.id LIMIT 1
  ) e ON TRUE
  JOIN target t ON t.id=e.id;
```

With anchor and target IDs 1 and 2, and only ID 1 in each lateral source,
`UPDATE lateral_chain SET n=n+1 WHERE id=1` affects one target row;
the same update for ID 2 affects none. FSDB already matched this behavior
when probed, including the simpler one-lateral-source form. The focused
regression pins writable metadata, both update counts, and the resulting
physical rows. No engine change was needed.

This confirms the audited inner-join dependency chain, not arbitrary lateral
reordering, nested security layers, outer joins, or multi-target view writes.
