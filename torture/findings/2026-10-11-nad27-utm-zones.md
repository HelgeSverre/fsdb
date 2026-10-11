# NAD27 UTM zones 1N–22N

MySQL 8.4.11 lists 22 NAD27 / UTM systems, EPSG 26701–26722. They use
Clarke 1866 (`a = 6378206.4`, inverse flattening `294.9786982138982`) and
the standard UTM scale and false easting. Fsdb now generates all 22 catalog
definitions and uses that ellipsoid in the shared fourth-order Transverse
Mercator projection.

Transforms within NAD27 stay in that datum. Crossing to WGS 84 applies
NAD27's seven-parameter position-vector Helmert shift. At NAD27
`POINT(49 -123)`, MySQL gives `POINT(500000.0000000093 5427236.770806939)`
in EPSG 26710. Feeding the same numeric coordinates as WGS 84 gives
`POINT(500143.85815078736 5427239.241439159)`. The focused regression
checks both paths, zone 1N and 22N samples, and the inverse to WGS 84.
The differential contract compares all 22 exact catalog definitions and
rounded forward/reverse results.

The initial regression exposed an unnecessary NAD27→WGS 84→NAD27 round trip
for a same-datum transform. Coordinate transformation now composes directly
within a shared datum, avoiding the height loss from that detour.
