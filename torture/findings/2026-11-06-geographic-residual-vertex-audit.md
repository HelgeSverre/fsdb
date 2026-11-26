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

## Endpoint point/line relation disagreement

The first residual line, `LINESTRING(1 2.999,3 2.999)`, illustrates why
sampling more points or discarding only the endpoint is insufficient. MySQL
reports `ST_Within=1` for the line's start point and for interpolated points
at fractions `10⁻⁹`, `10⁻⁸`, and `10⁻⁷`. Both MySQL and fsdb report those
points as inside the polygon and report the point at fraction `10⁻⁶` outside.
Yet MySQL reports `ST_Touches(line, polygon)=1` and
`ST_Crosses(line, polygon)=0`. Its `ST_Intersection` is a short line from
`(1, 2.999)` to approximately `(1.000000309934808, 2.99900000000118)`.
Fsdb calls the complete line a crossing because it promotes those point
predicates to line-interior evidence.

All 11 current relation mismatches have the same direction: MySQL says
touching and fsdb says crossing, with MySQL's intersection confined to a
tiny arc next to an endpoint. The point predicate and line overlay strategy
are numerically inconsistent at this scale. A line-level geographic
intersection and interval classification is needed; a blanket endpoint
exclusion or denser point grid has already regressed the broader oracle.

The native Boost 1.84 relation probe now covers all nine distinct line and
latitude shapes behind the eleven mismatches (the first two also occur with
the hole winding reversed). Each returns `F01FF0212`, `Crosses=0`,
`Touches=1`, and `Intersects=1`, matching MySQL 8.4.11. Its 85° polar
control returns `FF1FF0212` and remains disjoint. The common relation
matrix confirms that the residual is in line/polygon relation construction,
not in the point predicates or the distance calculation. The production
topology logic is unchanged by this probe expansion.

## Incoming side of the first line-area turn

The [native relation probe](../native/geographic-line-polygon-relate-probe.cpp)
also reports whether the source fraction is zero and whether a sole
turn arrives from inside. All nine distinct residuals have a zero source
fraction and fail Boost's incoming-side test. The crossing controls at
longitudes `1.00001` and `1.001` in the 0° base-latitude fixture also
have a zero source fraction, and both pass that test. A zero fraction alone therefore cannot classify the contact.
The `2.99` crossing control has a nonzero fraction and passes. The
polar disjoint control produces no turns.

