# Writable target after multiple derived STRAIGHT_JOIN sources

Native MySQL 8.4.11 reports `IS_UPDATABLE=YES` when two aggregate-derived
sources precede a physical target and either the derived-to-derived join, the
target join, or both use `STRAIGHT_JOIN`:

```sql
CREATE VIEW target_v AS
  SELECT a.id AS aid,a.n,d1.flag,d2.aux
  FROM (SELECT id,SUM(flag) AS flag FROM b GROUP BY id) d1
  JOIN (SELECT id,SUM(aux) AS aux FROM c GROUP BY id) d2 ON d2.id=d1.id
  STRAIGHT_JOIN a ON a.id=d1.id AND a.n<d2.aux;
```

With `a.n=0`, `b.flag=7`, and `c.aux=9` at ID 1, updating through each of
the three view forms incremented `a.n` to 3. Setting `a.n=20` then updating
through `target_v` left it at 20: the moved target predicate still filtered
the write. FSDB previously reported `IS_UPDATABLE=NO` for this multi-derived
shape, although it already handled one leading derived source with a straight
target join.

The shared leading-derived classifier now admits `STRAIGHT_JOIN` edges when
identifying a writable physical component. The existing reorder path keeps
the preceding derived-source join order and evaluates the target predicate
after its referenced sources are bound. A focused regression checks metadata,
all three writes, and the filtered update. This does not establish arbitrary
join reorderings, lateral dependencies, or outer-join writes.
