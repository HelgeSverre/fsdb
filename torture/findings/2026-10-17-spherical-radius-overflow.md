# Spherical distance with a large finite radius

The native MySQL 8.4.11 oracle is
[`spherical-radius-overflow-oracle.py`](../scripts/spherical-radius-overflow-oracle.py).
It compares `ST_DISTANCE_SPHERE` on EPSG 4326 points and a multipoint with
an explicit radius near the largest finite DOUBLE value.

At radius `1e308`, fsdb evaluated `radius * 2 * asin(...)` left to right.
The intermediate `radius * 2` overflowed even when the final distance was
finite. Consequently, coincident points returned NaN instead of zero, points
one degree apart returned infinity instead of about `1.745329251994328e306`
metres, and a multipoint containing a coincident member lost its zero
minimum. For antipodal points the final distance really does overflow:
MySQL returns error 3049, `Overflow error: INFINITY in function
st_distance_sphere.`, while fsdb returned infinity.

Fsdb now computes the central angle before multiplying by the radius and
raises MySQL error 3049 only when the final point-set distance is infinite.
The focused regression and native oracle cover zero, finite, multipoint,
and overflowing results. All eleven oracle cases match MySQL, including
radii on opposite sides of the antipodal overflow threshold, near-antipodal
points on either side of the antimeridian, and a reversed point-set pair.
`1e307` times the antipodal angle remains finite.

Out-of-range exponential literals such as `1e309` and `-1e309` now return
MySQL's parse-time error 1367 before evaluating the distance function. The
native oracle agrees on 19 radius cases and two bare-literal errors, including
zero, negative, subnormal, maximum finite, and overflowing radii.
String-to-DOUBLE conversion retains its distinct warning and clamping behavior.
This audit does not assert exact bitwise parity for spherical distances across
the antimeridian.
