# Spatial centroids

`ST_Centroid` returns a point in the input's Cartesian or projected spatial
reference system. Points are weighted equally, line segments by length,
and polygons by signed area with holes subtracted. For nested collections,
the highest-dimensional members determine the result. NULL and empty
Cartesian collections return NULL. Geographic inputs are rejected with
error 3618 after the applicable geometry checks.
[MySQL 8.4 centroid reference](https://dev.mysql.com/doc/refman/8.4/en/gis-polygon-property-functions.html#function_st-centroid)

```sql
SELECT ST_AsText(ST_Centroid(ST_GeomFromText(
  'GEOMETRYCOLLECTION(POINT(90 90),LINESTRING(0 0,4 0))'
)));
-- POINT(2 0)
```

The pinned MySQL 8.4.11 comparison covered 180 seeded line strings,
multipoints, and mixed collections with polygon holes, some more than
10 billion coordinate units from the origin. The parsed coordinate
values matched in every case. Four binary preparations
matched parameter and result descriptors. The audited invalid geometry,
byte order, unknown SRID, and geographic diagnostics matched, as did
nullable `POINT` column types in a view and an empty `CREATE TABLE AS
SELECT`. Memory and WAL runs produced identical results.

A stored `POINT` generated from the centroid matched MySQL before and
after reopening Fsdb's WAL-backed database, including the binary point,
its SRID, and NULL handling. When a multipoint coordinate sum overflows
to infinity, the result retains the binary point; `ST_AsText` returns NULL.
