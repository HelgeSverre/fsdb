# Geographic point-to-polygon distance

Status: implemented for `POINT`/`MULTIPOINT` against `POLYGON`/`MULTIPOLYGON`
in supported geographic SRSs, in either argument order. Line-to-polygon and
polygon-to-polygon distance remain open.

MySQL 8.4.11 measures distance to the polygon boundary when a point is outside
the exterior or inside a hole, and returns zero for points in the filled area
or on a ring. Its geographic edges are curved: `POINT(50.05 5)` is outside
`POLYGON((50 0,50 10,52 10,52 0,50 0))` by 6432.016521477691 metres,
although a longitude/latitude drawing makes the point look interior. The
implementation measures each geodesic ring segment and uses spherical winding
over sampled geographic edges for containment. It covers audited holes,
antimeridian crossings, polar caps, multipart geometries, and unit conversion.
Sampling can disagree with MySQL near a curved boundary; that remains a
precision limitation, as with the line-to-line crossing test.

Oracle examples:

```sql
SELECT ST_Distance(
  ST_GeomFromText('POINT(1.5 1.5)',4326),
  ST_GeomFromText('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))',4326));

SELECT ST_Distance(
  ST_GeomFromText('POINT(50.05 5)',4326),
  ST_GeomFromText('POLYGON((50 0,50 10,52 10,52 0,50 0))',4326));
```
