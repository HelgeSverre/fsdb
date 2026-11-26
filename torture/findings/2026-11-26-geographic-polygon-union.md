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
half-turn edges, near-degenerate multiway turns, and other geographic
polygon overlay operations open.
