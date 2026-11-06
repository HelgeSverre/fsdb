# Joined-view UPDATE waits for its read source

Native MySQL 8.4.11 waits when an UPDATE through a joined view reads a row held
by another transaction, even if the owner does not hold the writable target.
After the owner commits a change that makes the view predicate false, the
waiting UPDATE affects zero rows. Before this fix, fsdb completed immediately
and reported one affected row from its older source snapshot.

```sql
CREATE TABLE a (id INT PRIMARY KEY, n INT);
CREATE TABLE source_lock (id INT PRIMARY KEY, flag INT);
CREATE VIEW source_lock_view AS
  SELECT a.id, a.n, s.flag FROM a JOIN source_lock s ON s.id=a.id;

-- owner: BEGIN; UPDATE source_lock SET flag=0 WHERE id=1;
-- waiter: BEGIN;
UPDATE source_lock_view SET n=n+1 WHERE id=1 AND flag=1;
```

The native oracle also checks that an owner of source row 1 does not delay a
joined-view UPDATE of row 2. fsdb now claims the relevant source rows before
the target rows and rebases the statement snapshot after waiting. A literal
equality on the writable target is carried across an inner equality join to
narrow the read-source claim. Other joined-source predicates retain the
conservative candidate scan; further selective planning remains useful.

Evidence: `uv run --with pymysql torture/scripts/cte-joined-write-oracle.py`
and the Expecto case `joined view update rechecks a changed read source after
waiting`.
