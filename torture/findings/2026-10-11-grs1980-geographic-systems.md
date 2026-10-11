# ETRS89 and NAD83 geographic reference systems

Status: ETRS89 (EPSG 4258) and NAD83 (EPSG 4269) are registered for geometry
construction, serialization, geographic point distance and line length, and
`INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS`. Transformations among these
systems, WGS84, Pseudo-Mercator, and World Mercator are supported.

MySQL 8.4.11 reports latitude-longitude axis order and the GRS 1980 ellipsoid
for both systems: semi-major axis 6,378,137 metres and inverse flattening
298.257222101. It reports the distinct EPSG definitions for ETRS89 and NAD83,
including their `TOWGS84` clauses. `ST_AsText` of
`ST_GeomFromText('POINT(40 -75)',4258)` and the equivalent NAD83 expression is
`POINT(40 -75)`. The distance to `POINT(41 -75)` in either system is
111045.29885197058 metres.

MySQL transforms ETRS89 `POINT(40 -75)` to the same WGS84 coordinates. NAD83
to WGS84 yields `POINT(39.99999719341358 -74.99998565769141)`, reflecting
NAD83's `TOWGS84[1,1,-1,0,0,0,0]` geocentric translation. The reverse gives
`POINT(40.0000028065836 -75.00001434230444)`. NAD83 to Pseudo-Mercator
gives `POINT(-8348960.212917021 4865941.871657689)`.

Oracle query:

```sql
SELECT SRS_ID, SRS_NAME, DEFINITION
FROM INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS
WHERE SRS_ID IN (4258, 4269)
ORDER BY SRS_ID;

SELECT ST_Distance(
  ST_GeomFromText('POINT(40 -75)', 4258),
  ST_GeomFromText('POINT(41 -75)', 4258));

SELECT ST_AsText(ST_Transform(
  ST_GeomFromText('POINT(40 -75)', 4269), 4326));
```
