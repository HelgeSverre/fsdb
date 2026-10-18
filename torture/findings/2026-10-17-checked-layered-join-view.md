# CHECK OPTION above a joined writable view

MySQL 8.4.11 accepts a checked single-source view over a checked inner-join
view. The outer view remains updatable. An update that passes both predicates
reaches the physical table; an update that passes the inner predicate but
violates only the outer predicate fails with error 1369 and leaves the row
unchanged.

```sql
CREATE TABLE t(id INT PRIMARY KEY, n INT NOT NULL);
CREATE TABLE a(id INT PRIMARY KEY, v INT NOT NULL);
INSERT INTO t VALUES (1, 10);
INSERT INTO a VALUES (1, 1);
CREATE VIEW base_v AS
  SELECT t.id, t.n, a.v FROM t JOIN a ON a.id = t.id
  WHERE t.n > a.v WITH CASCADED CHECK OPTION;
CREATE VIEW outer_v AS
  SELECT id, n FROM base_v WHERE n > v + 1
  WITH CASCADED CHECK OPTION;
UPDATE outer_v SET n = 11 WHERE id = 1;
UPDATE outer_v SET n = 2 WHERE id = 1; -- 1369; t.n stays 11
```

Fsdb previously returned 1368 while creating `outer_v`. The single-source
view classifier already carries the inner joined write source and its join
check, but a separate guard rejected any outer CHECK OPTION over that source.
Removing that guard makes this shape work; the focused Expecto regression
checks the result and rejection.

This does not cover an outer view that adds its own join to an inner joined
view. In four separately probed MySQL 8.4.11 shapes, an outer LATERAL,
JSON_TABLE, derived, or derived-USING source still produces an updatable view
and enforces its CHECK OPTION. Fsdb currently returns 1368 when creating each
such checked view. Supporting them requires composing the inner view's join
sources with the outer join while preserving qualifiers, check predicates,
and view-security boundaries.
