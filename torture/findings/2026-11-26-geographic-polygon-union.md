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

### Mixed collection difference

`ST_Difference` first dissolves the source collection's polygons,
clips its lines against those polygons, and removes points covered by
either. It then subtracts each mask member in collection order from
each surviving dimension. A polygon cut can create a hole without
reintroducing lines or points that the original area covered. When
the normalized source has several dimensions, result members appear
in area, line, point order. If several members survive in one
dimension, they can still be returned as a geometry collection.
[MySQL collection normalization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/gc_utils.cc#L124-L169) [MySQL collection difference](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/difference_functor.cc#L43-L96)

The wire audit ran 72 pairs in both orders at SRIDs 4326, 4269, and
4267 against pinned MySQL 8.4.11. Seed `183` generated 18 sets of
rectangles, lines, and points. Each set put a nested mixed collection,
polygon, line, and point against a mixed mask. The masks covered
contained, crossing, and disjoint polygons as well as overlapping
and disjoint lines and points. The baseline at `f16ac022` returned
error 1235 in 378 of 432 cases. Fsdb now returns geometry with the
same SQL type and planar Hausdorff distance below 0.0001° in all 432
cases, including pairs of empty results. Memory and WAL results are
identical. Normalized members and vertices agree within 10⁻⁸ degrees
in 422 cases. Exact WKT matches in 297 cases. The remaining numerical
and serialization differences include ring starts and rounded
crossing coordinates.

Eleven additional controls checked for covered members, nested masks,
and narrowing. Seven matched exact WKT and four differed only in the
case of the exponent marker in a line coordinate. Eight other controls
covered overlapping source polygons and lines, multipart masks, and
empty collections. Five matched exact WKT. The other three differed
in ring start and coordinate rounding. Of the earlier 612
polygon comparisons, six differences involving polygon-only collections
now match MySQL's WKT order. The other 606 results retained their
previous Fsdb output.

### Mixed collection intersection

`ST_Intersection` normalizes each input collection before intersecting
its area, line, and point groups. When both inputs are collections,
each first-input group is intersected with the groups of the second.
The output keeps the order of those intersections and removes equivalent
members. A point returned by an area intersection can remain next to a
line from a different group, even at that line's endpoint. Two surviving
lines can remain separate geometry collection members. A single
surviving member is returned on its own. [MySQL collection intersection](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/intersection_functor.cc#L175-L234) [MySQL result normalization](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/so_utils.cc#L88-L156)

The intersection audit reused the 72 pairs, both directions, and
three SRIDs from the difference audit. The baseline at `288b52ab`
returned error 1235 in 324 of 432 cases. Fsdb now returns the same
SQL geometry type as pinned MySQL 8.4.11 in all 432 cases. All
point sets are within 0.0001° planar Hausdorff distance, with two
empty results counted as a match. Normalized members and vertices match
within 10⁻⁸ degrees in 388 cases. WKT matches exactly in 334 cases.
Memory and WAL results agree. Near-boundary contacts can still
produce different points, and some crossings differ in grouping
or coordinates.

Sixteen of 18 ordered controls match exact WKT. The other two
combined multipart areas, boundary lines, and points and differ
in coordinate serialization. The 612 earlier polygon cases and
432 mixed difference cases retained their previous Fsdb outputs.

### Mixed collection symmetric difference

For two collections, `ST_SymDifference` applies the first collection's
normalized polygon, line, and point groups to the second in sequence.
Each group sees the result of the previous operation. An empty group
can still normalize the intermediate collection. For a single shape
against a collection, the shape's dimension determines the order of
the result groups. Singleton multipart members can remain wrapped
inside a mixed result. An empty collection on the left preserves
the nested members of the right operand in the audited control.
[MySQL collection symmetric difference](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/symdifference_functor.cc#L721-L750)

The audit reused the 72 pairs, both directions, and three SRIDs from
the difference and intersection audits. All 432 cases returned error
1235 at `5411a2ec`. All now return the same SQL geometry type as pinned
MySQL 8.4.11, and memory and WAL results agree. The outputs are within
0.0001° planar Hausdorff distance in 388 cases. Normalized members and
vertices match within 10⁻⁸ degrees in 370 cases, and exact WKT matches
in 250 cases. Some area crossings still retain a lobe in Fsdb that
MySQL drops. The resulting polygon boundaries can also change how
later line and point members are clipped.

Three cases needed to keep the area rings already assembled by the
overlay when a second dissolve failed at rounded crossings. Fsdb
still clips the intermediate lower-dimensional members against those
rings. All 20 ordered controls match normalized members and vertices
within 10⁻⁸ degrees, including the asymmetric empty collection case.
Twelve of the earlier 612 polygon cases now match serialized member
order exactly. The other 600 polygon cases and the 864 mixed
difference and intersection cases returned their previous results.
