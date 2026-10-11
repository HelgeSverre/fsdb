# Geographic line/polygon topology

MySQL 8.4.11 distinguishes where a geographic line lies relative to a
polygon. With EPSG 4326's latitude-longitude text order and the exterior
`POLYGON((0 0,0 3,3 3,3 0,0 0))`, these are the oracle results:

| Line | `Crosses(line, polygon)` | `Touches(line, polygon)` | `Within(line, polygon)` |
|---|---:|---:|---:|
| `LINESTRING(-1 1,4 1)` | 1 | 0 | 0 |
| `LINESTRING(1 1,2 2)` | 0 | 0 | 1 |
| `LINESTRING(4 1,5 1)` | 0 | 0 | 0 |
| `LINESTRING(-1 1,0 1)` | 0 | 1 | 0 |
| `LINESTRING(0 1,0 2)` | 0 | 1 | 0 |
| `LINESTRING(1 1,0 1)` | 0 | 0 | 1 |

`ST_Contains(polygon,line)` matches the `ST_Within` column. Reversing
`ST_Crosses` to polygon-first yields `NULL`. With a polygon containing a
hole, MySQL treats a line along the hole boundary as within the polygon,
while a line that crosses from the filled area into the hole crosses it.

Fsdb now samples each geodesic line segment, classifies the sample points
against the polygon, and uses the existing line/polygon distance path to
distinguish boundary-only touches from disjoint lines. The focused Expecto
case and differential contracts cover the relationships above. Short
interior excursions between samples and near-tangent numerical boundaries
remain limitations of this sampled approach.
