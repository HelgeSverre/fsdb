# Geographic point-to-line distance

Status: implemented for `POINT`/`MULTIPOINT` against `LINESTRING`/
`MULTILINESTRING`, in either argument order, with optional linear units.

MySQL 8.4.11 measures the nearest point on each geographic line segment,
rather than the nearest vertex or a planar projection. For EPSG 4326,
`POINT(51 5)` to `LINESTRING(50 0,50 10)` is
99,246.14802240916 metres; the midpoint of that line is north of latitude
50, so a vertex-only or flat-segment calculation gives a different answer.
The same result holds with arguments reversed and when the point or line is
one member of a multi-geometry. An on-segment point gives zero. Oracle cases
also cover a high-latitude arc and a segment crossing the antimeridian.

Fsdb minimizes its MySQL-compatible ellipsoidal point distance along each
segment using the same geographic interpolation as `ST_LineInterpolatePoint`.
Azimuth projection checks keep the search inside the segment, and endpoint
distances use the original coordinates so a nearest endpoint remains exact.
For an 11-micrometre latitude separation, MySQL returns
0.000011057302444142153 metres while its near-coincident longitude
separation rounds to zero. Fsdb retains that meridional distance when the
general angular formula rounds to zero, and projects directly onto exact
equatorial and meridian segments so their nearest point is not lost to search
rounding. This also prevents a distinct near-line point from collapsing to
zero distance in the audited case.

Oracle example:

```sql
SELECT ST_Distance(
  ST_GeomFromText('POINT(51 5)',4326),
  ST_GeomFromText('LINESTRING(50 0,50 10)',4326));
```
