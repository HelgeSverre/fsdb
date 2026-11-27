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

Both servers return the original bytes for valid raw big-endian and
mixed-endian geometries, including nested collections.

```sql
SELECT HEX(ST_Validate(X'0000000000000000013FF00000000000004000000000000000'));
```

The audited coordinate and SRID accessors read the decoded geometry.
`ST_AsText`, `ST_AsWKB`, and `ST_GeometryType` report 3055 for a
big-endian outer header and 3037 for a little-endian header containing
big-endian members. Both servers return 1416 when a geometry column or
a materialized derived table attempts to store these results. An explicit
binary cast preserves the original bytes for BLOB storage.
[MySQL geometry column validation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/field.cc#L7050-L7069)

Both fsdb storage modes matched 216 text-protocol cases and 27 additional
big-endian or mixed-endian multi-geometry comparisons. A prepared
statement matched result metadata and bytes for nine BLOB parameters
tested individually in both modes.
Six values cast to BLOB matched MySQL before and after a durable restart.
Two differences remain. Direct equality against a binary literal still
differs for canonical little-endian multi-geometries. MySQL returns 1210
for an execute packet that declares a `MYSQL_TYPE_GEOMETRY` parameter,
which fsdb accepts.
