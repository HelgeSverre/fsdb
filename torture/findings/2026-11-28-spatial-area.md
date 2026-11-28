# Polygon area

`ST_Area` accepts POLYGON and MULTIPOLYGON, including MySQL binary
geometry values. It returns DOUBLE and preserves NULL. Other valid
geometry types, including empty collections, return 3516. Malformed
input returns 3037, an unknown SRID returns 3548, and a nonfinite area
returns 1690. A prepared call advertises a GEOMETRY argument and a
nullable DOUBLE result.

Cartesian and projected polygons use their coordinate units squared.
Geographic polygons return square meters using the SRS ellipsoid.
The calculation walks the exterior and holes in the directions used
by MySQL's area strategy. In geographic area, reversing the walk can
change the result because the first-order edge approximation depends
on direction. Polar rings use the pole-crossing adjustment.
[MySQL 8.4 area contract](https://dev.mysql.com/doc/refman/8.4/en/gis-polygon-property-functions.html#function_st-area)
[MySQL 8.4.11 area implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/area.cc)
[Boost 1.84 geographic strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategy/geographic/area.hpp)

```sql
SELECT ST_Area(ST_GeomFromText(
  'POLYGON((0 0,10 0,10 20,0 20,0 0),(1 1,3 1,3 3,1 3,1 1))'));
SELECT ST_Area(ST_GeomFromText(
  'POLYGON((179 0,-179 0,-179 1,179 1,179 0))',4326,'axis-order=long-lat'));
```

Pinned MySQL 8.4.11 returned 196 and approximately 24,619,443,710.34 for
these two queries. The differential run covered 414 results, including
392 geographic probes across EPSG 4326, 4258, 4267, and 4030. Fsdb's
memory and WAL modes agreed throughout. Cartesian values, diagnostics,
and prepared metadata matched MySQL. The largest geographic difference
was about 0.713 square meters on a 475-square-meter Clarke 1866 polygon.

A stored generated column using `ST_Area(shape)` matched the MySQL
indexed range result and NULL handling for WGS 84 parcels. The same
query matched after terminating and reopening the WAL-backed server.

## Area of geographic overlay results

In a 36-pair WGS 84 quadrilateral sample, 15 symmetric differences
returned a different geometry type. In one equatorial pair, both
engines measured the directed differences at about 64.59 and 22.51
billion square meters. MySQL's forward symmetric difference returned
only the latter polygon. Reversing the operands returned both polygons
on MySQL, as both orders did on Fsdb. The existing geographic overlay
gap therefore affects area as well as the reported geometry type.

```sql
SET @a = ST_GeomFromText('POLYGON((
  1.232678937691 -0.499506348621, 4.232678937691 -0.399506348621,
  3.632678937691 2.500493651379, 0.732678937691 1.200493651379,
  1.232678937691 -0.499506348621))',4326,'axis-order=long-lat');
SET @b = ST_GeomFromText('POLYGON((
  2.632678937691 0.800493651379, 5.232678937691 -0.649506348621,
  4.332678937691 2.100493651379, 2.532678937691 1.700493651379,
  2.632678937691 0.800493651379))',4326,'axis-order=long-lat');
SELECT ROUND(ST_Area(ST_SymDifference(@a,@b))),
       ROUND(ST_Area(ST_SymDifference(@b,@a)));
```
