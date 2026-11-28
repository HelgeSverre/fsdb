# Polygon area

`ST_Area` accepts POLYGON and MULTIPOLYGON, including MySQL binary
geometry values. It returns DOUBLE and preserves NULL. Other valid
geometry types, including empty collections, return 3516. Malformed
input returns 3037, an unknown SRID returns 3548, and a nonfinite area
returns 1690. A prepared call advertises a GEOMETRY argument and a
nullable DOUBLE result.

Cartesian and projected polygons use their coordinate units squared.
Geographic polygons return square meters using the SRS ellipsoid.
The calculation walks the exterior and holes in the directions used
by MySQL's area strategy. In geographic area, reversing the walk can
change the result because the first-order edge approximation depends
on direction. Polar rings use the pole-crossing adjustment.
[MySQL 8.4 area contract](https://dev.mysql.com/doc/refman/8.4/en/gis-polygon-property-functions.html#function_st-area)
[MySQL 8.4.11 area implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/area.cc)
[Boost 1.84 geographic strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategy/geographic/area.hpp)

```sql
SELECT ST_Area(ST_GeomFromText(
  'POLYGON((0 0,10 0,10 20,0 20,0 0),(1 1,3 1,3 3,1 3,1 1))'));
SELECT ST_Area(ST_GeomFromText(
  'POLYGON((179 0,-179 0,-179 1,179 1,179 0))',4326,'axis-order=long-lat'));
```

Pinned MySQL 8.4.11 returned 196 and approximately 24,619,443,710.34 for
these two queries. The differential run covered 414 results, including
392 geographic probes across EPSG 4326, 4258, 4267, and 4030. Fsdb's
memory and WAL modes agreed throughout. Cartesian values, diagnostics,
and prepared metadata matched MySQL. The largest geographic difference
was about 0.713 square meters on a 475-square-meter Clarke 1866 polygon.

A stored generated column using `ST_Area(shape)` matched the MySQL
indexed range result and NULL handling for WGS 84 parcels. The same
query matched after terminating and reopening the WAL-backed server.

## Area of geographic overlay results

In a 36-pair WGS 84 quadrilateral sample, 15 symmetric differences
returned a different geometry type. In one equatorial pair, both
engines measured the directed differences at about 64.59 and 22.51
billion square meters. MySQL's forward symmetric difference returned
only the latter polygon. Reversing the operands returned both polygons
on MySQL, as both orders did on Fsdb. The existing geographic overlay
gap therefore affects area as well as the reported geometry type.

```sql
SET @a = ST_GeomFromText('POLYGON((
  1.232678937691 -0.499506348621, 4.232678937691 -0.399506348621,
  3.632678937691 2.500493651379, 0.732678937691 1.200493651379,
  1.232678937691 -0.499506348621))',4326,'axis-order=long-lat');
SET @b = ST_GeomFromText('POLYGON((
  2.632678937691 0.800493651379, 5.232678937691 -0.649506348621,
  4.332678937691 2.100493651379, 2.532678937691 1.700493651379,
  2.632678937691 0.800493651379))',4326,'axis-order=long-lat');
SELECT ROUND(ST_Area(ST_SymDifference(@a,@b))),
       ROUND(ST_Area(ST_SymDifference(@b,@a)));
```

### Where symmetric difference diverges

For the same inputs, compare the internal union of the directed differences
with an explicit SQL union. The latter first serializes each intermediate
geometry, which can change the coordinates used by the union.

```sql
SET @ab = ST_Difference(@a,@b);
SET @ba = ST_Difference(@b,@a);
WITH results AS (
  SELECT 1 AS n, 'A minus B' AS operation, @ab AS g
  UNION ALL SELECT 2, 'B minus A', @ba
  UNION ALL SELECT 3, 'XOR A, B', ST_SymDifference(@a,@b)
  UNION ALL SELECT 4, 'XOR B, A', ST_SymDifference(@b,@a)
)
SELECT operation, ST_GeometryType(g), ROUND(ST_Area(g)) FROM results ORDER BY n;

SELECT ST_GeometryType(g), ROUND(ST_Area(g)) FROM (SELECT ST_Union(@ab,@ba) AS g) AS u;
SELECT ST_GeometryType(g), ROUND(ST_Area(g)) FROM (SELECT ST_Union(@ba,@ab) AS g) AS u;
```

| Operation | MySQL type and rounded area (m²) | Fsdb type and rounded area (m²) |
| --- | --- | --- |
| A minus B | POLYGON, 64,588,256,568 | POLYGON, 64,588,256,568 |
| B minus A | POLYGON, 22,506,879,148 | POLYGON, 22,506,879,148 |
| XOR A, B | POLYGON, 22,506,879,148 | MULTIPOLYGON, 87,095,135,715 |
| XOR B, A | MULTIPOLYGON, 87,095,135,715 | MULTIPOLYGON, 87,095,135,715 |
| Union AB, BA | POLYGON, 110,776,053,411 | MULTIPOLYGON, 87,095,135,715 |
| Union BA, AB | POLYGON, 110,776,053,411 | MULTIPOLYGON, 87,095,135,715 |

Across the 36 pairs, Fsdb matched MySQL on type and rounded area within
1 m² for all 72 directed differences and 44 of 72 symmetric differences.
The pinned Linux MySQL explicit unions matched the corresponding symmetric
differences in 63 of 72 cases. Native macOS MySQL 8.4.11 and the pinned Linux
build agreed in both operand orders for the four pairs where swapping
operands changed the result. Their symmetric differences differed in both
directions for 11 other pairs.

On macOS, Fsdb initially returned error 1235 for ten explicit unions. In
the fixture above, two endpoints from opposite subtractions were equal within Boost's
radian tolerance, and the geodesic solver introduced a false interior
crossing. The union now recognizes rounded endpoint contacts before solving
for a crossing and gives both output rings the same coordinate at the
contact. All ten previously rejected outputs now pass `ST_IsValid` in both
engines, including a round trip through MySQL. Three of the ten also match
MySQL on type and area, raising the explicit union total from 43 to 46 of
72. The other seven still differ in type or area. For one of the recovered
orders, MySQL's own union output fails its validity check.
[Boost geographic endpoint handling](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp)

An ARM64 Linux Fsdb replay reduced explicit union errors from 14 to two.
All 12 recovered results passed Fsdb's validity check. Type and area
matches against pinned MySQL rose from 45 to 46 of 72.

A separate run varied offsets around geographic rectangle contacts at four
latitudes and checked both operand orders. Of 160 explicit unions, matches
on type, rounded area, and validity rose from 92 to 98. In the six changed
cases, the type and area stayed the same and the result became valid,
matching MySQL. The 82 direct union controls retained their type and area.

Feeding both engines the same intermediate WKT still reproduces the
explicit union mismatch in the equatorial fixture above, even when MySQL
generated the directed differences. For the polar fixture
`-72/-175/2`, MySQL produces two lobes from its own serialized differences
and one lobe from Fsdb's, while Fsdb produces two lobes from either. The
directed differences can agree on type and area while their vertex
coordinates still affect a later union.

MySQL delegates polygon symmetric difference to Boost Geometry, which
unions the two intermediate multipolygons with their solver coordinates.
A standalone ARM64 Linux replay using Boost Geometry 1.84 and GCC
`-ffp-contract=off` matched the pinned MySQL build on all 72 symmetric
differences. The same replay with `-ffp-contract=fast` matched 54 of 72.
An Apple Clang build matched the native MySQL build on all 72.
[MySQL polygon dispatch](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/symdifference_functor.cc#L683-L690)
[Boost's directed differences and final union](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/sym_difference.hpp#L105-L147)

The Boost traversal trace for the SQL fixture produces one new ring in
both operand orders. In the forward order, finalized turns mark both input
rings as traversed, so only the new ring survives. In reverse, the larger
input ring is eligible for selection because its turns remain unfinalized,
and Boost returns it alongside the new ring. A parity implementation
needs to keep the intermediate coordinates and follow the union's turn
traversal and ring selection. The four raw equatorial cuts from Fsdb on
macOS matched the pinned Linux Boost replay bit for bit. Two change when
converted to degrees and back, as do 29 of the 144 cuts across the sample.
Raw Fsdb cuts matched 141 of 144 native macOS Boost cuts on macOS. On
ARM64 Linux, they matched 138 of 144 with GCC contraction enabled and 88
of 144 with it disabled. The pinned MySQL behavior follows the replay
with contraction disabled. A scratch build using separate multiplication
and addition in the crossing solver and its azimuth calculation matched
all 144 uncontracted Linux cuts. That comparison covered the raw solver
output. Full SQL overlay parity also requires the union traversal.
[Boost overlay traversal and ring selection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/overlay/overlay.hpp)
