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

Nonzero segments with a polar endpoint or a 180-degree longitude
change remain outside the implemented path.
For two audited half-turns through the north pole, MySQL
returns longitudes of approximately 61.18 and 64.46 degrees depending
on the operand order, at latitude 89.9999999999995 degrees.

## Near-endpoint precision

A 48-result precision probe originally exposed ten intersection mismatches
on nearly adjacent equatorial segments. MySQL treats the
gap between longitude `4` and `4.000000000001` as a point contact and
returns the first line's endpoint. It reports an empty intersection
when the second start is `4.000000001`.
The follow-up varied gap and overlap sizes, operand orders, and segment
lengths along the equator and a meridian. The results follow the rounded
inverse distance on a unit ellipsoid, measured from the shorter segment.
[Boost geographic segment intersection](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp).

Native MySQL 8.4.11 also matched an equatorial gap of `1e-11` degrees at
longitude 4 and separated a gap of `2e-11`. At longitude 100 the same
transition fell between `2e-11` and `2.5e-11`. The measured brackets
are consistent with one scaled section-envelope margin, so a fixed
longitude tolerance would miss cases. [Boost section envelope expansion](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/sections/sectionalize.hpp).

For adjacent segments on the equator or a shared meridian, Fsdb now
distinguishes a point contact created by inverse-distance rounding from
a short shared span. It retains the first operand's endpoint for rounded
contacts and uses the coordinates to order the cuts of a genuine overlap.

The native MySQL 8.4.11 wire comparison matched 45,304 results, including
all 48 original repro results byte for byte. The corpus varies gaps,
overlaps, and segment lengths, including equal-length segments, six
coordinates, antimeridian crossings, and operand and traversal order.
It covers EPSG 4326, 4258, and 4267. The largest coordinate difference was
about `7.1e-15` degrees. The 5,112 ordinary-line results were byte-identical
to the previous Fsdb run. Near-adjacent geodesics off the audited axes
remain open.

A separate 108-result axis check varies nearly contained spans, where
the endpoints at one end almost coincide. Twenty results differ in
the selected endpoint or in whether the result is a point or a span.
For example, intersecting the equatorial `LINESTRING(4 0,0 0)` with
`LINESTRING(-0.0000005 0,6 0)` gives the first line in MySQL and
`POINT(4 0)` in Fsdb.
Fsdb returned byte-identical results on both sides of the multipart
change for this check.

## Zero-length line strings

For a line whose vertices repeat one coordinate, intersection returns a
point when the other line covers it. Difference and symmetric difference
can remove the zero-length line, while union retains the first operand's
line representation. If the first operand has several zero-length
segments, MySQL can also discard the carrier in a union when that point
occurs inside the carrier's traversal. A point occurring only at the
carrier's endpoint follows the ordinary union rule.

The native MySQL 8.4.11 wire comparison matched 1,104 results across
EPSG 4326, 4258, and 4267. The checks cover both operand orders and all
four overlay operations, including interior and endpoint visits,
repeated vertices, closed lines, and identical and distinct polar
coordinates. The 5,112 ordinary-line wire results were byte-identical
to the previous Fsdb run.

## Multipart line overlays

Geographic `MULTILINESTRING` now participates in intersection, union,
difference, and symmetric difference with line strings and with other
multilines. In the tested mixed cases the commutative operations follow
the single line's traversal in either operand order. Two multilines
follow the first operand's component order. Difference always starts
with the left operand.

Intersection preserves cuts between separately covered components and
retains source vertices when adjacent pieces belong to the same span.
The order of the covering components matters when they meet at a turn.
The result removes duplicate lines and keeps isolated point contacts
separate from shared spans. Union keeps uncovered fragments that survive
clipping, including when a repeated point lies inside the original
component.

The native MySQL 8.4.11 wire checks matched all 3,456 results across
general multipart linework, fragmented networks, and reordered corner
cases. They include all four operations, both operand orders, and
EPSG 4326, 4258, and 4267. Geometry types, SRIDs, and component order
matched, and computed coordinates differed by at most `1.7e-12`
degrees. Of those results, 2,929 matched byte for byte. The previous
5,112 single-line results also remained byte-identical.

Run the focused fixtures with this command.

```sh
just test --filter-test-case 'geographic multiline overlays'
```
