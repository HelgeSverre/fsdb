# Discrete Hausdorff distance

Fsdb now supports `ST_HausdorffDistance` for point and multipoint, two
multipoints, two line strings, line string and multiline string, and two
multiline strings. MySQL finds the nearest target vertex for each source
vertex, then takes the largest of those distances. Reversing two `LINESTRING`
or two `MULTIPOINT` arguments can change the result. For multiline strings,
MySQL takes the largest distance across component pairs. A multiline string
with separated components can therefore return a nonzero distance when
compared with itself. For mixed argument types, MySQL uses the point or line
string as the source in either argument order. [MySQL implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/hausdorff_distance.cc) [Boost Geometry implementation](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/discrete_hausdorff_distance.hpp)

Both Fsdb storage modes matched all eight result rows and 14 error responses
from the [SQL fixture](2026-11-26-hausdorff-distance.sql) against MySQL 8.4.11.
The fixture covers argument order, component grouping, unit conversion,
SRID-prefixed geometry, empty collections, NULL, invalid coordinates,
overflow, and errors at SQL `PREPARE`. A binary-protocol probe also matched
the parameter and result definitions, and returned `1.0` kilometre from two
EPSG 3857 geometries bound as BLOBs and a unit bound as a string.

A seeded sweep (874181) generated 40 cases per supported pairing and SRID
(0, 3857, 4326, 4258, and 4267). It included duplicate vertices, near-polar
coordinates, wrapped longitudes, and kilometre and foot conversions. Each
storage mode matched all 2,000 results across both argument orders within
0.2 mm for geographic distances and within 1e-8 coordinate units for planar
distances after undoing unit conversions. The largest geographic difference
was 0.089 mm in SRID 4267.
