# Geographic polygon validity with holes and multipart shapes

Status: supported for the audited shapes and corpus. Geographic `ST_IsValid`
now returns a boolean for polygons containing holes, multipolygons, and
collections containing them. The previous explicit unsupported response is
removed. This does not establish general geographic predicate parity.

## Behavior

The [baseline](2026-11-17-geographic-polygon-validity-baseline.md) independently
recorded the missing behavior before implementation: MySQL 8.4.11 classified
707 inputs while FSDB returned error 1235 for every input. The new Expecto
regression also reproduced that unsupported response before the change.

Validation now combines:

- Simple geodesic ring validity and consecutive-duplicate normalization.
- Proper edge crossings, shared boundary intervals, and angular order at
  endpoint contacts. Crossing at a vertex is distinct from a permitted touch.
- Hole containment and nesting, including local edge-side classification
  when all vertices touch another boundary.
- A bipartite graph of rings and distinct contact points. Cycles reject a
  disconnected polygon interior; several holes meeting at one common point
  remain valid.
- Valid individual components and disjoint filled interiors for multipolygons.
  A component inside another component's hole can be valid, and multiple
  point contacts between otherwise disjoint components are permitted.
- Recursive validity for geometry collections.

The internal validator now returns a boolean directly. Ring cleanup is shared
between simple-ring checks and orientation normalization. No sampled edge
points are used to decide hole/component containment.

## Polar hole winding

The expanded polar corpus caught an incorrect first implementation that
treated every hole as a standalone polygon during component containment.
MySQL's hole winding role can differ from that standalone shell role.

For an outer cap through `(80,-120)`, `(80,0)`, `(80,120)` and a hole through
the corresponding latitude-85 vertices, MySQL rejects a latitude-87 triangular
component. It reports that component within the filled polygon. A small
component near `(84,-1)` instead remains valid. These results hold in the
audited reversed-ring variants too.

The validator therefore uses the existing hole winding predicate for filled
component tests. Boundary-only contact classification still uses local
geodesic sectors. The polar mismatch became a failing Expecto assertion
before this correction; the cap, southern component, and boundary-only island
are permanent regressions. No latitude-specific validity rule is introduced.

## Differential evidence

The [recorded results](2026-11-17-geographic-polygon-validity-results.json)
compare every input against digest-pinned MySQL 8.4.11:

| SRS | Global seed | Cases | MySQL valid | Differences |
| --- | ---: | ---: | ---: | ---: |
| WGS 84, 4326 | 9176 | 1,169 | 397 | 0 |
| WGS 84, 4326 | 9177 | 1,169 | 392 | 0 |
| WGS 84, 4326 | 9178 | 1,169 | 396 | 0 |
| GRS 1980, 4258 | 9176 | 1,169 | 397 | 0 |
| Clarke 1866, 4267 | 9176 | 1,169 | 397 | 0 |

Each run contains 369 fixed fixtures and 800 seeded global inputs. Fixed
fixtures repeat across runs; the seed and SRS table describes the comparison
scope rather than 5,845 independent random shapes. The global inputs include
small triangular holes/components and pairs of large global quadrilaterals.
The fixed fixtures cover independent ring orientations, contact cycles,
shared edges, nesting, inscribed components, collections, antimeridian shifts,
northern/southern high latitudes, and polar caps.

The older 3,000-ring and 1,000-shared-endpoint control also passes without
differences. `just check` passes all 3,957 tests, `just package-check` passes
isolated consumption, and scoped Ruff linting/formatting pass. Reproduction
commands and dependency locks are in the
[oracle README](../probes/geographic-endpoints/README.md).

The existing near-boundary point/line relation and distance precision findings
remain tracked in `GAPS.md`; this change does not claim to resolve them.
