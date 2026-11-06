# Geographic cross-track reduced-length diagnostic

The current MySQL 8.4.11 edge oracle has nine near-hole point-distance mismatches among 189 points. One isolated case is:

```sql
SELECT ST_Distance(
  ST_GeomFromText('POINT(3 1.00001)',4326),
  ST_GeomFromText('LINESTRING(3 1,3 3)',4326));
```

MySQL returns 0 m; fsdb returns 0.0950425203706804 m. The maintained polygon oracle selects this same hole edge in both engines, so this is a point-to-segment calculation difference.

The [native probe](../native/geographic-cross-track-probe.cpp) uses Boost 1.84's Andoyer geographic cross-track strategy on WGS 84 with the matching longitude/latitude coordinates. It returns 0 m, matching MySQL. The probe was compiled with the official Boost 1.84.0 source archive (`boost_1_84_0.tar.gz`, SHA-256 `a5800f405508f5df8114558ca9855d2640a2de8f0445f051fa1c7c3383045724`):

```sh
c++ -std=c++17 -I /path/to/boost_1_84_0 \
  torture/native/geographic-cross-track-probe.cpp -o /tmp/geographic-cross-track-probe
/tmp/geographic-cross-track-probe
```

Temporary instrumentation of fsdb and Boost's debug trace found that both evaluate the first trial point near longitude 1.0000100433293584°, latitude 3.0000000092238248°, and both measure 0.0950425203706804 m there. Boost's first inverse reports reduced length `m34 = -0.0000346230632847879` m and moves the along-track position from about 1.116496813 m to 1.115670896 m. Fsdb's corresponding reduced length is about `0.004641993794` m and moves from 1.116496807 m to 1.227228618 m. Boost reaches zero distance on its next iteration; fsdb stops with the first distance.

Changing the endpoint projection formula to Boost's signed projections did not change the oracle counts. Keeping the interpolated point in radians inside fsdb's Newton loop also left all nine distance mismatches. Both trials were reverted. The remaining difference is sensitive to the Andoyer azimuth and differential-quantity inputs at nearly coincident points.

## Exact-radian follow-up

The native probe now repeats Boost's first inverse with its direct point's
exact radian longitude `0.017453467809108461` and two adjacent representable
latitudes. With latitude `0.052359877720815996`, Boost reports forward
azimuth `-1.779458324947162`, reverse azimuth `-1.779458324290064`, and
reduced length `-0.00003462306328478791` m. With latitude
`0.052359877720815989`, reduced length changes sign to
`+0.00005098149074482166` m. The query radians in both cases are computed
from Boost's degree conversion. This shows that a single rounding step can
change the Newton direction in this case.

An instrumented fsdb trial kept the direct point in radians through the
inverse calculation and used Boost's degree-to-radian multiplication order.
Its first point and query radians then matched the native probe exactly, but
its forward and reverse azimuths were both about `-1.7794583206201624` and
its reduced length remained about `+0.0046419943` m. Aligning the algebraic
grouping of the Andoyer azimuth terms did not resolve the difference. The
remaining discrepancy is in the near-coincident inverse arithmetic, including
trigonometric rounding and azimuth corrections, rather than solely in the
direct-point round trip. Those trials were reverted after the focused MySQL
regression continued to fail. A targeted inverse implementation or a
geometric robustness rule needs broader oracle coverage before adoption.

The [MySQL-pinned cross-track strategy](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/geographic/distance_cross_track.hpp) and [Andoyer differential quantities](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/formulas/differential_quantities.hpp) are the source references for this comparison. The native probe is a diagnostic and is not part of fsdb's build.

## Longitude-difference conversion control

Fsdb's general Andoyer azimuth helper converts the longitude difference from
degrees to radians, whereas Boost converts both longitudes to radians before
subtracting them. A Release trial changed only that conversion order. The
maintained MySQL 8.4.11 edge oracle remained at 12/412 line-relation,
1/189 near-hole point-relation, and 9/189 near-hole point-distance mismatches;
the isolated `POINT(3 1.00001)` distance remained 0.0950425203706804 m in
fsdb versus zero in MySQL. All 100 seeded point-to-line distances matched.
The trial was reverted. Longitude-difference conversion alone does not
explain the near-coincident inverse discrepancy; the next diagnostic should
compare the first inverse's `A`, `B`, `U`, `V`, and `d/sin(d)` terms on identical
radian inputs before changing cross-track behavior.
