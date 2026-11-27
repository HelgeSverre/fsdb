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

A separate 128-row fixture found an access-path gap. With
`MBRCONTAINS(ST_MakeEnvelope(POINT(63.5,-1),POINT(65.5,1)),p)`, MySQL
lists the spatial index as possible and selects it with `FORCE INDEX`.
Fsdb scans the table. Fsdb uses the index for the equivalent
`ST_GeomFromText` polygon and examines two candidates.

MySQL enforces the polygon subtype when materializing a derived source.
For example, selecting a point envelope from a derived SELECT with a
`LIMIT` returns error 1416. The same was observed for a limited CTE,
a constant view, and an explicit `ALGORITHM=TEMPTABLE` view. Merged views
and mergeable derived queries return the point. Fsdb currently returns
the point in all of those cases. Explicit table creation from the same
degenerate envelope already returns error 1416 in both engines.
