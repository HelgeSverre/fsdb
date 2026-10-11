# Geographic point/line topology

Status: implemented for geographic `POINT` against `LINESTRING` and
`MULTILINESTRING`, in either argument order, for `ST_INTERSECTS`,
`ST_DISJOINT`, `ST_CONTAINS`, `ST_WITHIN`, `ST_TOUCHES`, and `ST_EQUALS`.

MySQL 8.4.11 treats a point in the line interior as intersecting and
contained, an unshared endpoint as touching but not contained, and a point
outside the line as disjoint. A shared endpoint of two multiline components
and the start/end point of a closed line are interior. MySQL also distinguishes
a point 0.000011057302444142153 metres from an equatorial line from a point
on that line, even though near-coincident numeric distance may round to zero.

Fsdb compares Andoyer azimuths at machine precision and classifies component
endpoints by parity. The audited cases include equatorial and meridional
segments, an antimeridian crossing, multiline shared endpoints, a closed line,
and a point returned by line interpolation that MySQL regards as off the
curved segment. This follows the [Boost geographic side strategy](https://github.com/boostorg/geometry/blob/boost-1.85.0/include/boost/geometry/strategies/geographic/side.hpp)
for segment membership. Broader curved-geodesic and near-boundary parity
needs further differential testing.

Oracle example:

```sql
SELECT ST_Contains(
  ST_GeomFromText('MULTILINESTRING((0 0,0 1),(0 1,0 2))',4326),
  ST_GeomFromText('POINT(0 1)',4326));
```
