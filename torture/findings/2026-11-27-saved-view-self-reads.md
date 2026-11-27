# Subqueries that read a saved view's base table

MySQL 8.4.11 can report `IS_UPDATABLE=NO` for a single-source view
whose saved query also opens the source table in a subquery. It rejects
`WITH CHECK OPTION` with 1368 for that definition and for a direct
outer layer over it. Catalog updateability also accounts for a
materialized read and an uncorrelated projection subquery.
[MySQL view registration](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_view.cc#L705-L759)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
INSERT INTO items VALUES (1, 10);
CREATE VIEW filtered AS SELECT id, n FROM items
WHERE EXISTS (SELECT 1 FROM items r WHERE r.id = items.id);
CREATE VIEW outer_filter AS SELECT id, n FROM filtered;

UPDATE filtered SET n = 11 WHERE id = 1;  -- 1288
INSERT INTO filtered VALUES (2, 20);
UPDATE outer_filter SET n = 11 WHERE id = 1;
DELETE FROM outer_filter WHERE id = 1;   -- 1395
```

For an update, the physical assignment target determines whether a
saved predicate or ordering subquery conflicts. Updating the other
component of an inner-join view can still succeed. A materialized
derived table or view supplies a separate read, and an uncorrelated
projection can remain usable for updating a directly exposed column.

An outer view over a predicate that keeps a second source can return
1395 for DELETE or REPLACE with a column list. An implicit column list
returns 1394 for INSERT and REPLACE, while explicit-column INSERT can
succeed. A source-free predicate or an unrelated
subquery eliminated by a constant-false condition can still allow the
outer delete. A projection that reads the target table can instead
produce 1288 for DELETE without preventing UPDATE.

The 137-observation matrix improved from 58 exact MySQL matches to 112
in each fsdb storage mode. It covers metadata, UPDATE, INSERT, DELETE,
and CHECK OPTION across 26 saved definitions plus seven joined writes.
Eight statements matched the expected errors and completed without
waiting while another session held the base row. Their binary PREPARE
diagnostics also matched, including after a durable restart.

Remaining differences include some INSERTs through views with indirect
self-reads, unused top-level CTEs, and dependent outer predicates.

## Direct joined updates

MySQL checks subqueries owned by a directly merged read-side view
against the physical assignment targets of an UPDATE. The check
includes predicates, ordering, and scalar projections, even when the
outer statement ignores the projection or its WHERE is false.
[MySQL duplicate-target check](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_update.cc#L1723-L1743)

```sql
CREATE TABLE items (id INT PRIMARY KEY, n INT);
CREATE TABLE labels (id INT PRIMARY KEY, tag INT);
CREATE VIEW matching_labels AS
  SELECT l.id, l.tag FROM labels l
  WHERE EXISTS (SELECT 1 FROM items p WHERE p.id = l.id);

UPDATE items i JOIN matching_labels r ON i.id = r.id
SET i.n = 11 WHERE 0;
```

Both engines return 1443 with `r` as the read-side view and `i` as
the leading leaf. When the leading leaf owns the conflicting read,
MySQL returns 1288 instead. A RIGHT JOIN can make its right operand
the leading leaf. A materialized derived operand uses its exposed
alias, while a merged derived operand can report its first base
table. [MySQL duplicate-target diagnostics](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_base.cc#L2305-L2338)

A materialized read-side view, a nested view with no subquery of its
own, and assignments to an unrelated physical table remain allowed.
The audited dependent and volatile projections materialize by
default, and explicit merge directives can change whether their
reads enter the check. [MySQL merge rules](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_resolver.cc#L3367-L3386)

The 30-case UPDATE matrix improved from 9 to 30 exact matches on
acceptance or error code and message in both Fsdb storage modes.
Additional right-join, optimizer-hint, and projection probes agreed
with MySQL. Binary PREPARE and prepared EXPLAIN returned the same
diagnostics. A conflicting UPDATE was rejected while another
session held the target row locked, and the stored value stayed
unchanged. The prepared and locked-row probes also agreed after
loading a checkpoint containing the view definitions.

Joined DELETE still has differences for scalar subqueries and for
predicates that MySQL keeps as subqueries during view resolution.
The direct correlated EXISTS control remains allowed. A separate
saved joined-view UPDATE can also fail with 1054 in Fsdb when the
outer definition renames the read-side view's source qualifier.
