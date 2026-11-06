# Residual geographic edge and vertex distances

The Release CLI and disposable native MySQL 8.4.11 were compared with
`torture/scripts/geographic-edge-oracle.py` after the reduced-length fix.
The baseline still has 12/412 line-relation, 1/189 near-hole point-relation,
and 8/189 near-hole point-distance mismatches. The seeded point-line distance
set (100 cases) and polar-corner set (100 cases) match.

All eight mismatching polygon distances equal distances to the hole line in
both engines. Five also show a direct point-to-hole-vertex difference of at
least 0.01 m (MySQL first, fsdb second):

| Point | Vertex | MySQL (m) | fsdb (m) |
| --- | --- | ---: | ---: |
| `43 2.99999` | `43 3` | 0.8188541524346334 | 0.8077127604662425 |
| `73 2.99999` | `73 3` | 0.31618400687144826 | 0.33024342460117184 |
| `83 2.99999` | `83 3` | 0.13485314242182217 | 0.16516069457216623 |
| `88 1.00001` | `88 1` | 0.09535991608716497 | 0 |
| `88 2.99999` | `88 3` | 0.09535991608716497 | 0 |

Thus point-to-point geographic distance is part of the remaining problem;
the polygon dispatcher alone cannot account for every mismatch. The
stand-alone Boost 1.84 cross-track probe, using the same version bundled by
MySQL 8.4.11, now accepts six optional coordinates. For the 43-degree
segment it returns 0.1648719135648529 m, agreeing with MySQL's line
distance. At 88 degrees its raw strategy result is 2.4535297088 m while
MySQL's complete line-distance result is 0.0953599161 m. The raw strategy
is therefore useful for tracing arithmetic, but cannot substitute for the
complete SQL operation as an oracle.

The probe also reports Boost's direct geographic point-to-point strategy for
the query and each segment endpoint. At the 43-degree vertex Boost returns
0.8188541524346334 m, matching MySQL rather than fsdb. At 83 degrees it
returns 0.1651606945721662 m and at 88 degrees it returns zero, matching
fsdb rather than MySQL. The same values result from Clang `-O0`, `-O3`, and
`-O3 -ffp-contract=fast` builds. This rules out a simple uniform choice of
the local Boost strategy as an explanation for all residuals. The exact
coordinate conversion and build arithmetic still need tracing.

