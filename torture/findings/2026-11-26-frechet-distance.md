# Discrete Fréchet distance

Fsdb previously reported an unknown function for `ST_FrechetDistance`. It
now compares two line strings using their stored vertex order. At each pair
of vertices, the recurrence chooses whether one line or both advance and
keeps the least possible maximum separation. It stores one row of distances
for the shorter line. With no unit argument, the result uses the coordinates'
units for projected lines and metres for geographic lines.

The [SQL fixture](2026-11-26-frechet-distance.sql) runs against MySQL 8.4.11
from the [pinned toolchain](../config/toolchain.json) and both Fsdb storage
modes. All seven result rows and 13 error responses match, including SQLSTATE
and messages. The fixture covers unequal line lengths, reversed order,
duplicate vertices, empty collections, unitless SRID 0, projected and
geographic units, SRID-prefixed binary inputs, coordinate validation, numeric
overflow, and errors at SQL PREPARE. A binary-protocol probe also matches all
parameter and result definitions and executes successfully with two EPSG 3857
geometry blobs and a string unit. MySQL describes the optional unit placeholder
as GEOMETRY at prepare time. MySQL rejects explicit GEOMETRY bind packets
with 1210. Fsdb's pre-existing typed-geometry decoder accepts the type and
uses its own geographic coordinate order. The compatible binary bind type
for the fixture is BLOB.

A sweep of 200 coincident WGS 84 point and line pairs found 47 cases where
the previous geographic distance path returned a nonzero self-distance. The
new point strategy checks coordinate equality before computing a distance
and uses a first-order meridian arc for meridional pairs. All 200 now return
zero for both `ST_Distance` and `ST_FrechetDistance`. The fixture also checks a
roughly one-millimetre separation on a single meridian. [Boost geographic point distance](https://raw.githubusercontent.com/boostorg/geometry/boost-1.84.0/include/boost/geometry/strategies/geographic/distance.hpp)

A seeded sweep (831422) compared 100 generated line pairs in each direction
for each of SRID 0, 3857, 4326, 4258, and 4267. The lines had 2–20 vertices
and included repeated vertices, near-polar coordinates, and wrapped
longitudes. Cases with a defined SRS also sampled kilometre and US survey
foot conversions. Both Fsdb modes agreed with MySQL on all 1,000 results
within 0.2 mm (coordinate units for SRID 0). The maximum geographic difference
was about 0.066 mm. Rebuilding the eight directed results outside a stricter
tolerance from each server's ordinary `ST_Distance` point matrix reproduced
their reported values.
