# Planar ST_Simplify

Fsdb now simplifies lines and polygon rings with Douglas–Peucker in
SRID 0 and projected coordinate systems. The tolerance uses the input
coordinate units, and the result retains its SRID. A ring starts at a
vertex opposite its closing point after normalizing exterior and hole
winding. If the first simplification loses the original area sign,
other start points can preserve the ring. [MySQL spatial convenience functions](https://dev.mysql.com/doc/refman/8.4/en/spatial-convenience-functions.html#function_st-simplify) [Boost Geometry ring implementation](https://raw.githubusercontent.com/boostorg/geometry/boost-1.84.0/include/boost/geometry/algorithms/simplify.hpp)

Collapsed lines and polygon exteriors disappear. Empty results become
SQL NULL. Collections keep the order and nesting of the surviving
members. MySQL can return a simplified polygon with a degenerate hole.
Passing that result to a second call produces error 3037 because the
result no longer has a well-formed ring. [MySQL simplification implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/simplify.cc)

The [SQL fixture](2026-11-27-spatial-simplify.sql) covers winding, holes,
collapse, nested collections, near-equal endpoints, binary geometry,
and argument errors including nonpositive tolerance and geographic
input. It covers diagnostic precedence, warnings from string tolerance
conversion, and SQL PREPARE. A binary-protocol probe matched MySQL's
GEOMETRY and DOUBLE parameter definitions, nullable GEOMETRY result
definition, and result bytes when binding an EPSG 3857 linestring as
a BLOB and the tolerance as a DOUBLE.

A seeded sweep (427622) matched 1,250 cases against pinned MySQL 8.4.11
in both Fsdb storage modes. The sweep covered lines, multilines,
polygons, multipolygons, and nested collections in SRIDs 0, 3857, and
3395. Tolerance ranged from 0.000001 to 45 coordinate units. Geographic
simplification returns MySQL error 3618.
