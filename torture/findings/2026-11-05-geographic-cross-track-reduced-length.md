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

Changing the endpoint projection formula to Boost's signed projections did not change the oracle counts. Keeping the interpolated point in radians inside fsdb's Newton loop also left all nine distance mismatches. Both trials were reverted. The remaining difference is sensitive to the Andoyer azimuth and differential-quantity inputs at nearly coincident points. The next implementation step should compare those intermediate radians and the direct-point formula against Boost before changing stopping rules or adding a distance threshold.

The [MySQL-pinned cross-track strategy](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/geographic/distance_cross_track.hpp) and [Andoyer differential quantities](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/formulas/differential_quantities.hpp) are the source references for this comparison. The native probe is a diagnostic and is not part of fsdb's build.
