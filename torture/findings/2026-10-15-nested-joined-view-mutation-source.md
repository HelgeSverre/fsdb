# Joined UPDATE through a joined updatable view

MySQL 8.4.11 accepts an UPDATE whose first source is an updatable view of an
inner join and whose statement adds another join:

```sql
CREATE TABLE t(id INT PRIMARY KEY, n INT);
CREATE TABLE l(id INT PRIMARY KEY);
CREATE TABLE x(id INT PRIMARY KEY);
CREATE VIEW v AS SELECT t.id, t.n FROM t JOIN l ON l.id=t.id;
UPDATE v JOIN x ON x.id=v.id SET v.n=v.n+1 WHERE v.id=1;
```

The [disposable-server oracle](../scripts/joined-view-nested-mutation-oracle.py)
runs this with the writable `t` before and after `l` inside the view. In both
orders, an owner transaction first increments `t.n`; the view-addressed joined
UPDATE waits for its row, then increments the committed value. The final
value is 2 and the joined UPDATE affects one row. Both a qualified `v.n` and
a uniquely owned bare `n` assignment work; adding `n` to the outer source
produces MySQL's 1052 ambiguity error. A `CASCADED CHECK OPTION`
view over the same join rejects a joined UPDATE that would remove the row
from its view with error 1369, leaving the base row unchanged.

Before this change, Fsdb returned 1288 because the external joined-source
rewrite rejected views containing their own joins. It now appends the view's
join to the physical source, rewrites exposed columns in the external join,
and claims the physical assignment target before execution. Tests cover both
internal source orders, rebasing, the CHECK OPTION, and caller permission on
the external read source. A literal equality on an exposed column mapped to
the writable physical table narrows the pre-execution row claim; other shapes
conservatively claim all its rows. The generic joined writer now passes its
matched physical rows to storage, avoiding a later full-table lock. With an
owner holding `t.id=2`, a joined view update restricted to `v.id=1` completes
before the owner commits, matching the oracle's disjoint-row result.

The implementation handles view assignments in the first, second, or third
inner-join position, qualified or uniquely owned bare.

## A physical target beside the writable view

MySQL also accepts `UPDATE x JOIN v ON x.id=v.id SET x.n=x.n+1,v.n=v.n+1`
and the reversed join order, reporting two affected rows. A three-source
inner join with the view last has the same behavior. Fsdb previously returned
1288 because the view rewrite assumed every assignment targeted that view.
It now retains the outer physical assignment while mapping only the view's
assignment to its physical component. A joined view can also supply read rows
for an UPDATE targeting only the other physical table.

The oracle and regression cover two-target row counts, a contended view row
that waits and rebases both targets, a CHECK OPTION rejection that leaves
both tables unchanged, and a caller missing `UPDATE` on the outer target
receiving MySQL error 1143. A definer with no rights on that outer table can
still expose the view target; the caller's outer-table grant is checked
separately. The same authorization split works through an additional view
layer with a different definer. `CURRENT_USER()` in the caller's outer-table
assignment stays the caller identity rather than changing to the view definer.
Further multi-target source kinds, bare
assignments, and outer joins remain to audit.
