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
