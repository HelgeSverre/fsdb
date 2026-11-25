# Geographic point-set overlays

The MySQL oracle was the digest-pinned 8.4.11 image in
`torture/compose.yaml`. Fsdb `f934a479` returned 1235 for geographic
`ST_Intersection`, `ST_Union`, `ST_Difference`, and `ST_SymDifference`.

The functions now handle geographic points and multipoints, including
combinations with linework or polygonal areas. Membership uses the
existing geodesic line and polygon classifiers, so a covered shell or
hole boundary point participates in an intersection. Adjacent
coordinates compare in radians at the audited MySQL tolerance, while
different longitudes at the pole retain distinct point identities.

The observed output rules include first-occurrence ordering for union,
the smaller input's order for multipoint intersection, and collapse to
a point or empty geometry collection when appropriate. In a mixed
collection, union expands a multiline or multipolygon into members,
while symmetric difference retains it as one multipart member. The
result carries the input SRID and the existing axis-order handling.
[MySQL 8.4.11 intersection dispatcher](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc).

The wire comparison matched all four results for 120 pairs across
EPSG 4326, 4258, and 4267. It used 72 seeded point-set pairs and 48
point-set pairs with linework, polygons, or an empty geometry.
Sixteen additional queries checked both argument orders and output shapes.
The focused regression covers duplicate points, near-equal coordinates,
the antimeridian, and polygon shell and hole boundaries. Geographic
overlays between two nonempty higher-dimensional inputs and arbitrary
nonempty geometry collections remain open.

## Point-set predicates

WGS 84 points `(3, 0)` and `(3.00000000000001, 0)` compare equal in
longitude-latitude order. The point-only predicates now use the same
radian tolerance as the overlays. Geographic empty operands return
`NULL` except in `ST_Equals`, which tests whether both operands are empty.

For two multipoints, MySQL's `ST_Equals` and `ST_Overlaps` use Boost's
sorted lower-bound lookup. Chains of near-equal coordinates can make
that lookup differ from the pairwise membership used by `ST_Within`
and `ST_Contains`. A 17-member regression also distinguishes the
pinned GNU library's partition and insertion order from .NET's sort.
Fsdb bounds the partition and insertion scans and falls back to exact
ordering if the partition depth limit is reached. Other sorting-library
implementations and depth-limit cases remain outside the audit.
[MySQL equality](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/equals.cc)
· [MySQL overlap](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/overlaps.cc)
· [Boost 1.84 point relations](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/relate/point_point.hpp).

Both engines returned identical results for eight predicates over 950
ordered inputs across EPSG 4326, 4258, and 4267. The corpus includes
duplicates, near-equality chains, points at the poles and antimeridian,
and multipoints with up to 850 generated members. Three additional
controls checked longitude identity at the pole and antimeridian.
