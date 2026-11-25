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

A separate longitude-precision control used WGS 84 points `(3, 0)` and
`(3.00000000000001, 0)` in longitude-latitude order. Both engines returned
`POINT(3 0)` for their union. MySQL returned 1 for `ST_Equals`, and Fsdb
returned 0. The point-only predicate path still uses planar comparison.
