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
and overflowing results. All six oracle cases match MySQL; `1e307` times
the antipodal angle remains finite.

This does not address MySQL's separate parse-time error 1367 for an
out-of-range `1e309` literal. It also does not assert exact bitwise parity
for spherical distances across the antimeridian.
