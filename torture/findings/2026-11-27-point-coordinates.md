# Point coordinate accessors and setters

Fsdb now supports the one- and two-argument forms of `ST_X`, `ST_Y`,
`ST_Latitude`, and `ST_Longitude`. MySQL 8.4.11 defines X and Y in
the order declared by the point's spatial reference system. For SRID
4326, X reads latitude and Y reads longitude even when the input was
parsed with `axis-order=long-lat`. The named latitude and longitude
functions require a geographic SRS. [MySQL point property functions](https://dev.mysql.com/doc/refman/8.4/en/gis-point-property-functions.html)

Getters return `DOUBLE`. Setters return a new point with the same SRID.
MySQL validates geographic replacements in radians. The conversion
can round an endpoint slightly past the degree boundary into range,
and it can change the last digits of a valid coordinate during a
round trip. The input point must pass its own range checks before
the replacement is applied. [MySQL coordinate implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_geofunc.cc#L4553-L4692)

The SQL fixture matched 17 output rows and 11 errors in both in-memory
and durable Fsdb. It covers endpoint rounding, malformed geometries,
SQL preparation, and updates to a spatially indexed point. A strict
update rejects a truncated coordinate and leaves the row intact.
`UPDATE IGNORE` writes the coerced coordinate and emits warning
1292. [SQL fixture](2026-11-27-point-coordinates.sql)

A seeded comparison matched 1,920 generated points with SRIDs 0,
3857, 3395, 4030, 4258, 4267, 4269, and 4326 in both Fsdb modes.
Each case compared the numeric getters and exact setter geometry
bytes. The generator used seed 81234 and 240 points per SRID with
uniform coordinates and independent replacement coordinates inside
the relevant range. Binary-protocol preparation matched parameter
and result types, widths, flags, scale, and collation for all four
functions and for literal `NULL` arguments.

The numeric constructor `POINT(x,y)` remains a separate gap.
MySQL accepts `SELECT ST_AsText(POINT(1,2))`. Fsdb resolves `POINT`
as an unknown function.
