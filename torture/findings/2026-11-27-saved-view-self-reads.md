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
self-reads, unused top-level CTEs, dependent outer predicates, and conflicts introduced by
joining a separate read-side view whose predicate reads the mutation
target.
