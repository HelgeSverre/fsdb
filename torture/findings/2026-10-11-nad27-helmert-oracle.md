# NAD27 datum and UTM oracle

MySQL 8.4.11 lists NAD27 as EPSG 4267 on the Clarke 1866 ellipsoid
(`a = 6378206.4`, inverse flattening `294.9786982138982`). Its WKT1
`TOWGS84` parameters are `[-32.3841359, 180.4090461, 120.8442577,
-2.1545854, -0.1498782, 0.5742915, 8.1049164]`: translations in metres,
rotations in arcseconds, and scale in parts per million. The catalog also has
NAD27 / UTM zones 1N–22N at EPSG 26701–26722. Fsdb now supports geographic
EPSG 4267 and the [projected NAD27 UTM family](2026-10-11-nad27-utm-zones.md).

At NAD27 `POINT(49 -123)`, MySQL transforms to WGS 84
`POINT(48.999977750278994 -123.00196683602634)`. A direct geocentric
calculation using the [PROJ Helmert position-vector convention](https://proj.org/en/stable/operations/transformations/helmert.html)
produced `POINT(48.99997775027899 -123.00196683602636)`; reversing the
rotation signs instead misses by roughly 0.001° latitude. This identifies the
convention used by the implementation.

MySQL projects the same NAD27 point to EPSG 26710 as
`POINT(500000.0000000093 5427236.770806939)`. Transforming the WGS 84
point with the same numeric latitude and longitude to EPSG 26710 gives
`POINT(500143.85815078736 5427239.241439159)`, showing why the datum shift
cannot be skipped. The matching projected point transforms back to WGS 84 as
`POINT(48.99997775027899 -123.00196683602647)`.

The geographic implementation exposes Clarke 1866 distance and uses a
position-vector Helmert shift through geocentric coordinates in both transform
directions. Its catalog definition, distance, and forward/reverse values are
covered by Expecto and differential contracts. The shared UTM projection now
uses the datum's semi-major axis and covers the 22 projected catalog rows.
