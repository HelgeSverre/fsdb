# Geographic line topology oracle

MySQL 8.4.11 distinguishes interior crossing, boundary contact, and
collinear overlap for geographic linework in EPSG 4326. The following
queries use MySQL's latitude-longitude axis order. Each result lists
`ST_Intersects`, `ST_Crosses`, `ST_Touches`, `ST_Overlaps`,
`ST_Contains`, `ST_Within`, and `ST_Equals`, in that order.

| Relationship | First geometry | Second geometry | MySQL result |
|---|---|---|---|
| Interior crossing | `LINESTRING(0 -1,0 1)` | `LINESTRING(-1 0,1 0)` | `1,1,0,0,0,0,0` |
| Shared endpoint | `LINESTRING(0 -1,0 0)` | `LINESTRING(0 0,1 0)` | `1,0,1,0,0,0,0` |
| T-junction | `LINESTRING(0 -1,0 1)` | `LINESTRING(-1 0,0 0)` | `1,0,1,0,0,0,0` |
| Collinear overlap | `LINESTRING(0 -1,0 1)` | `LINESTRING(0 0,0 2)` | `1,0,0,1,0,0,0` |
| Identical | `LINESTRING(0 -1,0 1)` | `LINESTRING(0 -1,0 1)` | `1,0,0,0,1,1,1` |
| Disjoint | `LINESTRING(0 -1,0 1)` | `LINESTRING(2 -1,2 1)` | `0,0,0,0,0,0,0` |

Reversing the interior-crossing operands still gives `ST_Crosses=1` and
`ST_Touches=0`. `MULTILINESTRING((0 -1,0 1),(5 5,6 6))` crosses the same
horizontal line. MySQL also reports an interior crossing for the two
antimeridian-spanning lines `LINESTRING(-1 179,1 -179)` and
`LINESTRING(-1 -179,1 179)`.

Fsdb currently computes line/line geographic `ST_Intersects` and
`ST_Disjoint` through its distance path, but returns `NULL` for these
other defined predicates. A topology implementation needs to distinguish
the interior/boundary relationship of the original geodesic members,
including multipart linework and longitude wrap. Planar longitude-latitude
segments alone are insufficient for the antimeridian example.
