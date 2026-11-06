# Residual geographic edge and vertex distances

The Release CLI and disposable native MySQL 8.4.11 were compared with
`torture/scripts/geographic-edge-oracle.py` after the reduced-length fix.
The baseline still has 12/412 line-relation, 1/189 near-hole point-relation,
and 8/189 near-hole point-distance mismatches. The seeded point-line distance
set (100 cases) and polar-corner set (100 cases) match.

All eight mismatching polygon distances equal distances to the hole line in
both engines. Five also show a direct point-to-hole-vertex difference of at
least 0.01 m (MySQL first, fsdb second):

| Point | Vertex | MySQL (m) | fsdb (m) |
| --- | --- | ---: | ---: |
| `43 2.99999` | `43 3` | 0.8188541524346334 | 0.8077127604662425 |
| `73 2.99999` | `73 3` | 0.31618400687144826 | 0.33024342460117184 |
| `83 2.99999` | `83 3` | 0.13485314242182217 | 0.16516069457216623 |
| `88 1.00001` | `88 1` | 0.09535991608716497 | 0 |
| `88 2.99999` | `88 3` | 0.09535991608716497 | 0 |

Thus point-to-point geographic distance is part of the remaining problem;
the polygon dispatcher alone cannot account for every mismatch. The
stand-alone Boost 1.84 cross-track probe, using the same version bundled by
MySQL 8.4.11, now accepts six optional coordinates. For the 43-degree
segment it returns 0.1648719135648529 m, agreeing with MySQL's line
distance. At 88 degrees its raw strategy result is 2.4535297088 m while
MySQL's complete line-distance result is 0.0953599161 m. The raw strategy
is therefore useful for tracing arithmetic, but cannot substitute for the
complete SQL operation as an oracle.

The probe also reports Boost's direct geographic point-to-point strategy for
the query and each segment endpoint. At the 43-degree vertex Boost returns
0.8188541524346334 m, matching MySQL rather than fsdb. At 83 degrees it
returns 0.1651606945721662 m and at 88 degrees it returns zero, matching
fsdb rather than MySQL. The same values result from Clang `-O0`, `-O3`, and
`-O3 -ffp-contract=fast` builds. This rules out a simple uniform choice of
the local Boost strategy as an explanation for all residuals. The exact
coordinate conversion and build arithmetic still need tracing.

MySQL 8.4.11's
[`sql/gis/distance_functor.cc`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/distance_functor.cc)
constructs `distance::andoyer`
for point pairs and `geographic_cross_track<andoyer>` for other geographic
pairs. Its
[`Geographic_point`](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/geometries.h)
coordinates are stored in radians. The native
probe now also constructs radian-coordinate Boost points explicitly; all
three controls above remain unchanged. Strategy selection and a basic
degree-versus-radian Boost coordinate type difference therefore do not
explain the residuals. MySQL's WKT conversion and Linux build arithmetic
remain to be checked before changing fsdb's point-distance formula.

Four candidate arithmetic changes were built in Release and run through the
full edge oracle. All were reverted:

| Trial | Near-hole point-distance mismatches | Result |
| --- | ---: | --- |
| Baseline | 8/189 | Reference |
| Fuse reduced-length subtractions | 8/189 | Identical output |
| Subtract converted longitudes | 8/189 | Identical output |
| Fuse point-distance cosine | 9/189 | New mismatch at `62 2.99999` |
| Multiply coordinates by radians-per-degree | 10/189 | New mismatches at `86.00001 1.00001` and `86.00001 2.99999` |

The next production change needs a more complete trace of MySQL's selected
endpoint/interior strategy and its floating-point operations. The test now
prints point-to-vertex controls for the residuals so that any fix can be
checked against both polygon and primitive distances.
