# Global geographic polygon containment counterexamples

The geographic `ST_IsValid` follow-up tested holes and multipolygons against
the pinned MySQL 8.4.11 oracle before enabling them. Twelve handpicked
contacts and 200 seeded local rectangle configurations matched MySQL:
single-vertex hole/shell and component/component contacts can be valid,
whereas shared edges, crossings, nested holes, and overlapping filled
components were invalid in that matrix. Antimeridian and polar examples also
matched.

A separate 100-case global quadrilateral corpus (seed 9173, with a small
triangle near a shell vertex) found two cases where a candidate fsdb validator
returned invalid but MySQL returned valid. Both are multipolygons whose
components are disjoint in MySQL; fsdb's existing geographic `ST_Intersects`
and point-in-polygon path says they overlap. One observed pair is:

```sql
SET @a = ST_GeomFromText('POLYGON((55.095784 43.215101,-1.549061 -106.976186,25.691447 176.152400,-77.605773 8.784222,55.095784 43.215101))',4326);
SET @b = ST_GeomFromText('POLYGON((21.909158 172.938139,28.962876 174.095155,26.699045 174.007825,21.909158 172.938139))',4326);
SELECT ST_IsValid(@a), ST_IsValid(@b), ST_Intersects(@a,@b), ST_Within(@b,@a);
```

MySQL returns `(1,1,0,0)`; fsdb returns `(1,1,1,1)`. Reversing the
first ring does not change either server's result. MySQL considers
`POINT(0 0)` and `POINT(-20 0)` inside `@a`, and `POINT(80 0)` outside;
fsdb returns the opposite for those three points. The current spherical
winding predicate in `GeometryOperations.fs` is therefore not a reliable
containment oracle for some large global rings. The mismatch is not limited
to `ST_IsValid`: it affects `ST_Within` and `ST_Intersects` directly.

Further differential testing found a simpler antipodal counterexample. For
`POLYGON((0 0,0 10,10 10,10 0,0 0))` in SRID 4326, MySQL places
`POINT(5 5)` inside and `POINT(-5 -175)` outside. Fsdb previously placed
both inside. The tangent-plane winding test also winds around a query
point's antipode. A hemisphere guard now excludes that false positive when
the sampled ring fits wholly inside an open hemisphere; both ring directions
match MySQL's `ST_Within` and `ST_Intersects` results. The large global-ring
cases above still require a different containment strategy. A prototype
meridian-crossing replacement fixed those examples but failed the existing
polar-cap regression (`POINT(85 0)` inside
`POLYGON((80 -120,80 0,80 120,80 -120))`) and depended on ring direction
where MySQL did not. It was reverted rather than broadening the regression.

A second candidate used a signed spherical triangle area to choose between a
ring's tangent-plane winding and its complementary region. It explained the
first global example and the polar cap, but did not match MySQL generally.
Among 226 MySQL-valid random global quadrilaterals (seed 9173, 12 point
probes each), it disagreed on 155 of 2,712 point results. For example, MySQL
reports the following polygon valid and the point inside, while the simple
spherical winding around the point is zero:

```sql
SET @a = ST_GeomFromText('POLYGON((-49.39793223 39.25611065,18.32056422 148.75405757,-69.02455409 -117.92274540,55.83993074 -88.64082883,-49.39793223 39.25611065))',4326);
SET @p = ST_GeomFromText('POINT(47.61723855 -61.46703001)',4326);
SELECT ST_IsValid(@a), ST_Within(@p,@a);
-- 1, 1
```

The spherical candidate was not added to fsdb. A full solution needs to
follow MySQL's geographic side and point-in-polygon decisions for global
geodesic edges, not infer them from a small-region spherical area.

The second observed pair is:

```sql
SET @a = ST_GeomFromText('POLYGON((-63.962352 -175.602231,47.774240 -160.567110,-32.437052 70.954473,10.638088 -84.992182,-63.962352 -175.602231))',4326);
SET @b = ST_GeomFromText('POLYGON((12.728225 -85.708396,9.138578 -86.807402,11.084715 -87.604848,12.728225 -85.708396))',4326);
```

MySQL returns valid and disjoint for this multipolygon; fsdb's current
geographic intersection result is true. The candidate hole/multipolygon
validity extension was not retained. `ST_IsValid` for simple single-ring
geographic polygons remains supported; holes and multipolygons retain the
explicit unsupported response until containment and component separation
match MySQL on both local and global shapes.

## MySQL strategy identified

MySQL 8.4.11 normalizes geographic polygon rings on WKB construction with
Boost.Geometry's geographic area strategy, then evaluates point containment
with `strategy::within::geographic_winding<Geographic_point>`. A standalone
probe built against the pinned Boost 1.84 headers, using the WGS 84 spheroid,
`boost::geometry::correct(polygon, geographic_area)`, and
`boost::geometry::within(point, polygon, geographic_winding)`, reproduced the
MySQL answers for the small square, polar cap, first global polygon, and
second global polygon above. In particular, the first global polygon returned
inside for `(0,0)` and `(-20,0)`, outside for `(80,0)` and the triangle vertex;
the second returned inside for `(47.61723855,-61.46703001)`. The small square
excluded its antipodal point, and the polar cap included `(85,0)`.

This establishes a concrete reference algorithm for the recorded cases. The
remaining work is a faithful F# implementation of ring normalization and
geographic winding, followed by differential testing over the 2,712-query
global corpus and local edge/antimeridian/polar regressions. The probe alone
does not establish parity for all polygons, and no production containment
change was made in this investigation.

Source trail: MySQL [`wkb.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/wkb.cc)
and [`ring_flip_visitor.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/ring_flip_visitor.cc)
perform correction; [`within.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/within.cc)
selects the geographic winding strategy. Boost 1.84 implements that strategy
in [`point_in_poly_winding.hpp`](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/spherical/point_in_poly_winding.hpp).
