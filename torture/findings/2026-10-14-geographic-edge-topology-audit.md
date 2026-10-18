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

## Geographic strategy follow-up

The [MySQL 8.4.11 intersects functor](https://dev.mysql.com/doc/dev/mysql-server/8.4.11/intersects__functor_8h_source.html)
declares Boost's `geographic_winding` strategy for point/area relations and
`geographic_segments` for line/area relations. The
[bundled Boost 1.84 geographic winding implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/geographic/point_in_poly_winding.hpp)
uses a longitude-crossing winding counter with an Andoyer-azimuth geographic
side strategy, rather than the sampled spherical-angle sum in fsdb's
`geographicRingContains`. This difference is a plausible cause of the
latitude-sensitive point result, but the current evidence does not prove it
explains the line relations too.
The winding counter treats segments crossing the query longitude and its
antimeridian separately, and the geographic side test compares the Andoyer
azimuth from a segment start to the query with the azimuth along the segment.
That side test is applied at a crossing, not at each sampled arc point. A
replacement therefore needs the crossing/count state and azimuth side rule
together; changing only the side calculation inside fsdb's angle sum would
still be a different algorithm. The standalone-hole/combined-polygon results
below also require validating polygon composition after a ring-level port.

The [bundled winding base implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/spherical/point_in_poly_winding.hpp)
checks aligned endpoints and meridian segments before counting crossings.
It gives a crossing at an endpoint a signed count of one, an interior
longitude crossing a signed count of two, and keeps separate ordinary and
antipodal counts for pole-wrap decisions. Its aligned-endpoint side check
constructs a short east/west segment before applying the geographic azimuth
strategy. A direct port therefore needs those count and touch states as well
as the side formula; merely replacing fsdb's sampled arc angles with one
side test would miss the endpoint and pole cases. This is the upstream Boost
implementation, not yet a proof of which branch of MySQL's full polygon
relation produces the observed near-polar combined-ring result.

The maintained 412-line oracle now records `ST_Distance` alongside each
mismatch's relation and intersection geometry. MySQL and fsdb return zero
distance for all 12 mismatches. In the near-polar case, MySQL still reports
`ST_Intersects=0` while returning both zero distance and an intersection
point. Distance and overlay output therefore cannot safely substitute for
MySQL's relation strategy. The next candidate fix should reproduce the
geographic winding and segment-relation decisions, then pass the entire
line/point corpus; changing only a tolerance or sampled step is unsupported.

A follow-up after fixing `ST_DISTANCE_SPHERE` tested whether MySQL's stored
geographic angular unit (`0.017453292519943278` radians per degree for EPSG
4326), rather than `pi / 180`, also explains this point/ring discrepancy.
Using that unit in fsdb's spherical ring vectors left the differential oracle
unchanged: 12 line mismatches among 412 pairs, one near-hole point mismatch
among 189, and zero near-exterior control mismatches. The trial was reverted.
The relation gap still requires the geographic winding/overlay strategy rather
than this coordinate-conversion adjustment alone.

The near-polar point has a further ring-composition wrinkle. MySQL 8.4.11
reports `POINT(86 1.00001)` inside the standalone exterior polygon and
outside the standalone hole polygon, yet outside their combined polygon.
At `POINT(86 2.99999)`, it reports the point inside both standalone polygons
and inside the combined polygon. Reversing either standalone hole or the
combined hole ring leaves those results unchanged. MySQL calls all five
polygons valid. Fsdb agrees on the exterior and on the first standalone-hole
result, but not on the combined result; it also differs on the second
standalone-hole result while matching the combined result. The maintained
oracle now pins MySQL's validity and point relations for these isolated and
combined rings. This rules out fixing the polar discrepancy by changing only
the standalone hole-ring membership test; the full polygon relation must be
validated as a unit.

The native oracle also pins `ST_Distance(point, polygon)=0` for both polar
points against the exterior, the standalone hole in either ring direction,
and the combined polygon in either direction. That includes cases where
MySQL reports the point outside and `ST_Intersects=0`. Fsdb likewise returns
zero for most of these cases but reports about 0.09536 m for the outside
point against one standalone-hole ring direction. Distance therefore has a
separate ring-order sensitivity and cannot be used to infer the missing
containment or intersection decision.

An initial F# translation of the longitude-crossing counter in MySQL 8.4.11's
[bundled Boost 1.84 winding strategy](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/spherical/point_in_poly_winding.hpp),
using fsdb's current Andoyer azimuth helper for its geographic side test,
increased the near-hole relation mismatches from one to two of 189. It made
the combined-polygon `POINT(88 1.00001)` result diverge while leaving the 12 line
and 34 point-distance mismatches unchanged. The trial was reverted. The
remaining mismatch is not resolved by a counter translation alone: the
geographic side calculation and MySQL's combined-ring relation path both
need independent checks against the bundled implementation.

A trial taking the minimum of forward and reverse point-to-segment distances
removed that 0.09536 m polar discrepancy without changing the 12 line or
one point relation mismatches. It failed an existing MySQL point-to-line
regression for `POINT(-20 100)` against `LINESTRING(-10 90,-30 110)`, whose
native distance is about 71,223.12146 m. The trial was reverted. Globally
symmetrizing the segment distance would change other MySQL-compatible cases;
the polar ring-order result needs a narrower explanation.

A broader point-distance replay now compares all 189 near-hole points in the
maintained oracle with a `0.01` metre tolerance. The unchanged implementation
has 34 distance mismatches, alongside the previously known one relation
mismatch; the line relation corpus remains at 12 of 412. A polygon-only trial
that took the minimum of each ring's forward and reversed segment distances
still had 34 point-distance mismatches. It removed the standalone polar-ring
`0.09536` metre error, but at `(73, 2.99999)` and `(83, 2.99999)` it changed
nonzero distances to zero while MySQL reports approximately `0.31618` and
`0.13485` metres. That trial was also reverted. The expanded distance corpus
now exposes these tradeoffs before another geographic strategy change.

A 49-point grid around the combined polar polygon found only the known
`POINT(86 1.00001)` disagreement. A finer 35-point corner grid found four
disagreements, all at latitude 86 and longitudes `1.000001`, `1.000002`,
`1.000005`, and `1.00001`. MySQL calls each outside and disjoint; fsdb calls
each inside and intersecting. The adjacent sampled longitudes `1.00002`
through `1.0001`, and latitudes `85.9999`, `85.99999`, `86.00001`, and
`86.0001`, agree. This confines the sampled divergence to a narrow polar
corner band, but the standalone/combined relation contradiction above still
precludes a simple hole-membership or tolerance patch. The maintained
`geographic-edge-oracle.py` reports both grids for future strategy work.

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
