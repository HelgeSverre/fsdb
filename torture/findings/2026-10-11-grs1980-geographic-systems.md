# ETRS89 and NAD83 geographic reference systems

Status: ETRS89 (EPSG 4258) and NAD83 (EPSG 4269) are registered for geometry
construction, serialization, geographic point distance and line length, and
`INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS`. Transformations involving
these systems remain open.

MySQL 8.4.11 reports latitude-longitude axis order and the GRS 1980 ellipsoid
for both systems: semi-major axis 6,378,137 metres and inverse flattening
298.257222101. It reports the distinct EPSG definitions for ETRS89 and NAD83,
including their `TOWGS84` clauses. `ST_AsText` of
`ST_GeomFromText('POINT(40 -75)',4258)` and the equivalent NAD83 expression is
`POINT(40 -75)`. The distance to `POINT(41 -75)` in either system is
111045.29885197058 metres.

Oracle query:

```sql
SELECT SRS_ID, SRS_NAME, DEFINITION
FROM INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS
WHERE SRS_ID IN (4258, 4269)
ORDER BY SRS_ID;

SELECT ST_Distance(
  ST_GeomFromText('POINT(40 -75)', 4258),
  ST_GeomFromText('POINT(41 -75)', 4258));
```
