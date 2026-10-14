# Geographic edge topology audit

MySQL 8.4.11 is the oracle for EPSG 4326. A native-server differential probe
compared `ST_Crosses`, `ST_Touches`, `ST_Within`, `ST_Contains`, and
`ST_Intersects` for 337 line/polygon pairs. The corpus varied lines along and
near the edges of two squares and a polygon with a hole, plus a multipart
polygon where an edge overlap precedes an interior crossing. The probe is
[`geographic-edge-oracle.py`](../scripts/geographic-edge-oracle.py).

FSDB previously treated a line that extended past a polygon's exterior edge
as crossing because interpolated geodesic samples drifted from the shared
coordinate edge. A bounded overlap interval now preserves boundary contact
only on the common exterior-edge portion. The multipart case confirms that
the later interior crossing still counts. MySQL also reports a point exactly
on a hole's meridian edge as touching; the point classifier now records that
case separately from other near-hole samples that MySQL treats as filled area.

The original corpus agreed on 335 of 337 line/polygon pairs. Its remaining
disagreements were these near-hole lines, for which MySQL reports crossing and
FSDB reported touching:

```sql
SET @area = ST_GeomFromText(
  'POLYGON((0 0,0 4,4 4,4 0,0 0),(1 1,3 1,3 3,1 3,1 1))',4326);
SELECT ST_Crosses(ST_GeomFromText('LINESTRING(1 1.00001,3 1.00001)',4326),@area),
       ST_Touches(ST_GeomFromText('LINESTRING(1 1.00001,3 1.00001)',4326),@area);
```

The same difference occurs at longitude `1.001`. Separate point probes also
find near-corner differences, including `POINT(3 1.00001)` and offsets only
`0.0000001` degree from a hole corner. This audit is a synthetic boundary
corpus, not a general geographic parity claim.

A narrower MySQL 8.4.11 probe shows why classifying the line solely from its
endpoints is insufficient. `POINT(1 1.00001)` is within the polygon, while
`POINT(3 1.00001)` is outside it; the same within/outside split occurs for
`POINT(1 2.999)` and `POINT(3 2.999)`. Yet every tested line from latitude
`1` to `1.000001`, `1.0001`, `1.01`, `1.1`, `2`, or `3` crosses beside longitude
`1.00001` and only touches beside longitude `2.999`. In fsdb, geographic
point-to-hole-edge distance can round to zero for both near-corner endpoints
even though the exact point-on-line check says they are off the edge. Treating
those rounded distances as exact boundary contact explains the first pair of
mismatches; replacing the distance check alone shifts the mismatches to the
opposite edge. A fix needs line/ring topology at the corner, not a wider or
narrower distance tolerance.

A follow-up sampled 39 points along and just beyond both sides of the hole
while replaying the 337 line cases. One point differs: `POINT(3 1.00001)` is
outside the polygon in MySQL but inside it in fsdb. The line classifier's
internal samples for `LINESTRING(1 1.001,3 1.001)` and the opposite-side
`LINESTRING(1 2.999,3 2.999)` both start as `HoleBoundary` and continue as
`Outside`; MySQL nevertheless calls the first crossing and the second
touching. Point predicates treat `HoleBoundary` as interior, but promoting
that label throughout line topology would turn the opposite-side case into
a false crossing. Reducing geodesic sampling from 25 km to 5 km left the
classification unchanged and made the focused test substantially slower, so
the sampling change was discarded. The remaining fix needs an oriented
line/ring intersection rule rather than a point-label or sampling shortcut.

The point classifier now checks exact geodesic segment membership before
calling a point a hole-boundary point. This fixes the near-corner point and
agrees with MySQL on all 39 sampled points. It leaves two line mismatches in
the 337-case corpus: lines at longitude `2.999` and `2.99999` now report
crossing in fsdb while MySQL reports touching. Reversing the line and the hole
ring preserves the MySQL relation. The expanded 345-case probe records four
mismatches because it includes both ring orientations of those two lines.
This is still an open line/ring topology gap.

A further diagnostic checked the existing line sampler and the spherical
proper-segment intersection test on the near-hole lines. For both the crossing
line at longitude `1.001` and the touching line at longitude `2.999`, the
sampler saw one interior endpoint followed by outside samples, and the
proper-segment test reported an intersection. Adding geodesic samples at
0.1%, 1%, 99%, and 99.9% of each line did not change any of the four
mismatches in the 345-case corpus. Neither an endpoint-transition shortcut nor
the current spherical intersection predicate distinguishes MySQL's two
relations. That probe was reverted; the remaining work needs a more precise
line/ring relation near the hole corner.

A later MySQL-only probe split the disputed line into subsegments and inspected
`ST_AsText(ST_Intersection(line, polygon))`. For longitude `1.001`, MySQL
reports a short `LINESTRING` from the starting point plus a separate `POINT`
near latitude `1.00000031`, and calls the line crossing. At longitude `2.999`,
it reports only the short `LINESTRING` and calls the line touching. Both lines
start at a point that `ST_Within` calls interior; their middle subsegments do
not intersect the polygon. Reversing the hole ring does not change the result.
This suggests the disagreement lies in the near-corner line/ring intersection
construction, rather than endpoint classification alone.

The result is not explained merely by whether `ST_Intersection` contains a
separate point: longitude `2.99999` produces a `GEOMETRYCOLLECTION` containing
a short line and a point, yet MySQL still calls it touching. Offsets from
`1.00001` through `2.99` in the same probe were crossings; `2.999` and
`2.99999` were touches. A threshold or component-count special case would
encode this sample without establishing a general geographic topology rule.
