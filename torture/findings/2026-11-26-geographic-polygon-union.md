# Geographic polygon union

MySQL 8.4.11 merges overlapping geographic polygon boundaries with
`ST_Union`. Fsdb previously returned 1235 for a pair of polygons and
for a collection containing the same two polygons.

~~~sql
SET @a = ST_GeomFromText('POLYGON((0 0,4 0,4 4,0 4,0 0))', 4326, 'axis-order=long-lat');
SET @b = ST_GeomFromText('POLYGON((2 2,6 2,6 6,2 6,2 2))', 4326, 'axis-order=long-lat');
SELECT ST_AsText(ST_Union(@a, @b), 'axis-order=long-lat');
~~~

The two generated vertices are near `(2, 4.00244656)` and
`(4, 2.00122630)` in longitude and latitude. The overlay splits both
rings at the same ellipsoidal turns, removes covered fragments and
opposite coincident edges, and assembles exterior rings and holes.
Clockwise exterior traversal supplies the turns. Reversing that
direction shifts some generated coordinates even when the same
geodesics intersect. [Boost geographic segment strategy](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp)

For collections, the accumulated polygons are merged with each
successive polygon. A later turn can lie on an edge shortened by an
earlier merge. The dissolved area clips lines, then the retained area
and lines absorb covered points. [MySQL collection union](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/gc_utils.cc#L116-L164)

## Oracle results

The wire audit used the [digest-pinned MySQL image](../config/toolchain.json),
both operand orders, and SRIDs 4326, 4258, and 4267. It compared the SRID,
geometry type, ring direction, and coordinates within 10⁻⁸ degrees.
It allowed a cyclic shift of each closed ring and permutation of holes
and collection or multipolygon members.

| Inputs | Comparisons | Geometry matches | Different ring start or member order |
| --- | ---: | ---: | ---: |
| Structured cases | 126 | 126 | 96 |
| Seeded rectangles | 420 | 420 | 324 |
| Seeded polygons with 3–7 vertices | 360 | 360 | 348 |
| Total | 906 | 906 | 768 |

The structured cases include containment, equal boundaries, disjoint and
point-touching polygons, partial shared arcs, an island in a hole, holes
filled partly or fully, antimeridian crossings, a polygon bridging two
multipolygon components, and collections of up to three overlapping areas.
The collection checks cover empty partners and lines and points inside,
outside, or on a boundary. The seeded cases used Python's
`random.Random(43264258)`.

The antimeridian union could be stored in an indexed SRID 4326 column.
Fsdb matched MySQL on the stored type, SRID, and inside and outside
point checks. WAL replay after an abrupt stop recovered the identical
Fsdb WKB.

The audit leaves serialized ring start and member order, polar endpoints,
half-turn edges, and near-degenerate multiway turns open.

## Intersection, difference, and symmetric difference

Polygon and multipolygon operands, including nested collections containing
only areas, support the other three set operations. A contained intersection
returns the inner polygon. Subtracting it from the exterior preserves it as
a hole. An intersection limited to one shared edge or vertex returns a line
or point. A disjoint intersection returns an empty geometry collection.

Area intersection uses clockwise turns. Its point result visits the rings
in their original direction and removes contacts covered by the output area
or lines. Two traversals of the same crossing can round to different
coordinates, leaving an isolated point next to an output vertex. Difference
reverses its clipping rings, so its generated coordinates can differ from
those of intersection. Symmetric difference first tries the union of both
directed differences. If the rounded cuts prevent the union from assembling
closed rings, the overlay uses a single cut at each crossing and retains
the boundary of the area covered by exactly one operand. [MySQL intersection functor](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc#L124-L139) [Boost symmetric difference](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/sym_difference.hpp)

The wire audit ran 34 polygon pairs in both orders at SRIDs 4326, 4269,
and 4267 against the [digest-pinned MySQL 8.4.11 image](../config/toolchain.json).
The pairs comprised 10 controls and 24 seeded crossings. The controls
included equal, contained, disjoint, shared-vertex, shared-edge, and
hole cases, plus multipolygon and nested polygon-only collections.
The crossing fixtures used Python's `random.Random(127)`, integer
origins within longitude −65 to 55 and latitude −45 to 40, and
rectangles with corners `(x,y)` and `(x+4,y+5)`, and `(x+2,y+2)`
and `(x+6,y+6)`.

The baseline at `9728ec85` returned error 1235 in every case. The
candidate returned geometry for all cases and produced identical results
in memory and with WAL enabled. Area was computed from the returned WKT
on the corresponding ellipsoid, summing the area of polygon members.
The area check allows the greater of 1 m² or 0.001% of the MySQL area.
The WKT distance check compares vertices and segments in longitude and
latitude using planar Hausdorff distance below 0.0001°, and counts two
empty results as a match.

| Operation | Comparisons | Same SQL geometry type | Within area tolerance | Within WKT distance tolerance | Exact WKT |
| --- | ---: | ---: | ---: | ---: | ---: |
| Intersection | 204 | 182 | 204 | 204 | 42 |
| Difference | 204 | 204 | 204 | 204 | 33 |
| Symmetric difference | 204 | 173 | 170 | 170 | 24 |

The intersection type differences come from near-vertex point members which
one engine retains and the other absorbs. In several symmetric difference
crossings MySQL emits one lobe, while Fsdb retains both. Serialized ring
starts and member ordering also differ. The audit covers polygon-only
collections. Collections mixing areas, lines, and points still have
unsupported cases.

### Shared edges

For an intersection limited to boundary lines, MySQL walks the source
ring from its start, keeps its vertices, and can drop subdivision
vertices from the clipping ring. It uses the polygon as the source for
a polygon paired with a multipolygon, regardless of argument order.
For like types the first operand supplies the source. Fsdb keeps the
source ring and edge identities while splitting polygons, then uses
the geographic line assembly for coincident fragments outside the
result area. [Boost areal intersection line dispatch](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/overlay/intersection_insert.hpp#L803-L841) [MySQL multipolygon dispatch](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc#L795-L805)

On the pinned MySQL 8.4.11 image, 16 ordered controls matched exact WKT,
including reversed and rotated rings, partial edges, corners, holes,
and multipolygon components meeting at a point. A seeded audit of 48
polygon pairs in both orders at SRIDs 4326, 4269, and 4267 matched
directed line components and vertices in 273 of 288 cases, up from 6.
The fixtures subdivide meridian and equator edges and rotate or reverse
the subdivided rings using `random.Random(623)`. Vertex comparison
rounds each coordinate to 10 decimal places. In the 15 remaining cases
MySQL splits a shared line at the clipping ring's start while Fsdb
returns one continuous line. All 612 earlier polygon comparisons
retained their previous Fsdb results.
