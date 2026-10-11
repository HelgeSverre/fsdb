# Geographic point-to-line distance

Status: implemented for `POINT`/`MULTIPOINT` against `LINESTRING`/
`MULTILINESTRING`, in either argument order, with optional linear units.
Geographic line-to-line and polygon distance remain open.

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
Endpoint distances are retained so a nearest endpoint remains exact.

Oracle example:

```sql
SELECT ST_Distance(
  ST_GeomFromText('POINT(51 5)',4326),
  ST_GeomFromText('LINESTRING(50 0,50 10)',4326));
```
