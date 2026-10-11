# Geographic point/polygon topology

Status: implemented for geographic `POINT`/`MULTIPOINT` against `POLYGON` and
`MULTIPOLYGON`, in either argument order, for `ST_INTERSECTS`, `ST_DISJOINT`,
`ST_CONTAINS`, `ST_WITHIN`, `ST_TOUCHES`, and `ST_EQUALS`.

MySQL 8.4.11 treats a point in the filled area as intersecting and contained,
a point outside or inside a hole as disjoint, and a point on the exterior ring
as touching but not contained. An audited point on a hole ring is contained
and does not touch. Fsdb classifies exterior and hole boundaries separately,
using the same geographic ring sampling as its distance calculation. Cases
also cover curved edges, antimeridian crossings, multipolygons, and reversed
argument order. For `MULTIPOINT`, any interior or exterior-boundary member
intersects, containment requires every member to be covered and at least one
interior member, and touching requires an exterior-boundary member with no
interior member. An outside member does not prevent touching. Near-boundary
sampling remains a precision limitation.

Oracle example:

```sql
SET @p=ST_GeomFromText(
  'POLYGON((0 0,0 3,3 3,3 0,0 0),(1 1,1 2,2 2,2 1,1 1))',4326);
SELECT ST_Contains(@p,ST_GeomFromText('POINT(1 1.5)',4326)),
       ST_Touches(@p,ST_GeomFromText('POINT(0 1)',4326));
```