MySQL 8.4.11's
[`sql/gis/distance_functor.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/distance_functor.cc)
constructs `distance::andoyer`
for point pairs and `geographic_cross_track<andoyer>` for other geographic
pairs. Its
[`Geographic_point`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/geometries.h)
coordinates are stored in radians. The native
probe now also constructs radian-coordinate Boost points explicitly; all
three controls above remain unchanged. Strategy selection and a basic
degree-versus-radian Boost coordinate type difference therefore do not
explain the residuals. MySQL's WKT conversion and Linux build arithmetic
remain to be checked before changing fsdb's point-distance formula.

Four candidate arithmetic changes were built in Release and run through the
full edge oracle. All were reverted:

| Trial | Near-hole point-distance mismatches | Result |
| --- | ---: | --- |
| Baseline | 8/189 | Reference |
| Fuse reduced-length subtractions | 8/189 | Identical output |
| Subtract converted longitudes | 8/189 | Identical output |
| Fuse point-distance cosine | 9/189 | New mismatch at `62 2.99999` |
| Multiply coordinates by radians-per-degree | 10/189 | New mismatches at `86.00001 1.00001` and `86.00001 2.99999` |

The next production change needs a more complete trace of MySQL's selected
endpoint/interior strategy and its floating-point operations. The test now
prints point-to-vertex controls for the residuals so that any fix can be
checked against both polygon and primitive distances.

## SRS conversion and fused arithmetic follow-up

MySQL's 4326 WKT definition specifies an angular unit of
`0.017453292519943278` radians per degree. Its WKB parser multiplies each
coordinate by that value before the Boost distance strategy sees it. Using
that unit in the native probe reproduces MySQL's 73°, 83°, and 88° vertex
distances. With Clang's floating-point contraction disabled, the 83° native
probe instead follows fsdb's smaller value. The discriminating expression is
the Andoyer cosine sum: fusing its sine product with the cosine product
reproduces MySQL's rounded result.

fsdb now uses the SRS angular unit for each point-to-point coordinate and
the corresponding fused Andoyer cosine sum. The five near-vertex regression
cases match MySQL, and all five vertex diagnostics in the edge oracle agree.
The full oracle falls from 8 to 7 near-hole point-distance mismatches, with
the other baseline counts unchanged. Applying the SRS unit only to the current
cross-track entry coordinates increased the near-hole distance count to 9;
that incomplete trial was reverted. Six of the remaining seven polygon
distances equal hole-line distances in both engines and point to the segment
strategy. In the seventh (`88 2.99999`), the hole-line distances agree at
0.09535991608716497 m, but fsdb classifies the point as inside the polygon
and returns zero. That case is a polygon topology residual.

The native Boost probe can now run cross-track directly on SRS-radian points.
For representative 43°, 71°, 72°, and 86° residual segments, this returns
the MySQL line distance, including MySQL's zero result at `86.00001 1.00001`.
This makes the native strategy a useful arithmetic reference for the six
segment cases, while the separate polygon classification must be addressed
for the seventh.

## Radian-native Newton distances

The cross-track routine previously converted the spherical projected point
and each Thomas direct point from radians to degrees, then back to radians
inside its Andoyer distance helper. Keeping both calls in radians reduces
the full near-hole polygon-distance oracle from 7 to 5 mismatches. The
`43 2.99999` and `86.00001 1.00001` polygon cases now agree with MySQL.
The remaining five comprise four hole-segment distance differences (the two
`71.00001` points, `72 2.99999`, and `88 1.00001`) plus the `88 2.99999`
polygon classification difference. The line oracle still reports 12/412
relation mismatches; the point relation and seeded point-line controls are
unchanged.

The 71° trace shows fsdb's first Newton distance at 0.3566792078 m and its
next at 0.3691980567 m. Native Boost with SRS-radian input begins at
0.3691980556 m, which MySQL returns. The initial spherical position and
first direct point still differ enough to change the selected minimum.
Changing all cross-track entry coordinates to SRS radians did not improve
the full oracle, so that trial was reverted. A standalone `POINT(43 2.99999)`
to `LINESTRING(43 1,43 3)` control also remains different even though the
polygon case agrees; the polygon result must not be used to claim general
point-line parity.

## Spherical projection trial

The 71° first-iteration trace separates the initialization error from the
later Newton update. Boost's SRS-radian trace starts with spherical along-track
distance 1.111190703481076 m and returns 0.3691980556077172 m. In the
pre-radian-native fsdb trace, the first projected position was
1.1149777346723868 m along the segment and its first distance was
0.35667920777369744 m. Its second iteration was approximately
0.3691980567 m, but the lower first value remained selected. The oracle
still reports 0.3566792078 m after the radian-native change; its new
intermediate position has not yet been captured.

Replacing fsdb's spherical projection formula with Boost's `spherical_direct`
formula resolved both `71.00001` cases and `72 2.99999`, but introduced
`72 1.00001` and both `86.00001` cases. The full count stayed at 5/189.
Combining that projection with SRS-radian entry coordinates also stayed at
5/189 while shifting failures again, including the formerly matching
`3 1.00001` control. Both trials were reverted. The next attempt needs to
match the spherical initial guess, direct point, and Andoyer inverse as one
path, then verify the full edge grid before replacing any portion.

## Hole-orientation containment trial

The remaining `88 2.99999` polygon distance is zero because fsdb classifies
the point as inside the combined polygon, while MySQL excludes it from the
hole. A trial evaluated hole containment with the exterior-ring orientation
instead of the corrected hole orientation. It fixed that one classification
and reduced near-hole distance differences from 5/189 to 4/189. The complete
oracle also showed point-relation differences rising from 1/189 to 3/189,
line-relation differences from 12/412 to 13/412, and polar-corner differences
from 1/49 to 3/49. The trial was reverted. A blanket role change is therefore
incorrect; the remaining corner needs a closer look at the winding side
calculation and its rounded azimuths.

## Fused Andoyer side azimuth

The `88 2.99999` trace isolated the hole's upper edge. Fsdb rounded the
query-to-edge cosine sum to exactly 1, assigned the short query segment
azimuth 0, and cancelled the two ring crossings. A Boost 1.84 side probe
with MySQL's SRID 4326 angular unit produced cosine
`0.99999999999999989`, azimuth `-1.57079623930712`, and the opposite side
for that edge. Compiling the probe with floating-point contraction disabled
reproduced fsdb's old azimuth and side. Fusing the sine product into the
cosine sum in fsdb's Andoyer inverse matches the optimized Boost result.

The full edge oracle now reports 0/189 near-hole point-relation mismatches
and 4/189 near-hole point-distance mismatches. The remaining four polygon
distances equal their corresponding hole-line distances and are segment
distance residuals. Line relations remain at 12/412 mismatches; the 100
seeded point-line distances, 49 polar-corner points, and four near-exterior
controls still match. The focused near-polar regression now includes
`88 2.99999`; 32 geometry and 33 geographic tests pass.

## Line/polygon relation strategy control

The 12 remaining line-relation mismatches comprise 11 `Crosses`/`Touches`
disagreements and one near-polar `Touches`/disjoint disagreement. Fsdb's
line/polygon topology currently classifies sampled points along each line;
the point-relation grid itself has no mismatches. The
[native Boost 1.84 probe](../native/geographic-line-polygon-relate-probe.cpp)
checks the geographic relation directly. With MySQL's SRID 4326 angular unit
and radian-coordinate points, it returns `Crosses=0, Touches=1,
Intersects=1` for the 0° and 40° near-hole controls and `0,0,0` for the
85° polar control, matching MySQL in all three. Using Boost's generic degree
conversion instead gives a different polar result. The remaining work is
therefore in the line/polygon relation strategy and coordinate conversion;
point sampling alone is not a sufficient parity test for that path.

## Comparable-haversine initialization trial

An integrated cross-track trial replaced fsdb's spherical initial guess with
Boost 1.84's comparable-haversine cross-track calculation, spherical-direct
projection, and MySQL SRID 4326 radian coordinates. The full edge oracle
still found 12/412 line-relation mismatches, 0/189 point-relation mismatches,
and 4/189 near-hole polygon-distance mismatches. It fixed the old
`88 1.00001` distance case but introduced `3 1.00001`; the two
`71.00001` cases and `72 2.99999` remained. The 100 seeded point-line
distances and polar-corner controls still matched. The trial was reverted.
Matching only the spherical initialization is insufficient; the next trace
should compare the first Thomas direct point and Andoyer inverse result from
the same coordinate and arithmetic path.

## Denser line sampling control

The current-head edge oracle on `a6f1de86` still reports 12/412 line-relation
and 4/189 near-hole point-distance mismatches, with zero point-relation,
polar-corner, and seeded point-line-distance mismatches. A temporary trial
reduced the line interpolation spacing in `sampleGeographicSegment` from
25 km to 5 km, retaining the same 256-step cap. The full oracle returned
the same counts in every lane. The trial was reverted because it increased
the number of sampled positions without closing any counted gap. The line
relation work should use the Boost geographic segment strategy rather than
assuming that a denser point grid resolves these cases.

## Current-head first-step and isolated spherical formula

Temporary instrumentation on `a160d577` confirms the 71° distance case
diverges before Newton refinement. For `POINT(71.00001 1.00001)` against
the hole edge from `(71 1)` to `(73 1)`, fsdb's spherical projection seeds
the along-track position at `1.1149777346692951` m and its first direct
point yields `0.35667920777372397` m. The pinned Boost 1.84 probe with
MySQL's SRID 4326 angular unit seeds at `1.111190703481076` m and its first
direct point yields MySQL's `0.3691980556077172` m. The instrumentation
was removed after the trace.

An isolated trial replaced only fsdb's spherical cross-track distance with
Boost's comparable-haversine calculation. It kept the existing projection,
direct point, and Newton steps. The full oracle still had 12/412 line
relations and 4/189 near-hole distances mismatching, with the same four
distance values; the 100 seeded point-line cases still matched. This trial
was reverted. The earlier combined projection trial and this isolated
formula trial both leave the first direct point and inverse calculation as
the next arithmetic comparison.

## Line-interior versus endpoint control

The current-head edge oracle still has 12/412 line/polygon relation
mismatches. The native Boost 1.84 probe now prints the DE-9IM relation
matrix for seven near-hole controls. Six return `F01FF0212`: the line's
interior meets the polygon boundary and exterior, but not its interior.
They agree with MySQL's `Touches=1, Crosses=0`. The 85-degree polar control
returns `FF1FF0212`, disjoint, also agreeing with MySQL. Fsdb's point
sampling labels the six as crossing and the polar control as touching.

A temporary topology trial excluded line endpoints from the sampled
interior/exterior evidence. The full oracle worsened from 12 to 70 relation
mismatches out of 412. MySQL reports crossing for numerous lines whose
interior enters the polygon for a very short distance near an endpoint;
the fixed samples miss that portion. The trial was reverted and Release
rebuilt from unchanged production code. Both endpoint-only sampling and
denser fixed sampling are inadequate. The next implementation needs to
classify geographic segment/boundary intersections and the intervals
between them, including portions near endpoints that the samples miss.

## Interpolated-point and proper-crossing control

For the first remaining `LINESTRING(1 2.999,3 2.999)` near-hole mismatch,
`ST_LineInterpolatePoint` at fractions 1/9 through 8/9 yields points that
both engines classify outside the polygon. Both classify the start point
inside. Nevertheless MySQL's complete line/polygon relation is `Touches=1,
Crosses=0`, while fsdb's sampled relation is `Touches=0, Crosses=1`.
MySQL's intersection is only a tiny line near the start. The point
predicate at that endpoint therefore cannot stand in for the line's
interior/boundary relation.

A temporary trial excluded endpoints from interior evidence and added
`geographicSegmentsProperlyCross` against each polygon boundary as crossing
evidence. The full oracle worsened from 12 to 15 line-relation mismatches
out of 412; the point and distance lanes stayed at 0/189 relations and
4/189 near-hole distances. The trial and diagnostic printout were removed,
and Release was rebuilt from unchanged production geometry code. The next
implementation needs the geographic relation's ordered intersection events
and segment intervals, rather than an endpoint classification plus a binary
proper-crossing flag.

## Zero-distance polar disjointness

The 85° polar control has a zero MySQL and fsdb line/polygon distance, but
MySQL's DE-9IM matrix is `FF1FF0212`: the geometries are disjoint. Fsdb
previously inferred `ST_Intersects=1` and `ST_Touches=1` from distance zero.
Line/polygon intersection and disjointness now use the same sampled contact
result as the relation predicates, while retaining the distance check where
contact is seen. This makes the polar control `Intersects=0`, `Disjoint=1`,
`Touches=0`, with `Distance=0` on both engines. The full native oracle falls
from 12 to 11 line-relation mismatches out of 412; its 0/189 point-relation,
4/189 near-hole distance, 0/49 polar-corner, and 0/100 seeded point-line
distance counts are unchanged. All 33 geographic and 22 geometry tests pass.
The 11 near-hole line disagreements still require ordered boundary events.

## Short corner crossings between fixed samples

A broader oracle added 36 lines whose endpoints are outside a polygon but
whose path clips a very short interval near one exterior corner. The cases
use four latitude bands (0°, 40°, 70°, and 85°), three line spans, and
three corner offsets. A guard based only on fixed sampled contact made seven
of the nine equatorial cases appear disjoint even when MySQL reported crossing;
distance was zero in both engines. A binary segment-crossing flag found
contact but labelled a representative line as touching.

Fsdb now obtains ordered candidate boundary fractions from planar segment
crossings, then classifies geographic midpoints in the intervals between
them. This recovers interior evidence for 34 of the 36 corner cases without
changing the original 412-case mismatch set or the polar disjoint result.
The expanded native oracle reports **13/448 line-relation mismatches**:
the former 11 near-hole cases and two very narrow corner clips at the
equator. The point-relation and point-distance counts remain 0/189 and
4/189, with 0/49 polar-corner and 0/100 seeded point-line distance
differences. All 33 geographic and 22 geometry tests pass. The planar
fractions are candidates, not a complete ellipsoidal relation strategy;
the two narrow misses and other segment orientations remain open.

## Geographic boundary-fraction refinement

The two remaining corner clips were not missing boundary events. Their
interior interval lay a few millionths of the segment beyond the planar
candidate fractions. For the equatorial 0.1° clip, MySQL and fsdb agree
that the interpolated point at fraction 0.50001 is inside the polygon,
while the point at 0.5 is outside. A longer 1° clip has the same shifted
interval. The earlier planar fractions sampled before that interior sliver.

Fsdb now bisects the coordinate-side change along its geographic
interpolated line, checks that the event lies within the candidate boundary
segment, and classifies the intervals between refined events. Wrapped
longitude segments remain on the existing path until they have an
unwrapped event frame. The expanded native oracle now reports **11/448
line-relation mismatches**, all from the prior near-hole set; all 36 added
corner cases match MySQL. Point relations remain 0/189, near-hole distances
4/189, polar corners 0/49, and seeded point-line distances 0/100. Focused
regressions pin both formerly missed clips; all 33 geographic and 22
geometry tests pass. Ellipsoidal boundary intersections, including the
remaining near-hole touch/cross distinctions, remain open.

## Endpoint-exclusion and midpoint trial

On the expanded 448-case oracle, a temporary trial excluded line endpoints
from interior evidence and classified every segment midpoint, including
segments without detected boundary events. It increased line-relation
mismatches from 11 to 69. MySQL's intersections can contain very short
interior portions adjacent to an endpoint; the midpoint does not detect
those portions. The trial was reverted. Any endpoint treatment needs an
ordered geographic intersection event and interval classification that can
distinguish a boundary-only endpoint from a short adjacent interior arc.

## Radian-native segment endpoints

The cross-track Newton loop previously converted its segment endpoints with
the generic radians-per-degree constant, then recalculated the Thomas direct
azimuth from degree coordinates on every iteration. MySQL supplies both the
inverse and direct strategies with coordinates already converted by SRID
4326's angular unit. Fsdb now passes those SRS-radian endpoints through a
radian-native direct helper. The existing public degree-coordinate path uses
the same helper through a wrapper.

The complete Release edge oracle falls from four to **one** near-hole
point-distance mismatch across 189 points. Both `71.00001` corner points,
`72 2.99999`, and `88 1.00001` now match MySQL. The sole remaining case is
`72 1.00001`: MySQL returns 0.3302323744957476 m and fsdb returns
0.34371391572221144 m; both match their respective hole-line distances.
The 448 line relations still have 11 differences; 189 point relations, 49
polar-corner points, and 100 seeded point-line distances have none. All 33
geographic and 22 geometry tests pass.

A further trial substituted Boost's comparable-haversine cross-track and
spherical-direct seed into this radian-native path. It increased the
near-hole count to two, introducing `88 1.00001` again, and was reverted.
The remaining point-distance case needs a first-point/Newton trace on this
new coordinate path. The line-relation differences still require ordered
geographic boundary events.

## Reduced-length operation order

The last `72 1.00001` trace had the same first direct-point distance as
Boost, but its reduced length changed the first Newton update. Boost groups
the two multiplicative terms before subtracting them; fsdb multiplied each
term left to right. Those nearly equal terms cancel at this point. Matching
Boost's grouping lets the iteration reach MySQL's 0.3302323744957476 m
minimum. The regression pins the primitive segment distance.

The complete Release oracle now has **0/189 near-hole point-distance**,
0/189 point-relation, 0/49 polar-corner, and 0/100 seeded point-line-distance
mismatches. The 11/448 line/polygon relation residuals remain. All 33
geographic and 22 geometry tests pass.
