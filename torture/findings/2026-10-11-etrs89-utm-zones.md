# ETRS89 UTM zones 28N–37N

MySQL 8.4.11 lists ten ETRS89 / UTM projected systems, EPSG 25828–25837.
They use the GRS 1980 ellipsoid, six-degree central-meridian steps, 0.9996
scale, and 500,000 m false easting. Fsdb now generates all ten catalog rows
and supports their forward and inverse `ST_Transform` operations through the
same UTM projection code used for WGS 84.

The MySQL-derived regression covers both endpoint zones and zone 32N at
Oslo. For example, ETRS89 `POINT(59.9139 10.7522)` transforms to
`POINT(597979.9028839062 6643118.991370019)` in EPSG 25832. Sampled
forward coordinates agree within 1 mm, and inverse coordinates within
1e-8 degrees. The differential contract compares all ten exact catalog
definitions and the Oslo forward result.

Other ETRS89 projected systems and most of MySQL's SRS catalog remain open.
