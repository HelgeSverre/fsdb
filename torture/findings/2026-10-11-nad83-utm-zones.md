# NAD83 UTM zones 1N–23N

MySQL 8.4.11 lists 23 NAD83 / UTM systems, EPSG 26901–26923. Their
Transverse Mercator projection uses the GRS 1980 ellipsoid and the same UTM
scale and false easting as WGS 84 zones. The catalog definitions include
NAD83's `TOWGS84[1,1,-1,0,0,0,0]` datum translation.

Fsdb now generates all 23 catalog entries and applies the NAD83 translation
when a projected coordinate crosses the WGS 84 datum boundary. At 49°N,
123°W, MySQL transforms NAD83 to EPSG 26910 as
`POINT(500000.0000000093 5427455.781076529)`, while WGS 84 to the same
SRS yields `POINT(499999.70608616463 5427455.393397634)`. The regression
checks both paths and the WGS 84 inverse. The differential contract compares
all 23 exact definitions and these forward and reverse results.

Other NAD83 projected systems and most of MySQL's SRS catalog remain open.
