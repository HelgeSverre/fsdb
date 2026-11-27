# Cartesian mixed-collection overlays

MySQL 8.4.11 accepts nested Cartesian geometry collections in
`ST_Union`, `ST_Intersection`, `ST_Difference`, and
`ST_SymDifference`. Fsdb previously passed the whole collection
to the planar overlay engine. That worked for some inputs of a
single dimension and failed with 1105 on many mixed collections.

```sql
SET @collection = ST_GeomFromText(
  'GEOMETRYCOLLECTION(LINESTRING(0 0,4 4),POINT(6 6),GEOMETRYCOLLECTION(POINT(1 1)))'
);
SET @area = ST_GeomFromText('POLYGON((2 2,2 6,6 6,6 2,2 2))');
SELECT ST_AsText(ST_Intersection(@collection, @area));
-- GEOMETRYCOLLECTION(LINESTRING(2 2,4 4),POINT(6 6))
SELECT ST_AsText(ST_Difference(@collection, @area));
-- LINESTRING(0 0,2 2)
```

The Cartesian path now splits nested collections into points, lines,
and polygons. It dissolves overlapping polygons, clips covered line
sections, and removes points covered by either dimension before the
overlay. Each operator assembles the surviving parts in its own result
shape. For example, intersections from separate dimensions can return
a geometry collection even when every surviving member is a point.
[MySQL collection normalization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/gc_utils.cc#L125-L175)
[MySQL intersection dispatch](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc#L174-L230)

The matrix used all ordered pairs of 23 shapes and all four operators
at SRID 0 and EPSG 3857. Shapes included overlapping polygons, a hole,
duplicates, nested empty collections, overlapping and crossing lines,
and isolated or covered points. For different WKT strings, both
results were parsed by MySQL and compared with `ST_Equals`.

| Outcome | Before | After |
|---|---:|---:|
| Exact WKT | 1,380 | 2,928 |
| Different WKT, same spatial set | 876 | 1,304 |
| Different spatial set | 12 | 0 |
| Fsdb error 1105 for valid input | 1,964 | 0 |

An additional 1,800 overlays on stored geometry columns retained the
same SRID, WKT, and WKB after a durable checkpoint restart. A control
matrix of 900 geographic overlays at EPSG 4326 returned the same
results as before. Geographic collections and exact Cartesian
serialization in the remaining cases are still open.
