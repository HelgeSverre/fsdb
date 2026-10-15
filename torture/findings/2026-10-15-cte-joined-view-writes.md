# CTE sources in joined updatable views

Native MySQL 8.4.11 marks a joined view updatable when a read-only CTE
precedes a writable physical table. A single CTE can be an aggregate,
pass-through, or limited projection. Two independent aggregate CTEs can
also precede the target. A CTE may explicitly rename its projected columns
or join two physical read sources. In each audited case, `UPDATE` changes
the target row once even when a CTE yields duplicate matching rows.

The focused contract creates a target `a(id,n)` and source `b(id,flag)`, then
uses this representative view:

```sql
CREATE VIEW v AS
WITH d AS (SELECT id, SUM(flag) AS flag FROM b GROUP BY id)
SELECT a.id, a.n, d.flag FROM d JOIN a ON a.id = d.id;
```

MySQL reports `IS_UPDATABLE = YES`; `UPDATE v SET n = n + 1 WHERE id = 1`
affects one row. With `WHERE a.n >= 0 WITH CASCADED CHECK OPTION`, setting
`n = -1` fails with error 1369 and leaves the target unchanged. `INSERT`
through the joined view fails with error 1471, including when the CTE is a
simple pass-through projection.

FSDB previously reported `NO` and rejected all three UPDATE shapes with
error 1288 because the writable-view resolver rejected every `WITH` clause.
Metadata and execution now share a bounded normalization: a nonrecursive,
independent CTE is treated as a read-only derived source while
resolving the joined write. Explicit CTE column names become projection
aliases in that derived source; CTE bodies may join physical read sources.
When the same CTE appears twice, both aliases share one materialized rowset
for the write. MySQL 8.4.11 marks the joined view updatable for both a
pass-through CTE and an aggregate CTE with renamed columns. Duplicate join
matches still update the physical target once, and `WITH CASCADED CHECK OPTION`
rejects an invalid target value with error 1369. A repeated CTE projecting
`RAND()` also uses one materialization: equality between the two aliases'
random values matches, and the target row is updated.
The original view definition continues to serve reads. Recursive or dependent
CTEs, nested query expressions, and starred explicit-column projections retain
the conservative non-updatable path. Independent CTE bodies using plain derived
sources are covered by the [follow-up oracle](2026-10-15-derived-cte-joined-view-writes.md).

The native oracle was run against MySQL 8.4.11. The focused Expecto case
checks three basic one-CTE shapes, a two-CTE composition, renamed columns,
joined CTE bodies, repeated references (including a volatile projection),
metadata, affected rows, base state, CHECK OPTION, and INSERT errors.
