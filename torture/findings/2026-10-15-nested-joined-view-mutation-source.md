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
the external read source. The scheduler conservatively claims all rows of the
physical target for this shape; narrowing those claims without losing
correctness remains a performance opportunity.

The implementation handles first-source view assignments, qualified or
uniquely owned bare. More than one physical assignment target and broader
join, derived-source, and security-layer combinations remain to audit.
