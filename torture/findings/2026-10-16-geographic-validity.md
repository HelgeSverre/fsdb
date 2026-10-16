# Geographic `ST_IsValid` for point and line shapes

Pinned MySQL 8.4.11 evaluates `ST_IsValid` on SRID 4326 point, line,
multiline, multipoint, polygon, and collection values. fsdb previously
returned 1235 for every geographic value. For example, MySQL returns `1`
for `ST_IsValid(ST_GeomFromText('LINESTRING(0 179,0 -179)',4326))` and `0`
for the same expression with `LINESTRING(0 0,0 0)`.

A differential probe compared MySQL's geographic result with fsdb's existing
coordinate-based planar validity result for 1,009 lines and multilines,
including duplicate points, closed lines, antimeridian and antipodal cases,
and 1,000 random global line shapes from seed 512. Every result matched. Explicit
point, multipoint, empty collection, and collection-of-point-and-line cases
also matched. `ST_IsValid` now uses that validation for geographic shapes
without polygon components. A focused Expecto regression pins valid and
invalid representatives, including the antimeridian and a zero-length line.

The same shortcut is unsafe for polygons: a 200-case rectangle and
bow-tie corpus from seed 116 happened to match, but 402 of 1,000 global quadrilaterals
from seed 4326 differed between MySQL's geographic validity and fsdb's
planar validity. Geographic polygons and collections containing polygons
therefore retain the existing unsupported response until geodesic validity
can be implemented and compared directly with MySQL. This change does not
claim geographic polygon validity or a complete geographic topology model.

## Polygon follow-up

A fresh 1,000-quadrilateral probe (seed 4326, latitude in `[-85,85]`, longitude
in `[-179,179]`) compared `ST_IsValid` for identical WKT at SRIDs 0 and 4326
on MySQL 8.4.11. Planar and geographic results disagreed on 417 rows: 328
were planar-invalid/geographic-valid, and 89 were planar-valid/geographic-invalid.
Longitude span alone is not a sufficient classifier: 82 disagreements occurred
among the 320 polygons whose sampled longitude span was below 180 degrees.

For the same corpus, a separate diagnostic converted each vertex to a unit
sphere and tested the two pairs of nonadjacent edges for intersection along
their shorter great-circle arcs. That predicted MySQL geographic validity in
999 of 1,000 cases. The remaining case has a 172.35-degree edge and MySQL
returns invalid where the spherical test returns valid. The result supports
geodesic self-intersection as the main missing rule, but the counterexample
means a spherical shortcut is not yet an exact replacement for MySQL's
geographic predicate. It needs ellipsoidal or otherwise oracle-matched edge
handling, plus separate hole and multipolygon validation, before enabling
geographic polygon `ST_IsValid` broadly.
