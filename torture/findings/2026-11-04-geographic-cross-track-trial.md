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

The focused spatial suite passed all 46 tests, and the full suite passed all
3,875 tests after this change. The added point-to-line regression pins the
resolved MySQL case.
