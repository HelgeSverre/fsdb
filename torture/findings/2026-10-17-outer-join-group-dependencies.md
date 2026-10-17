# Outer-join functional dependencies in grouped queries

MySQL 8.4.11 rejects a grouped `LEFT JOIN` that uses a nullable-side key to
claim a preserved-side primary-key dependency. With two unmatched parents,
both nullable-side keys are `NULL`, while the parent labels differ:

```sql
CREATE TABLE parent (id INT PRIMARY KEY, label VARCHAR(20));
CREATE TABLE child (parent_id INT);
INSERT INTO parent VALUES (1,'a'),(2,'b');
SELECT c.parent_id,p.label,COUNT(*)
FROM parent p LEFT JOIN child c ON c.parent_id=p.id
GROUP BY c.parent_id;
```

MySQL returns 1055; fsdb formerly returned an arbitrary parent label. The
same rejection applies to the mirrored `RIGHT JOIN`. A preserved-side grouped
key may determine a unique child row, but only when all preserved-side inputs
to `ON` are fixed. MySQL accepts a child projection grouped by `p.id` with
`ON p.id=c.parent_id` when `c.parent_id` is unique, and rejects a child
projection grouped by `p.grp` with `ON p.grp=c.grp AND p.id=1` when `p.id` can
vary within the group.

A nullable unique child key is usable in this outer-join inference when an
`ON` equality requires it to be non-NULL in matched rows. Every child column
is NULL in unmatched rows. MySQL accepts `GROUP BY p.grp` selecting `c.label`
for `LEFT JOIN c ON p.grp=c.grp` with `c.grp INT UNIQUE`, including a parent
whose `grp` is NULL, while it rejects the same projection when the child is
queried alone or the join uses an unrelated child column. fsdb now applies
that conditional unique-key rule.

The same dependency direction applies to `USING` and `NATURAL` joins. Their
equalities are generated during join resolution, so grouped validation now
uses that resolved condition too. Pinned MySQL probes accept a grouped
preserved primary key and a unique child projection for `LEFT JOIN ... USING`,
`NATURAL LEFT JOIN`, and mirrored `RIGHT JOIN ... USING`; they reject grouping
by the nullable child key while selecting an arbitrary parent label. Focused
fsdb regressions cover accepted and rejected forms.

A parenthesized joined operand can contain the unique child. MySQL accepts
`p LEFT JOIN (c JOIN d ON c.id=d.cid) ON p.id=c.pid`, grouped by `p.id`, while
selecting `c.label` when `c.pid` is unique, even if two `d` rows multiply the
matching child. It rejects selecting the non-unique `d.tag` or selecting
`p.label` while grouping by the nullable `c.pid`. The validator now walks
physical leaves inside grouped operands when collecting candidate keys; the
focused regression pins all three results.
