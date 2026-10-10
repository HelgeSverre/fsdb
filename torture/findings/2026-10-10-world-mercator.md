# EPSG 3395 World Mercator

MySQL 8.4.11 exposes EPSG 3395 as `WGS 84 / World Mercator`, a metric,
longitude/easting-first projected SRS. Its definition uses Mercator variant A
over the WGS 84 ellipsoid. The differential contract compares that catalog row,
planar distance, and `ST_Transform` among EPSG 4326, 3857, and 3395.

For Oslo (`POINT(59.9139 10.7522)` in 4326), native MySQL transforms to
`POINT(1196929.428907435 8343586.513829184)` in 3395. Round trips and
3857↔3395 conversion are compared at five or six decimal places because the
projection and inverse are approximate floating-point calculations.

At either geographic pole, MySQL returns a non-NULL projected geometry whose
point coordinates are positive infinity. `ST_AsText` of that geometry is NULL,
while `ST_SRID` reports invalid GIS data (3037/22023). The contract checks
these distinct behaviors. Broader EPSG transformations and non-point
geographic distance and topology remain outside fsdb's supported SRS set.
