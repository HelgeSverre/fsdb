# Projected longitude wrapping in coordinate transforms

Status: implemented for Pseudo-Mercator (EPSG 3857) and World Mercator
(EPSG 3395) inverse transforms. The wrapped geographic coordinate also feeds
NAD83's datum shift and projected-to-projected transforms.

MySQL 8.4.11 wraps inverse-projected longitudes outside the antimeridian.
EPSG 3857 `POINT(30000000 0)` becomes WGS84
`POINT(0 -90.50541476414367)`, while `POINT(-30000000 0)` becomes
`POINT(0 90.50541476414367)`. At the antimeridian, MySQL retains tiny
floating-point roundoff: `POINT(20037508.342789244 0)` becomes a longitude
of `180.00000000000017`, and the negative input gives
`-180.00000000000017`. Geographic constructor bounds remain stricter; the
roundoff allowance applies only to transform results.

MySQL also transforms EPSG 3857 `POINT(30000000 0)` to EPSG 3395 x-coordinate
`-10075016.685578488`, confirming that the wrap occurs before the second
projection.

Oracle query:

```sql
SELECT ST_AsText(ST_Transform(
  ST_GeomFromText('POINT(30000000 0)',3857),4326));

SELECT ST_AsText(ST_Transform(
  ST_GeomFromText('POINT(30000000 0)',3857),3395));
```
