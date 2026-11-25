# Geographic line topology oracle

MySQL 8.4.11 distinguishes interior crossing, boundary contact, and
collinear overlap for geographic linework in EPSG 4326. The following
queries use MySQL's latitude-longitude axis order. Each result lists
`ST_Intersects`, `ST_Crosses`, `ST_Touches`, `ST_Overlaps`,
`ST_Contains`, `ST_Within`, and `ST_Equals`, in that order.

| Relationship | First geometry | Second geometry | MySQL result |
|---|---|---|---|
| Interior crossing | `LINESTRING(0 -1,0 1)` | `LINESTRING(-1 0,1 0)` | `1,1,0,0,0,0,0` |
| Shared endpoint | `LINESTRING(0 -1,0 0)` | `LINESTRING(0 0,1 0)` | `1,0,1,0,0,0,0` |
| T-junction | `LINESTRING(0 -1,0 1)` | `LINESTRING(-1 0,0 0)` | `1,0,1,0,0,0,0` |
| Collinear overlap | `LINESTRING(0 -1,0 1)` | `LINESTRING(0 0,0 2)` | `1,0,0,1,0,0,0` |
| Identical | `LINESTRING(0 -1,0 1)` | `LINESTRING(0 -1,0 1)` | `1,0,0,0,1,1,1` |
| Disjoint | `LINESTRING(0 -1,0 1)` | `LINESTRING(2 -1,2 1)` | `0,0,0,0,0,0,0` |

Reversing the interior-crossing operands still gives `ST_Crosses=1` and
`ST_Touches=0`. `MULTILINESTRING((0 -1,0 1),(5 5,6 6))` crosses the same
horizontal line. MySQL also reports an interior crossing for the two
antimeridian-spanning lines `LINESTRING(-1 179,1 -179)` and
`LINESTRING(-1 -179,1 179)`.

Fsdb computes line/line geographic `ST_Intersects` and `ST_Disjoint`
through its distance path. It now distinguishes crossing, touching, and
partial overlap with sampled geodesic arc intersections and whole-line
boundary positions. The focused regression covers the table above,
contained segments, reversed T-junctions, multipart linework, an
internal-vertex crossing, and longitude wrap. Differential contracts
compare crossing, touching, and overlap with MySQL. Planar
longitude-latitude segments alone would misclassify the antimeridian case.

Fsdb also covers audited line/line `ST_Contains`, `ST_Within`, and
`ST_Equals` behavior. Identical, reversed, and differently segmented paths
compare equal; a contained subsegment is within its carrier; a bent path
sharing only its endpoints is not. MySQL treats a zero-length line at a
carrier line's interior as contained but one at its boundary as not
contained. The implementation checks covered vertices and samples the
covered geodesic path between them. Near-tangent and unsampled path
deviations remain precision limits.

## Geographic line-string overlays

MySQL 8.4.11 returns geographic `LINESTRING` overlay results in the
source traversal order. Intersection retains first-input vertices along
a shared span and includes isolated contacts separately. Difference
clips shared spans out of the first input. Union retains the first line
and appends the uncovered spans of the second. Symmetric difference
returns uncovered spans from each input in operand order.

Fsdb uses the existing geodesic segment membership and proper-crossing
calculation for these operations. Point contacts leave line fragments
intact. The same geodesic rules work across the antimeridian without
projecting the input onto a plane.

The pinned wire comparison checked all four operations in both operand
orders on 213 pairs per SRS across EPSG 4326, 4258, and 4267. All 5,112
results matched geometry type, SRID, component order, and coordinates
within `2e-13` degrees. Of those, 3,852 also matched exact
WKT. The audit includes shared and disjoint geodesics, mixed line and
point results, segmented and reversed paths, endpoint contacts,
antimeridian crossings, and seeded local and global lines.

Multipart input, zero-length lines, and segments with a polar endpoint
or a 180-degree longitude change remain outside the implemented path.
MySQL returns a point for the intersection of a zero-length line with
its carrier. For two audited half-turns through the north pole, it
returns longitudes of approximately 61.18 and 64.46 degrees depending
on the operand order, at latitude 89.9999999999995 degrees.

A separate 48-result precision probe exposed ten intersection mismatches
on nearly adjacent equatorial segments. For example, MySQL treats the
gap between longitude `4` and `4.000000000001` as a point contact and
returns the first line's endpoint. It reports an empty intersection
when the second start is `4.000000001`. Fsdb returns empty in both cases.
The follow-up crossed gap and overlap sizes, operand orders, and segment
lengths along the equator and a meridian. MySQL's result changes with the
shorter segment and the rounding of the inverse distance. The precision
gap remains separate from ordinary segment clipping.
[Boost geographic segment intersection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).