For these cases the first-turn decision compares the original source
start against azimuths formed by the directed polygon edge and the
computed intersection. This provides a control for an event-based
line relation. The point-in-polygon results and the intersection WKT
still follow their respective strategies.
[Boost 1.84 line/area relation](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/relate/linear_areal.hpp#L235-L320).

## Directed proper turns in line/area predicates

For a two-vertex line with one proper turn against a polygon, the line
relation checks the incoming interval against that directed turn. A
near-start exit whose fraction rounds to zero reports a touch when the
incoming side is outside. A proper entry with an exterior incoming side
can establish a crossing when sampling misses the small interval beside
a corner. The intersection strategy orders each segment by latitude.
For a descending line, the source distance is the ordered segment length
minus the ordered distance to the turn. Computing that distance directly
from the original start can round a genuine crossing to zero.

Native MySQL 8.4.11 and Release Fsdb matched all 448 structured line/area
cases in each direction. The preceding revision disagreed on 11 forward
and four reversed cases. MySQL changed its result when direction reversed
in 13 cases. The point relation and distance lanes continued to match.
A wider seeded set of 1,000 EPSG 4326 relations improved from 20
differences to 13. Reversing that set reduced differences from 21 to 9.
The forward fixtures in EPSG 4258 and EPSG 4267 each improved from 21 to 14.
Each sampled difference in the candidate also occurred in the baseline.
The remaining cases include near-endpoint classifications where Boost
rejects a turn and cases where the two engines differ on its fraction.
[Boost 1.84 geographic segment strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

The structured reverse and seeded checks use `--reverse-lines` and
`--seeded-relations`. The extra reference systems use `--srid 4258`
and `--srid 4267` with the seeded mode.
[Geographic edge oracle](../scripts/geographic-edge-oracle.py).

## Rounded endpoints that reject a turn

The geographic segment strategy can round the normalized inverse
distance between nearby endpoints to zero. It then checks the
endpoint's azimuth against the latitude-ordered segment before accepting
a turn. If the endpoint faces away from the segment, no turn is
generated. For a simple line with this rejected contact and no other
turn or shared endpoint, the line/area relation follows the first
point's area position. Reversing the line can change the result from
disjoint to within even when the overlay returns a contact geometry.
[Boost geographic segment intersection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp)
· [Boost line/area relation](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/relate/linear_areal.hpp).

For a real turn, the incoming-side calculation uses paired sine and
cosine evaluations on the SRS radians. At one audited exit, separate
evaluations rounded the angular cosine to one and erased the short
arc's azimuth. The paired evaluation retained the one-bit difference
and correctly classified that line as a crossing. A sole exit reached
from outside is a touch even when its directed fraction is nonzero.

Native MySQL 8.4.11 and Release Fsdb still agree on all 448 structured
fixtures in both directions. Of the 1,000 seeded cases per lane, the
differences fell from 13 to 0 in forward EPSG 4326 and from 9 to 0
in reverse. The EPSG 4258 and 4267 forward sets each fell from 14 to 2.
A separate 5,000-case run using seed `615801` fell from 64 to 13.
Every remaining difference also occurred in the previous build.
Five of the 13 wider residuals produce two touch-interior turns in
the native probe. Those paired contacts reach the sampled classifier.

## Rounded contact pairs along one edge

A two-vertex line can have a rounded contact at a polygon corner and
a second rounded contact at the other end of the line. The segment
strategy orders source endpoints by latitude and checks each rounded
contact against the directed boundary. When the contacts straddle the
transverse edges at opposite vertices of one boundary span, the
line/area relation records a crossing. The sampled point positions can
all lie inside in this case. Nearby controls with both endpoints on the
same side of a transverse edge remain contained.
[Boost linear/areal turn dispatch](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/overlay/get_turn_info_la.hpp).

The wider 5,000-case EPSG 4326 set fell from 13 to 8 differences forward
and from 14 to 9 reversed, with no new mismatching cases. A 70-case
longitude offset sweep reduced differences from 30 to 2. Its remaining
cases combine a proper crossing turn with a rounded endpoint contact.
All structured cases and both original seeded EPSG 4326 runs still
match. The EPSG 4258 and 4267 seeded sets retain two differences each.

The wider fixture uses `--seeded-relations --seed 615801 --seeded-polygons
2500` on the [geographic edge oracle](../scripts/geographic-edge-oracle.py).
Add `--reverse-lines` for the reversed fixture.

## Proper turn paired with a rounded endpoint

The paired-contact case can also have a proper turn at the low end of the
latitude-ordered source and a rounded contact at the high endpoint. The
proper turn's distance from the low endpoint rounds to zero. Fsdb evaluates
the two turns in source order. A proper exit followed by a terminal rounded
contact reports a touch when the exit's incoming side is exterior. Reversing
the source makes the rounded contact occur first, followed by a proper entry
that establishes an interior interval. Other proper turns or shared
endpoints leave the case with the existing classifier.
[Boost line/area turn analysis](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/relate/linear_areal.hpp).

The 70-case longitude offset sweep now matches MySQL 8.4.11 throughout.
A denser sweep covers the same five fixtures and both source directions,
using 81 positions from −0.000002° to +0.000002° in steps of 0.00000005°.
Its differences fell from 41 to one, with no new mismatching cases. The
remaining case produces a sole proper turn in the native probe. The
structured and original seeded checks retain their previous counts, as
do the wider 5,000-case runs in both directions. A separate 6,000-case
holdout with `--seed 311041 --seeded-polygons 3000` fell from 17 to 15
differences. The remaining differences also occurred in the baseline.

## Reduced-latitude tangent and rounded turn rejection

The last disagreement in the dense sweep was a crossing that Fsdb called
a touch. The Sjöberg solver reconstructed the reduced-latitude tangent
from its angle when calculating the starting arc. Reusing the original
tangent matched Boost's cut coordinates and incoming azimuth in that case.
[Boost Sjöberg solver](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/formulas/sjoberg_intersection.hpp).

The tangent correction alone exposed a second problem. The line relation
accepted some calculated cuts where a rounded source endpoint lay behind
the ordered boundary segment. Boost checks rounded endpoint contact before
solving a proper turn. Fsdb now applies its rounded endpoint rejection to
calculated relation turns as well. With no accepted turn, the line relation
uses the area position at the start of the source, as MySQL does in these
fixtures. The overlay can still report a point in those cases.
[Boost geographic segment intersection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

Native MySQL 8.4.11 supplied the expected line relations.

| Corpus | Before | After |
| --- | ---: | ---: |
| Dense WGS 84 offsets | 1/810 | 0/810 |
| Wider WGS 84, forward | 8/5,000 | 0/5,000 |
| Wider WGS 84, reverse | 9/5,000 | 0/5,000 |
| Independent WGS 84 holdout | 15/6,000 | 7/6,000 |
| ETRS89 seeded lines | 2/1,000 | 0/1,000 |
| NAD27 seeded lines | 2/1,000 | 0/1,000 |

The full mismatch signatures introduced by the tangent-only trial
disappeared with the endpoint rule. All the after-state holdout differences
were present before the change. The structured and original seeded WGS 84
sets still match in both directions. Reversing the independent holdout
produced six differences in 6,000 cases.

In the forward holdout, MySQL reports a crossing in all seven residuals.
Fsdb reports containment for five, a touch for one, and disjointness for
one. The disjoint case already yields two proper candidate cuts. The
single-turn refinement leaves its sampled result in place.

## Interior interval between two proper turns

The disjoint holdout fixture crosses near an exterior corner. Both
endpoints are outside, and sampled points miss the short inside interval.
The relation can now use proper boundary entry and exit cuts ordered along
the input segment. Both cuts must lie strictly inside the segment and off
polygon vertices, with positive separation between entry and exit.
[MySQL's object-shape predicates](https://dev.mysql.com/doc/refman/8.4/en/spatial-relation-functions-object-shapes.html).

MySQL 8.4.11 and Fsdb matched all 1,764 focused cases, including 1,556
crossings. The sweep shifts both line latitudes from −0.0001° through
+0.0021° in 0.000005° increments, uses both source directions, and varies
the shell's starting vertex and the hole's winding. The independent
6,000-case holdout dropped from seven to six forward differences and
from six to five reverse differences, with no new mismatching cases.
The wider 5,000-case sweep still matches in each direction.

Two of the remaining geometries disagree in both directions. In an
isolated endpoint calculation, paired sine and cosine preserved the
nonzero separation seen in the native turn trace. Separate sine and
cosine rounded the endpoints together and triggered turn rejection.

## Near-coincident endpoints and signed cut distances

Paired sine and cosine in the Andoyer inverse retain the nonzero endpoint
separation for both geometries. The proper cuts themselves can still have
an inverse distance that rounds to zero. The segment check now tests the
signed distance against the segment bounds, so a negative zero remains
on the segment. A nonzero distance behind the segment still falls outside
the bounds. [Boost geographic segment intersection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

The two geometries and all 484 nearby longitude-offset cases match
MySQL 8.4.11. The offset sweep covers both source directions and 121
positions per geometry, from −0.0000006° through +0.0000006° in
0.00000001° steps. The independent holdout drops from six to four forward
differences and from five to three reversed, with no new differences.
The wider WGS 84, dense offset, and short-corner sweeps still match.
Global line pairs, shared endpoints, rings, and polygon-validity probes
also match.

## Proper cuts and rounded polygon vertices

A proper cut and a rounded endpoint contact on opposite sides of one
boundary span can be distinct turns even when the inverse distance from
the other source endpoint is nonzero. Fsdb now checks separation from
the rounded contact and evaluates the two turns in source order.

When a cut and the first latitude-ordered source endpoint both round
to the end of the directed ring edge, Fsdb uses the next distinct
vertex on that ring to classify the incoming side. The check follows
the latitude-ordered endpoint comparison. A trial without the
source-order check classified several descending touches as crossings.
[Boost line/area turn analysis](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/relate/linear_areal.hpp).

The independent seed 311041 now matches native MySQL 8.4.11 in all
6,000 cases in each direction, closing four forward and three reverse
differences. The wider 5,000-case sweep still matches in each direction.
A fresh seed, 2468137, matches in all 7,000 cases in each direction.
The rounded-corner sweep matches all 968 cases across both line
directions and four hole-ring starting vertices. It varies source
longitude over the same 121 offsets as the near-coincident sweep.

## Current-head overlay check

A current-head rerun still finds 11/448 geographic line/polygon relation
differences, all `Crosses` versus `Touches`; the point-relation and distance
lanes remain at zero mismatches. The edge oracle's new
`--compare-overlays` option asks both engines for
`ST_AsText(ST_Intersection(line, polygon))` on every mismatching line. MySQL
returns a short endpoint-adjacent line or a
line-plus-point collection. Fsdb instead returns error 1235, `ST_INTERSECTION
with nonzero SRIDs`, in all eleven cases. Thus fsdb has no geographic overlay
geometry to use as a line-relation authority today.

The next implementation seam is a geographic line/polygon overlay that
produces ordered boundary events and inside intervals. Its intersection
geometry can serve `ST_Intersection` and the line relation predicates from
the same topology decision. Treating the sampled point predicate as that
authority is contradicted by the pinned MySQL endpoint trace above.

## Boundary-event extraction

The coordinate-side bisection is now a named `geographicBoundaryEvents`
operation. Each ordered candidate retains its line fraction, interpolated
point, and polygon boundary segment. The existing line relation still uses
the fractions to classify intervals, so this is a behavior-preserving step
toward a geographic overlay rather than a topology fix. A Release rerun of
the complete native MySQL 8.4.11 edge oracle retained 11/448 line/polygon
relation mismatches, with zero differences in 189 near-hole point relations,
189 near-hole distances, 49 polar-corner points, and 100 seeded point-line
distances. All eleven relation mismatches remain `Crosses` versus `Touches`.

These coordinate-side candidates still miss wrapped-longitude segments and
do not by themselves reproduce Boost's endpoint-adjacent intersection
geometry. The next change must classify ordered geographic intersection
events, including endpoint contact and interior intervals, then use that
same result for `ST_Intersection` and line relations.

## Endpoint-contact classification trial

The first residual starts at a coordinate-aligned hole edge, while the
standalone point predicate calls that endpoint interior. Two temporary
line-level overrides tested whether endpoint classification alone could
resolve the relation mismatch. Marking an endpoint as boundary only when
`geographicPointOnLineSegment` accepted it left the oracle at **11/448**
mismatches. Using an exact coordinate-side point-on-segment check instead
raised the count to **70/448**. The first new mismatch is a short
endpoint-adjacent intersection that MySQL classifies as crossing; the
override incorrectly removed its interior evidence. Point-relation and
distance lanes stayed at zero mismatches in both trials.

Both overrides were reverted, and the Release CLI was rebuilt from the
committed source. Endpoint labels cannot replace the ordered overlay: MySQL
can return a tiny line plus a separate point near the same endpoint, and the
line relation depends on that intersection geometry.

## Geographic line and area overlays

`ST_Intersection`, `ST_Difference`, `ST_Union`, and
`ST_SymDifference` accept geographic line strings and multilines
paired with polygons and multipolygons. Geodesic edge crossings split
each line into intervals. Shared boundary spans count as covered, and
the output retains the source direction and vertices. Intersection
returns covered fragments and isolated contacts, while difference
retains the outside fragments. Union and symmetric difference combine
the outside fragments with the oriented area.

MySQL derives the linear and point intersections separately. The two
passes can traverse a level polygon edge in opposite directions. The
Sjöberg solution can then place a point beside the output line instead
of at its exact endpoint. Fsdb preserves those separate coordinates.
The signed side checks also allow proper crossings whose inverse
distance rounds to a segment endpoint. [MySQL intersection dispatcher](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc)
· [Boost geographic segment ratios](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

Native MySQL 8.4.11 matched the output type, member order, and
coordinates within 10⁻⁸ degrees in all 648 structured results across
both operand orders and WGS 84, GRS 1980, and Clarke 1866. Those cases
cover holes, boundary overlap, isolated touches, degenerate lines,
antimeridian crossings, and multipart inputs. A separate 90-pair
seeded run matched 344 of 360 results. The 16 differences were in
point retention or point order in `ST_Intersection`.

The 448-case near-edge probe matched 397 intersection geometries at
that tolerance. Fsdb now returns a line fragment in all 11 cases whose
relation predicates still differ. Near-edge contact retention and
ordering remain open, along with the 11 `Crosses` versus `Touches`
differences and overlays with nondegenerate polar or half-turn
segments.

### Isolated contacts beside clipped linework

MySQL subtracts the finished line fragments from the point result
using Boost's default geographic spheroid. Matching that operation
lets a point survive when it coincides with an intermediate cut that
is absent from the final line. It also removes nearby crossings that
Boost treats as covered by the finished line.
[MySQL intersection dispatcher](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc)
· [Boost geographic winding strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/spherical/point_in_poly_winding.hpp).

Compared against native MySQL 8.4.11 at the same 10⁻⁸ degree tolerance,
the 90 seeded intersections went from 16 differences to 14, with
11 of the remaining differences due solely to point order. The 448
near-edge intersections went from 51 differences to 39, with 16 of the
remaining differences due solely to point order. Both fixtures still
contain cases with different geometry content or coordinates.

### Contact order within monotonic sections

MySQL's point overlay emits turns in the order Boost visits section
pairs. The direct traversal walks line sections first and polygon
sections second. A line section can contain several consecutive edges
with the same latitude and longitude directions. In that case an
exterior crossing on a later line edge can precede a hole crossing
on an earlier edge. Fsdb groups contacts by those sections before
subtracting the final linework. [Boost turn traversal](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/overlay/get_turns.hpp)
· [Boost section boundaries](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/sections/sectionalize.hpp).

When a line segment strictly contains a polygon edge, the collinear
turn belongs to the destination of the polygon edge. Its start is
reported by the incoming edge. This preserves the two isolated
vertices' order even when the source line is reversed.
[Boost linear–areal turns](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/overlay/get_turn_info_la.hpp).

Native MySQL 8.4.11 results compare geometry type, member order,
and coordinates within 10⁻⁸ degrees. The 240 additional seeded inputs
include multipart lines, multiple polygons, holes, reversed rings,
and both operand orders over WGS 84, GRS 1980, and Clarke 1866. The
hole-meridian cases vary line direction and how intermediate source
vertices divide the span along the hole.

| Intersection fixture | Cases | Differences before | Differences after | Order-only before | Order-only after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Seeded rectangles | 90 | 14 | 3 | 11 | 0 |
| Near-edge and near-hole contacts | 448 | 39 | 23 | 16 | 0 |
| Seeded multipart and holed rectangles | 240 | 131 | 7 | 124 | 0 |
| Aligned hole meridians | 216 | 108 | 0 | 108 | 0 |

The cases with geometry content or coordinate differences are the
same before and after. A separate 24-case probe used alternating line
segments and stepped polygon boundaries that exceed Boost's direct
traversal threshold. All 24 differed before and after, with nine
order-only differences in each run. The spatially partitioned traversal
and residual coordinate differences remain open.
[Boost spatial partition traversal](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/partition.hpp).

`ST_Difference`, `ST_Union`, and `ST_SymDifference` each matched
all 90 seeded rectangles on the final build. The
[edge predicate oracle](../scripts/geographic-edge-oracle.py)
still reports the same 11 `Crosses` versus `Touches` differences.

### Contact order in spatial partitions

When both inputs have more than 16 sections, MySQL partitions their
geographic envelopes. The point overlay visits sections spanning the
split before the two halves and alternates longitude and latitude on
recursive splits. Fsdb uses that traversal for the point contacts.
The envelopes include geodesic latitude extrema and wrap longitude
across the antimeridian. [Boost section partitioning](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/partition.hpp)
· [Boost geodesic segment envelopes](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategy/spherical/envelope_segment.hpp).

The native MySQL 8.4.11 comparison used the same 10⁻⁸ degree tolerance.
The dense fixtures increase the number of sections enough to exercise
recursive partitions. The antimeridian fixtures use stepped polygons
whose horizontal edges and line crossings span the longitude seam.

| Intersection fixture | Cases | Differences before | Differences after | Order-only before | Order-only after |
| --- | ---: | ---: | ---: | ---: | ---: |
| Stepped polygons | 24 | 24 | 15 | 9 | 0 |
| Dense stepped polygons | 15 | 15 | 8 | 7 | 0 |
| Stepped polygons across the antimeridian | 15 | 15 | 10 | 5 | 0 |

The same 33 cases still differ in geometry content or coordinates.
Nine include a clipped longitude that differs by exactly 360° within
tolerance. MySQL serializes the unwrapped longitude in those cases.
After normalizing longitudes, all linework agrees and 28 cases still
differ in isolated points. The four earlier intersection fixtures
retained their match counts.
A compact two-fragment regression checks that the contact spanning
the split precedes contacts in the left half, including when an
input crosses the antimeridian.

### Unwrapped clipping longitudes

The solver can return a crossing just outside the conventional
longitude interval. MySQL 8.4.11 preserves that longitude in geographic
line–area overlay output. For example, clipping
`LINESTRING(-179 9,179.5 13)` against a rectangle spanning
`178°E–178°W` and latitudes 10°–12° produces a cut at about
`-180.12343490820842`. Both `ST_AsText` and `ST_AsWKB` retain
the coordinate. Fsdb preserves the generated longitude in this
overlay path. [Boost geographic segment intersections](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

| Operation on the stepped antimeridian fixture | Cases | Differences before | Differences after |
| --- | ---: | ---: | ---: |
| `ST_Intersection` | 15 | 10 | 5 |
| `ST_Difference` | 15 | 6 | 0 |
| `ST_Union` | 15 | 9 | 0 |
| `ST_SymDifference` | 15 | 9 | 0 |

The five remaining `ST_Intersection` cases disagree on isolated
points. The seeded, near-edge, multipart, and other stepped intersection
fixtures retained their existing match counts.

### Coordinate ranges in spatial function arguments

MySQL validates geographic operation arguments in radians and checks
longitude before latitude at each vertex. Fsdb now reports
`3731 / 22S02` for an out-of-range longitude and `3732 / 22S03`
for an out-of-range latitude. The checks run before a binary operation
compares the two SRIDs and before a same-SRID transform returns its
input. Exporters, geometry component extraction, and shape metadata
such as `ST_NumPoints` still accept the generated geometry.
[MySQL geometry argument parser](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/wkb.cc)
· [MySQL coordinate range visitor](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/coordinate_range_visitor.h).

The MySQL 8.4.11 wire comparison matched the code, SQLSTATE, and
message in all 37 range-error cases. The fixtures use an unwrapped
overlay cut and an invalid latitude created by `ST_SwapXY`.
Normalized controls and all 1,048 prior intersection outputs were
unchanged. Some geographic operations still differ in availability or
unsupported-SRS error codes, including `ST_IsClosed`.

### Geographic MBR predicates

`MBRContains`, `MBRWithin`, and `MBRIntersects` use geodesic
edge envelopes on geographic SRIDs. An edge can extend past either
endpoint latitude. Enclosing longitudes can cross the antimeridian,
and a polar polygon can extend its envelope to a pole. Empty operands
return NULL. For containment, a bound collapsed to a line uses
geographic line membership. A line along a rectangle's edge has no
interior point inside that rectangle. [MySQL envelope construction](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/mbr_utils.cc)
· [MySQL geographic box containment](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/within.cc).

The MySQL 8.4.11 comparison matched all 3,720 results over 1,240 pairs.
It used 44 structured cases across the five supported geographic
SRIDs, another 400 seeded cases, and both operand orders. The
structured cases cover wrapped boundary rounding, geodesic
latitude extrema, polar lines and areas, point and line boxes,
multipart and nested collections, and empty members. This comparison
covers scalar evaluation of geographic bounds.

### Coverage, equality, overlap, disjointness, and touch

`MBRCovers`, `MBRCoveredBy`, `MBRDisjoint`, `MBREquals`, `MBROverlaps`,
and `MBRTouches` use the same scalar envelopes as the existing predicates.
`MBREquals` treats two empty geometries as equal and an empty geometry as
unequal to a nonempty one. It returns NULL for a SQL NULL operand.
Geographic coverage and disjointness compare wrapped longitude ranges.
Geographic box overlap and touch compare the stored intervals directly.
For two line bounds, MySQL evaluates the line relation, and an interior
crossing counts as overlap. A ring's starting vertex can affect the final
wrapped envelope by one representable coordinate, which matters for
inclusive coverage. [MySQL MBR reference](https://dev.mysql.com/doc/refman/8.4/en/spatial-relation-functions-mbr.html)
· [MySQL box overlap](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/overlaps.cc)
· [MySQL box touch](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/touches.cc).

All 19,710 results matched native MySQL 8.4.11 across SRID 0 and the five
supported geographic SRIDs. This includes 1,390 pairs of structured and
seeded shapes and a holdout of 800 ring rotations, wrapped meridians, and
seam corners. The previous 3,720 results for containment and intersection
also matched. The six added functions matched MySQL's codes and SQLSTATEs
for arity, mixed SRIDs, invalid GIS data, and invalid latitude. The
latitude errors also matched message text. The other messages follow
Fsdb's shared spatial error formatting.

#### Scalar scan timing

The native Release CLI with a data directory and native MySQL 8.4.11
with default InnoDB settings ran on the same arm64 Mac over loopback TCP.
Each query summed the predicate results over 2,000 stored geometry pairs.
The planar table used SRID 0. The geographic table mixed the five supported
geographic SRIDs. Both tables repeated the seeded pairs from the comparison
above. Each query had three warmups and nine timed runs per engine, with
engine order reversed on alternating runs. Row counts and every sum matched.

| Workload | MySQL median (ms) | Fsdb median (ms) | Fsdb / MySQL latency |
| --- | ---: | ---: | ---: |
| Planar, existing three predicates | 9.38 | 11.20 | 1.19× |
| Planar, six added predicates | 16.21 | 19.17 | 1.18× |
| Geographic, existing three predicates | 27.48 | 13.21 | 0.48× |
| Geographic, six added predicates | 52.95 | 27.89 | 0.53× |

The timings cover warm read-only scans with scalar evaluation. They do not
measure index selectivity, write throughput, or durability costs.

#### SRID-restricted spatial indexes

Fsdb now accepts SRID declarations from its supported spatial catalog.
Writes check the declared SRID, and an indexed column needs a separate
index drop before an SRID change. MySQL 8.4.11 and Fsdb agree on the
tested DDL, geometry subtype and SRID errors, and catalog IDs.

The index stores geodesic envelopes for geographic columns. It searches
equivalent longitude ranges for `MBRIntersects`, `MBRWithin`, and
`MBRContains`, then rechecks the full SQL predicate. Candidate selection
retains uncommon degenerate envelopes whose line interpretation can extend
past their stored latitude. Generated out-of-range values remain candidates
so scalar evaluation can report their coordinate error. MySQL can skip
such values when a forced spatial probe excludes them.

All 400 queries matched across Fsdb and MySQL with the spatial index
both forced and ignored. The corpus used points, lines, multipoints, and
polygons on all five supported geographic SRIDs. Separate fixtures cover
latitude extrema, both sides of the antimeridian, updates, deletes,
WAL replay, and checkpoint reload.

The same-host, durable Release benchmark used 24,000 WGS 84 grid points
and one geodesic line. Each query returned ordered IDs, with three
warmups and seven timed runs per mode over TCP.

| Query | Rows | MySQL scan (ms) | Fsdb scan (ms) | MySQL forced index (ms) | Fsdb forced index (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Local rectangle | 6 | 115.438 | 106.742 | 0.272 | 0.496 |
| Antimeridian rectangle | 16 | 130.137 | 127.231 | 0.417 | 0.537 |
| Geodesic latitude extremum | 1 | 75.347 | 65.596 | 0.188 | 0.335 |

MySQL chose a spatial range under `FORCE INDEX` for the three timed
queries. In the geodesic extremum query without hints, Fsdb chose the
spatial index and MySQL chose a scan. Cost-model parity remains open.
[MySQL spatial index optimization](https://dev.mysql.com/doc/refman/8.4/en/spatial-index-optimization.html).

#### Additional MBR index predicates

`MBREquals`, `MBRTouches`, `MBROverlaps`, and `MBRDisjoint` can use
spatial indexes on Cartesian and geographic columns. Equality probes
check approximate equality on the cached bounds and keep empty rows
searchable. Touch and overlap probes keep geographic line cases that can
extend beyond a collapsed latitude interval. Disjoint probes compare
the exact cached bounds in the original argument order. Unhinted
queries stop collecting when estimated candidate lookups would cost
at least as much as scanning. Invalid geographic rows remain available
to the scalar coordinate validator. MySQL scans for `MBRCovers` and
`MBRCoveredBy` in the audited case. [MySQL MBR predicate reference](https://dev.mysql.com/doc/refman/8.4/en/spatial-relation-functions-mbr.html).

All 500 seeded queries returned the same IDs across both engines with
the index forced and ignored. That run exposed 30 cases where an earlier
Fsdb scalar implementation reported that a collapsed geographic box
overlapped itself. Identical bounds now produce false. In the structured
comparison, MySQL's forced equality range omitted a point one ULP away
that its own scan matched. Fsdb returns both points in both modes.
Planar containment and within probes use padded bounds to preserve the
same scalar equality tolerance.

The same-host durable benchmark used 24,000 WGS 84 grid points, a
rectangle, and a distant point. Ordered rows matched in every measured
query. Times are medians over seven runs after three warmups per mode.

| Query | Rows | MySQL scan (ms) | Fsdb scan (ms) | MySQL forced index (ms) | Fsdb forced index (ms) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Equality | 1 | 73.656 | 62.823 | 0.214 | 0.355 |
| Touch | 11 | 112.796 | 95.208 | 0.389 | 0.419 |
| Overlap | 1 | 113.512 | 98.498 | 0.362 | 0.436 |
| Disjoint | 1 | 113.428 | 95.071 | 1.272 | 1.226 |

The measured disjoint query excludes all but one row. Without a hint,
Fsdb picked the index for this query and a scan for a disjoint probe
that returned almost every row.

## November 26 line-relation recheck

Native MySQL 8.4.11 and the Release Fsdb build matched all 448
structured geographic line/polygon relation cases. They also matched
5,000 WGS 84 cases from seed `615801` in each direction. The point
relation and point-to-line distance controls reported zero differences.

`--compare-overlays` compares intersections only when a line relation
differs. This run produced no overlay comparisons.

```sh
uv run --with pymysql python torture/scripts/geographic-edge-oracle.py --compare-overlays
uv run --with pymysql python torture/scripts/geographic-edge-oracle.py --seeded-relations --seed 615801 --seeded-polygons 2500
uv run --with pymysql python torture/scripts/geographic-edge-oracle.py --seeded-relations --seed 615801 --seeded-polygons 2500 --reverse-lines
```
