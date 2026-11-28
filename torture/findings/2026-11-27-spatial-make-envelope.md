# Spatial envelopes from point pairs

The pinned MySQL 8.4.11 image returns a rectangle, an ordered horizontal
or vertical segment, or a point from `ST_MakeEnvelope`. It derives the
bounds from both coordinates and writes the result with SRID 0, including
when both inputs have the same projected SRID. With equal coordinates,
the lower bound uses the second input's sign for zero. MySQL rejects
different SRIDs before consulting the shared SRS, and rejects geographic
SRSs before checking the input types.
[MySQL spatial convenience functions](https://dev.mysql.com/doc/refman/8.4/en/spatial-convenience-functions.html)

```sql
SELECT ST_AsText(ST_MakeEnvelope(POINT(3,5), POINT(1,2)));
-- POLYGON((1 2,3 2,3 5,1 5,1 2))

SELECT ST_AsText(ST_MakeEnvelope(POINT(1,5), POINT(1,2)));
-- LINESTRING(1 2,1 5)

CREATE TABLE envelopes AS
SELECT ST_MakeEnvelope(POINT(3,5), POINT(1,2)) AS shape;
-- shape is a nullable POLYGON.
```

Fsdb shares the shape construction with `ST_Envelope`. The differential
run compared 227 statements in memory and on disk. It covered exact WKB
for seeded Cartesian coordinates and degenerate bounds, projected SRIDs,
NULLs, bad arguments, raw byte order and nonfinite coordinates, view and
table column declarations, and results of an MBR query over an indexed
column. Prepared execution matched the parameter and result metadata
and returned the same binary geometry.

## Spatial index probes

In a table of 128 indexed points at `(id, 0)`, the predicate
`MBRCONTAINS(ST_MakeEnvelope(POINT(63.5,-1),POINT(65.5,1)),p)`
now supplies a spatial range with two candidate rows in Fsdb's plan. The
previous plan scanned all 128 rows. MySQL lists the index as possible
and uses it with `FORCE INDEX` on this fixture.
[MySQL spatial index selection](https://dev.mysql.com/doc/refman/8.4/en/using-spatial-indexes.html)

The planner can evaluate built-in geometry constructors with literal
arguments, including projected points and point WKB. Row-dependent
arguments and text-to-number coordinate coercion continue through normal
expression evaluation. An invalid constant can still report its SQL error
during execution.

The 25-statement wire comparison matched MySQL 8.4.11 before and after,
including SELECT with and without index hints, UPDATE, DELETE, and
mismatched-SRID diagnostics. The new range plan also appeared with WAL
enabled.

## Materialized sources

Fsdb and MySQL return error 1416 when a derived table with a `LIMIT`
materializes a point into the declared polygon column. This also applies
when the outer query omits the column or filters out the affected row
after the derived `LIMIT`. The wire comparison matched 23 statements
in memory and with WAL enabled. It covered `DISTINCT`, unions, CTEs,
recursive CTE anchors, lateral sources, constant views, and explicit
`ALGORITHM=TEMPTABLE` views. Mergeable sources still return the point.
An empty source or an unused CTE can skip validation. A union of an
envelope and bare NULL uses the general geometry type and accepts both
values. [MySQL derived-table materialization](https://dev.mysql.com/doc/refman/8.4/en/derived-table-optimization.html)

An eligible derived table materialized through `NO_MERGE` also returns
error 1416 in Fsdb and MySQL. MySQL applies the same check to a CTE
reference materialized by the hint. Fsdb still returns the point in that
case. Disabling `derived_merge` through `optimizer_switch` remains open.
