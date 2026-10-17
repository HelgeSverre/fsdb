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

A later native 8.4.11 comparison ran the same line and hole coordinates in
SRID 0 and 4326. The planar line at offsets `1.001`, `2.5`, `2.99`, `2.999`,
and `2.99999` only touches the two hole edges, and `ST_Intersection` contains
the two endpoints. Geographic lines cross through `2.99`; at `2.999` and
`2.99999` MySQL instead reports touching even though `ST_Intersection`
contains a short line beginning at the endpoint. At `2.99999` it also
contains a separate point. This confines the discrepancy to geographic
intersection construction near the corner; a planar rule, a component-count
test, or a simple point label would contradict the native result.

The maintained oracle now translates the same exterior and hole by -40, -20,
0, +20, and +40 degrees of latitude and samples lines near the opposite hole
edge. Across 372 line/polygon pairs, five comparisons differ: the original
two near-hole lines in both ring orientations, and the line at longitude
`2.99999` after a +40-degree translation. The -40, -20, and +20-degree
translations agree at all tested offsets; at +40 degrees, `2.999` agrees but
`2.99999` differs. All 39 retained point probes still agree. The location of
the crossing-to-touching transition changes with latitude, so an offset
threshold would be incorrect. This additional evidence points toward the
geographic line/ring relation rather than coordinate proximity or sampled
point labels.

A current-head replay still has the same five mismatches and all 39 point
probes agree. The maintained oracle now prints MySQL's endpoint membership and
`ST_Intersection` for each mismatch. In all five, MySQL says the line's start
point is within the polygon and its end point is outside; the intersection
contains a positive-length line beginning at the start. Nevertheless MySQL
reports `ST_Touches=1` and `ST_Crosses=0`. The original `2.999` offset yields
only a short `LINESTRING`; `2.99999` yields a short line plus a separate point
in both hole-ring orientations. The +40-degree case likewise yields a short
line and a point. An endpoint-only or mathematically exact inside/outside
rule is therefore insufficient for MySQL parity. A safe fix must reproduce
the geographic relation strategy behind this overlay, or retain the narrow
divergence; a coordinate threshold would only fit these fixtures.

The same replay exposed a separate, fixable exterior-boundary error. Points
roughly a micrometer outside the west, east, and south edges of the exterior
ring were previously classified as touching by fsdb because its Andoyer
point-to-ring distance fell below `0.00001` meter. MySQL 8.4.11 reports all
three as disjoint. Exterior-ring boundary classification now also requires
geodesic segment membership, as the hole-ring path already did. The focused
point/polygon regression pins the three cases; the Release oracle now reports
zero mismatches for four near-exterior probes, zero for 39 near-hole probes,
and the same five open line/polygon mismatches among 372 line cases.

A higher-latitude extension of the maintained oracle adds +60, +70, +80,
and +85-degree translations and samples both hole edges more densely. Across
412 line/polygon pairs, 12 now differ; the original point corpora still have
zero mismatches. Six added cases are the same `ST_Crosses=1` versus
`ST_Touches=1` split seen near the lower-latitude hole corners. At +85 degrees,
`LINESTRING(86 1.00001,88 1.00001)` has a different discrepancy: MySQL reports
all five relations false, including `ST_Intersects`, while its own
`ST_Intersection` returns `POINT(86 1)`; fsdb reports crossing and intersecting.
This native inconsistency and the latitude-dependent transition make an
offset-based patch especially unsafe. The line/ring geographic overlay remains
open; the expanded oracle is a guard for a future general fix.

## High-latitude point follow-up

The maintained oracle now checks another 150 points around translated holes
at +40, +60, +70, +80, and +85 degrees latitude. Across 189 near-hole point
cases, one differs: MySQL 8.4.11 reports `POINT(86 1.00001)` outside and
disjoint from the +85-degree polygon with a hole, while fsdb reports it
inside and intersecting. This is the start point of the near-polar line for
which MySQL reports every relation false even though `ST_Intersection`
returns `POINT(86 1)`. The 412 line cases still have 12 mismatches, and the
four near-exterior point controls still agree.

Reducing fsdb's geographic ring interpolation step from 25 km to 5 km left
the point and line mismatch counts unchanged; the more expensive sampling
was reverted. This rules out coarse ring sampling as the sole cause on this
corpus. A local coordinate exception would fit the sample but would not
explain MySQL's overlay and relation behavior, so the point remains an
explicit open spatial edge case.

A component probe further narrows the near-polar point discrepancy. For
`POINT(86 1.00001)` under SRID 4326, MySQL and fsdb both say the point is
within the exterior ring considered as its own polygon and outside the hole
considered as its own polygon; neither says it touches either component.
For the combined polygon, MySQL says it is outside while fsdb says it is
inside. MySQL reports zero distance to the hole ring at this point, while
fsdb reports about 0.09536 m. The maintained oracle now prints all three
component results. This is evidence of a geographic ring-composition or
numeric-boundary strategy difference: replacing the combined classifier
with independent `outer within` and `not hole within` tests would preserve
fsdb's wrong answer, and treating zero line distance as boundary would
contradict MySQL's `ST_Touches=0` for the hole polygon.

## Geographic winding strategy boundary

MySQL 8.4.11's [point/polygon `Within` implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/within.cc#L661-L669)
passes the complete polygon to Boost.Geometry with its ellipsoidal
`geographic_winding` strategy. MySQL constructs that strategy from the SRS
spheroid in [`within.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/within.cc#L53-L59),
and the pinned source names [Boost 1.84.0](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/cmake/boost.cmake#L24).
Boost's [winding strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/spherical/point_in_poly_winding.hpp)
counts longitude and antipodal crossings, with special handling around the
poles, and its [geographic side test](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/side.hpp)
uses Andoyer azimuths. Fsdb instead sums spherical angles along sampled
ellipsoidal segments in `geographicRingContains`, then composes exterior and
hole classifications.

This source difference is a plausible cause of the near-polar point result,
not a proof of which Boost branch decides that point. The next behavior
change should compare a faithful winding/side implementation against the
entire point corpus before replacing fsdb's classifier. The line/polygon
relation differences also involve MySQL's geographic segment-intersection
strategy, so a point-only winding change cannot be claimed to close them.

## Near-machine-precision hole-edge offsets

A separate MySQL 8.4.11 probe placed `LINESTRING(1.5 y,3.0 y)` beside the
`0.5`-longitude hole edge of
`POLYGON((0 0,0 4,4 4,4 0,0 0),(0.5 0.5,3.5 0.5,3.5 3.5,0.5 3.5,0.5 0.5))`
in SRID 4326. At `y=0.5`, both engines report touching. At
`y=0.500000000000001` and `y=0.50000000000001`, MySQL reports
`ST_Intersects=1` but neither crossing nor touching; fsdb reports no
intersection. At `y=0.5000000000001` through `0.50000001`, both report
disjointness. On the other side, MySQL reports intersection without `Within`
at `y=0.499999999999999` and `0.49999999999999`, while fsdb reports `Within=1`.
The engines agree on `Within=1` by `y=0.4999999999999`.

Before the focused fix, fsdb also reported `ST_Crosses=1` and
`ST_Intersects=0` at `y=0.500000000000001`. Crossing now requires a computed
intersection, so that internal contradiction is gone. The finer MySQL
boundary classifications still differ. MySQL's `ST_Distance` jumps from zero
at the two closest positive offsets to about 1.2488642 metres at
`y=0.5000000000001`; fsdb reports about 0.09504187 metres across those
positive offsets. This is a floating-point/geographic strategy boundary,
not evidence for applying a broad coordinate tolerance to topology.
