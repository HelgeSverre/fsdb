# EPSG 4030: unspecified WGS 84 datum

Status: implemented for construction, catalog metadata, geographic distance,
and MySQL's transformation refusal.

The pinned MySQL 8.4.11 oracle registers EPSG 4030 as “Unknown datum based upon
the WGS 84 ellipsoid.” Its geographic axis order is latitude/longitude, and
its spheroid has semi-major axis 6,378,137 metres and inverse flattening
298.257223563. MySQL returns 111045.29885331448 metres between
`POINT(40 -75)` and `POINT(41 -75)` in this SRS. A transform from 4030 to 4326
fails with 3743/22S00; the reverse fails with 3744/22S00. The definition has
no `TOWGS84` clause, so sharing an ellipsoid does not imply a valid datum
transformation. Transforming a geometry to its own SRID preserves it.

The `geographic-spatial-contracts` case compares the complete catalog row,
a prepared distance query, the same-SRID transform, and both error contracts
against the pinned oracle.

Oracle probes:

```sql
SELECT SRS_NAME, DEFINITION
FROM INFORMATION_SCHEMA.ST_SPATIAL_REFERENCE_SYSTEMS
WHERE SRS_ID = 4030;

SELECT ST_Distance(
  ST_GeomFromText('POINT(40 -75)', 4030),
  ST_GeomFromText('POINT(41 -75)', 4030));

SELECT ST_Transform(ST_GeomFromText('POINT(40 -75)', 4030), 4326);
SELECT ST_Transform(ST_GeomFromText('POINT(40 -75)', 4326), 4030);
```
