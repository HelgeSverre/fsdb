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

The same rule applies when the writable view leads a larger `UPDATE ... JOIN`
statement. MySQL waits for a contended source row inside that view and returns
zero after its predicate changes, while a write targeting a disjoint source
row proceeds. fsdb initially skipped read-source preparation for this form;
the follow-up now includes it, using only view-local WHERE conjuncts to narrow
its source claims. A physical read table can also precede the writable view in
the UPDATE's join order. The read-source planner applies the same view-moving
rewrite as write-target planning before claiming rows, so this form waits and
rebases as well.

An UPDATE assigning through two joined views must also claim the second
view's physical read source. MySQL waits when that source row is held and
returns zero changed rows after its flag stops matching; fsdb previously
completed immediately and changed both targets. The planner now visits every
assigned joined view and coalesces its source claims by physical table.
Literal equality bounds propagate through the outer inner join and then the
view's inner join, so a row-2 write does not wait for a holder on source row 1.
Both the contended and disjoint controls match the native oracle.

A view whose physical read join uses `USING(id)` has the same selective wait.
MySQL waits and rechecks a held matching source row, but allows an update of
row 2 while source row 1 is held. Fsdb previously claimed both source rows
because the `USING` key is absent from the explicit `ON` expression. The
candidate planner now carries the merged key across a single inner `USING`
edge. Longer merged-key chains retain conservative claims until their key
ownership is validated.

A single physical `NATURAL JOIN` has the same behavior. The candidate planner
derives its shared columns from the two physical tables and carries a literal
target key to the read source. For a view with two inner joins, it follows
the connected key through the intermediate table. The first edge can be an
explicit equality, `USING(id)`, or a physical `NATURAL JOIN`, followed by an
explicit equality. MySQL and fsdb both wait on a matching held source row,
recheck after commit, and allow
a disjoint row to proceed. Chains containing outer or grouped joins still
need separate ownership and null-preservation checks.

The same source-row wait also holds with autocommit enabled. fsdb routes a
joined-view UPDATE through an implicit transaction, and its post-wait result
matches MySQL's zero affected rows without an explicit `BEGIN` on the writer.

Evidence: `uv run --with pymysql python torture/scripts/cte-joined-write-oracle.py`
and the Expecto cases `joined view update rechecks a changed read source after
waiting`, `NATURAL joined view locks only its keyed read-source row`, and
`joined view propagates read-source key through explicit and merged joins`.
