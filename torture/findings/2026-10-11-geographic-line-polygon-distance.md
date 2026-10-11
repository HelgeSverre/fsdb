# Geographic line-to-polygon distance

Status: implemented for `LINESTRING`/`MULTILINESTRING` against
`POLYGON`/`MULTIPOLYGON` in supported geographic SRSs, in either argument
order.

MySQL 8.4.11 returns zero for a line crossing a ring or lying in the filled
polygon. A line entirely inside a hole is disjoint and measures to the hole
boundary: `LINESTRING(1.2 1.5,1.8 1.5)` is 22110.449683532755 metres from
the surrounding polygon. The implementation combines endpoint-to-polygon
distance with line-to-ring distance. Audited cases cover disjoint and curved
edges, holes, antimeridian crossings, polar caps, multipart geometries, and
unit conversion. Geographic edge sampling can diverge near tangent boundaries.

Oracle examples:

```sql
SELECT ST_Distance(
  ST_GeomFromText('LINESTRING(1.2 1.5,1.8 1.5)',4326),
  ST_GeomFromText('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))',4326));

SELECT ST_Distance(
  ST_GeomFromText('LINESTRING(50.05 4,50.05 6)',4326),
  ST_GeomFromText('POLYGON((50 0,50 10,52 10,52 0,50 0))',4326));
```
