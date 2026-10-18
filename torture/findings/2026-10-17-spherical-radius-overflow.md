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

## Follow-up: near-pole rounding

An expanded deterministic sweep of 79 point/radius combinations found one
coordinate-pair discrepancy, repeated at four radius values and mirrored at
the south pole. For `POINT(89.999999 0)` and
`POINT(89.999999 179.999999)` at radius `1e300`, MySQL returns
`3.4906587964907284e292` and fsdb returns `3.4906584856282815e292`,
about `8.9e-8` lower. The same relative difference occurred at radii
`5e307`, `5.7e307`, and `1e308`; the other 71 cases matched within the
oracle's `1e-13` relative tolerance.

The difference came from conversion of geographic coordinates to radians.
MySQL uses the SRS's stored angular unit (`0.017453292519943278` for WGS 84),
while fsdb used `pi / 180`. SRID 0 uses the latter conversion in MySQL too,
and already matched fsdb. Fsdb now uses the geographic angular unit for the
built-in geographic SRS definitions and subtracts converted coordinates as
MySQL's haversine strategy does. All 79 oracle cases now match, including the
eight near-pole combinations.

A deterministic follow-up adds 240 point pairs at radius `1e300`, including
near-polar and opposite-meridian pairs. All 319 WGS 84
cases match MySQL 8.4.11 within `1e-13` relative tolerance. Twelve further
cases cover the same polar and near-antipodal patterns in SRID 0, EPSG 4030,
4258, and 4269; all match. SRID 0 uses longitude/latitude WKT order, while
the audited geographic systems use their latitude/longitude axis order.
This strengthens the spherical-distance precision check; it does not resolve
the separate geographic point/ring topology discrepancies.
