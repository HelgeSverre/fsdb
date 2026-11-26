# ST_Validate and geometry validity

Pinned MySQL 8.4.11 returns the original polygon winding and vertex
order when `ST_Validate` succeeds. It returns SQL NULL for a
well-formed geometry with invalid topology and raises 3037 for the
audited malformed binary and nongeometry values. Unknown SRIDs and
out-of-range geographic coordinates retain their spatial diagnostics.
[MySQL 8.4.11 implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/item_geofunc.cc#L3132-L3168)

Fsdb validates Cartesian polygons and multipolygons with its existing
NetTopologySuite dependency. For both `ST_Validate` and `ST_IsValid`,
a single point where a hole touches the shell can be valid. Shared
edges and contacts that disconnect the interior are invalid. Geographic
polygons retain the geodesic validity path. Line validity checks for
distinct positions use the coordinate domain of the input, with
geographic coordinates compared in radians. Only the validity check
uses normalized coordinates. The SQL result preserves the original
vertices and winding.

Both storage modes matched MySQL on all 36 [fixture](2026-11-27-spatial-validate.sql)
output and error lines and 900 seeded cases (427623). The generated
cases cover mixed winding, touching and intersecting holes, overlapping
multipolygons, and geographic collections. Binary PREPARE and EXECUTE
matched the parameter and result definitions and the little-endian
geometry bytes in SRIDs 0, 3857, and 4326. The same bytes matched in the
text protocol. A durable restart retained both tested geographic rows,
including their WKT, SRID, validity, and longitude-first SQL bytes.

MySQL also accepts a valid raw big-endian geometry and returns those
exact bytes from `ST_Validate`. Fsdb emits its canonical little-endian
encoding, so byte comparisons and later consumers that check the
original endian tag can still differ.
