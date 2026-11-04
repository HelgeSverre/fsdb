# Geographic point-to-segment cross-track refinement

MySQL 8.4.11 uses Boost 1.84's geographic cross-track strategy with Andoyer
for non-point distance. Its [distance functor](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/gis/distance_functor.cc)
selects the [cross-track implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/geographic/distance_cross_track.hpp).
Fsdb previously minimized point-to-point distance along a Thomas geodesic
with a golden-section search. This change uses a spherical along-track seed
and refines it with Andoyer inverse quantities and Thomas direct points.
Endpoint and degenerate-segment handling remains bounded by the endpoint
distance.

The maintained [edge oracle](../scripts/geographic-edge-oracle.py) was run
against Release fsdb and native MySQL 8.4.11 before and after the change:

| Corpus | Before | After |
|---|---:|---:|
| 189 near-hole point-to-polygon distances, 0.01 m tolerance | 34 mismatches | 22 mismatches |
| 100 seeded geographic point-to-line distances, 0.01 m tolerance | 44 mismatches | 44 mismatches |
| 412 line/polygon relations | 13 mismatches | 13 mismatches |
| 189 near-hole point relations | 3 mismatches | 3 mismatches |
| Four near-exterior point controls | 0 mismatches | 0 mismatches |

The near-hole distance change resolves 15 previously differing cases and
introduces three new ones. One resolved case is `POINT(2.5 1.00001)` to
`LINESTRING(1 1,3 1)`, now within 0.01 m of MySQL's 2.1689634565 m.
The 100-case sample has the same mismatch count; it does not establish parity
for broad geographic input. The remaining 22 near-hole differences include
near-corner edge selection and zero-distance classifications. The separate
topology disagreements remain open.

A current-head replay after preview.46 isolated all 22 remaining polygon
distance differences to the hole boundary: each engine's polygon distance
equals its own hole `LINESTRING` distance in every differing case. Both
engines choose the same nearest hole segment in 13 of the 22 cases; the
other nine also need segment-selection parity. Several high-latitude cases
return zero on only one engine. This rules out polygon distance aggregation
as the cause on this corpus and directs the next fix to the geographic
point-to-segment strategy. The same replay retained 13/412 line relation
mismatches, 3/189 near-hole point relation mismatches, and 44/100 seeded
point-to-line distance mismatches.

A local-distance fallback was trialed for the cross-track iteration when
Andoyer rounds a nonidentical point pair to zero. It used the ellipsoid's
meridian and parallel radii at the pair's mean latitude. The near-hole
distance count worsened from 22 to 25 mismatches, while the seeded
point-to-line count remained 44. The trial was reverted. Zero is also an
observable MySQL cross-track outcome in this corpus, so replacing it with a
locally more accurate metric is not a general compatibility fix.

The focused spatial suite passed all 46 tests, and the full suite passed all
3,875 tests after this change. The added point-to-line regression pins the
resolved MySQL case.

## Endpoint ordering follow-up

The [Boost 1.84 cross-track implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/extra/boost/boost_1_84_0/boost/geometry/strategies/geographic/distance_cross_track.hpp)
orders the segment endpoints by longitude before its inverse azimuths,
endpoint checks, and Newton seed. Fsdb previously kept WKT order. Applying
that general rule resolved all 44 differences in the 100 seeded point-to-line
distance cases, at the same 0.01 m tolerance. It changed the near-hole
point-to-polygon distance count from 22 to 23 mismatches across 189 points;
the added mismatch is `POINT(86 2.99999)` at a near-polar hole corner.
The 412 line relation and 189 point relation corpora retained 13 and three
mismatches respectively. This is a substantial broader-distance gain, but
the high-latitude corner behavior still needs the remaining Boost strategy
details. Two formerly mismatching seeded line cases are pinned in the focused
regression. All 31 geographic tests and the full 3,875-test suite passed.

## Meridian endpoint ordering follow-up

Boost also orders the latitudes of a same-longitude meridian segment by
absolute value before computing inverse azimuths. Applying that rule in fsdb
reduced near-hole point-to-polygon distance mismatches from 23 to 10 of 189.
The 100 seeded point-to-line distances still have zero mismatches. The 412
line relations and 189 point relations remain at 13 and three mismatches.
The newly resolved high-latitude `POINT(41.00001 2.99999)` case is pinned in
the point-to-polygon regression. The ten remaining distance cases include
near-corner segment selection and numerical zero-distance disagreements.
