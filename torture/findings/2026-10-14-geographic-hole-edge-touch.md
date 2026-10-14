# Geographic line on a polygon hole edge

For SRID 4326, MySQL 8.4.11 returns `ST_Crosses=0`, `ST_Touches=1`,
`ST_Within=0`, and `ST_Contains=0` for this line and polygon:

```sql
LINESTRING(1.5 0.5,3.0 0.5)
POLYGON((0 0,0 4,4 4,4 0,0 0),
        (0.5 0.5,3.5 0.5,3.5 3.5,0.5 3.5,0.5 0.5))
```

The line follows part of the hole boundary. FSDB previously reported
`ST_Crosses=1` and `ST_Touches=0` because sampled geodesic positions near the
edge could fall on different sides of the ring. The line/polygon topology path
now identifies segments whose endpoints lie on the same boundary segment and
classifies a line made entirely of those segments as touching. This exact
boundary check does not widen the tolerance for nearby parallel lines.

The Expecto spatial regression includes this case alongside exterior boundary,
interior, hole crossing, and disjoint lines. Broader near-tangent and unsampled
geodesic cases remain open.
