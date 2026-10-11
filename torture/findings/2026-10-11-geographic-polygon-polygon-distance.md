# Geographic polygon-to-polygon distance

Status: implemented for `POLYGON`/`MULTIPOLYGON` pairs in supported geographic
SRSs. Geographic distance now covers audited point, line, and polygon pairs.

MySQL 8.4.11 returns zero for overlapping polygons and containment. A polygon
entirely inside another polygon's hole is disjoint: the audited inner square
is 22111.975264264012 metres from the holed polygon. The implementation
compares each polygon's rings against the other filled polygons in both
directions. This finds boundary crossings and containment while measuring
separation from exterior and hole rings. Oracle-backed cases also cover
multipart polygons, antimeridian crossings, polar caps, curved edges, and
unit conversion. The underlying geographic edge sampling can diverge near
tangent boundaries.

Oracle examples:

```sql
SELECT ST_Distance(
  ST_GeomFromText('POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))',4326),
  ST_GeomFromText('POLYGON((1.2 1.2,1.2 1.8,1.8 1.8,1.8 1.2,1.2 1.2))',4326));

SELECT ST_Distance(
  ST_GeomFromText('POLYGON((50 0,50 10,52 10,52 0,50 0))',4326),
  ST_GeomFromText('POLYGON((49 4,49 6,50.05 6,50.05 4,49 4))',4326));
```
