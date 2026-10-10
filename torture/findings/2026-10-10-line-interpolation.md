# Line interpolation across supported SRSs

MySQL 8.4.11 implements `ST_LineInterpolatePoint`,
`ST_LineInterpolatePoints`, and `ST_PointAtDistance` for planar and geographic
LineStrings. The first two interpret their numeric argument as a fraction of
total line length; the last uses SRS distance units (metres for EPSG 4326).

All three fsdb functions share one segment walker. Planar segments use
Euclidean distance. For EPSG 4326, MySQL's Boost.Geometry strategy measures
segments with Andoyer distance, obtains an Andoyer azimuth, and projects with
first-order Thomas direct geodesics. The [MySQL functor](https://dev.mysql.com/doc/dev/mysql-server/8.4.11/line__interpolate__functor_8h_source.html)
selects Boost's [geographic line strategy](https://raw.githubusercontent.com/boostorg/geometry/boost-1.87.0/include/boost/geometry/strategies/geographic/line_interpolate.hpp),
which in turn uses [Andoyer inverse](https://raw.githubusercontent.com/boostorg/geometry/boost-1.87.0/include/boost/geometry/formulas/andoyer_inverse.hpp)
and [Thomas direct](https://raw.githubusercontent.com/boostorg/geometry/boost-1.87.0/include/boost/geometry/formulas/thomas_direct.hpp).
This matters: Vincenty's more accurate
geodesic gives visibly different coordinates. Repeated points advance from
the previous interpolated point, matching Boost's behavior rather than
calculating every point independently from the original segment start.

The native contract covers planar, EPSG 3857/3395, and EPSG 4326 results;
fraction and absolute-distance boundaries; a zero-length line; NULL;
prepared execution; and MySQL's wrong-type and out-of-range errors.
MySQL does not apply `max_points_in_geometry` to this function; a contract
also checks an output larger than the default 65,536-point setting.
