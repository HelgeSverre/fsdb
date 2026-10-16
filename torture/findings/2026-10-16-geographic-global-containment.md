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
`POINT(5 5)` inside and `POINT(-5 -175)` outside. Fsdb places both inside.
The existing tangent-plane winding test winds around both a query point and
its antipode, so this error is not confined to large global rings. A
prototype meridian-crossing replacement fixed these examples but failed the
existing polar-cap regression (`POINT(85 0)` inside
`POLYGON((80 -120,80 0,80 120,80 -120))`) and depended on ring direction
where MySQL did not. It was reverted rather than broadening the regression.

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
