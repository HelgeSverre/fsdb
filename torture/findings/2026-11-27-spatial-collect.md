# Spatial collection aggregates

`ST_Collect` supports ordinary groups and window frames, including
`DISTINCT` in either form. It ignores SQL NULL and retains empty geometry
members. A homogeneous set of points, lines, or polygons
produces the corresponding multipart shape. A mix of types or input
multipart and collection shapes produces a geometry collection. The
aggregate preserves member order within the input it receives and
normalizes polygon winding.

`DISTINCT` keeps the first spatially equal representative. The oracle
cases include reversed lines, points separated by one floating-point
step, and a geographic collection whose point is covered by its own
line. A geographic collection with an uncovered point remains distinct.
An SRID mismatch raises 4034 with SQLSTATE `22S05`. The prepared result
descriptor is nullable GEOMETRY with binary collation. MySQL describes an
unconstrained input placeholder as VAR_STRING.

This also fixes type reconciliation for `UNION` and `UNION ALL` where
every branch returns geometry or bare NULL. The derived rows retain
typed geometry, including their SRIDs, when read by the aggregate.

The [SQL fixture](2026-11-27-spatial-collect.sql) matched all 19 result
rows and all three error lines against the digest-pinned MySQL 8.4.11
image in memory and on disk. A separate seeded comparison matched 70
group summaries and 560 moving-frame results in each mode. These cases
cover the audited Cartesian and geographic shapes and reference systems.
Broader mixed-collection topology and spatial precision limits remain
tracked in the spatial gap notes.

[MySQL spatial aggregate functions](https://dev.mysql.com/doc/refman/8.4/en/spatial-aggregate-functions.html)
