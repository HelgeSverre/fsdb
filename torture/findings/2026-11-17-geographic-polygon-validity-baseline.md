# Geographic hole and multipart validity baseline

Status: resolved for the audited corpus by the
[polygon validity implementation](2026-11-17-geographic-polygon-validity.md).
This report preserves the unsupported-response baseline recorded before that
implementation.

## Baseline evidence

The [recorded result](2026-11-17-geographic-polygon-validity-baseline.json)
compares source revision `d59b4380` with the digest-pinned MySQL 8.4.11 image:

| Result | Cases |
| --- | ---: |
| MySQL valid | 270 |
| MySQL invalid | 437 |
| FSDB error 1235 | 707 |

The corpus includes fixed contact and containment cases, every independent
ring-orientation combination for the local hole fixtures, both component
orders, geometry collections, and 300 global shell/triangle configurations
tested as both polygons with holes and multipolygons. Global coordinates use
seed 9176. The two global component pairs from the earlier containment
finding are included too.

Those two former component-relation blockers now agree: MySQL and FSDB both
return `(intersects,within)=(0,0)`. MySQL considers both corresponding
multipolygons valid. The current unsupported result is therefore a remaining
validator boundary, rather than evidence that the earlier containment bug
still blocks these examples.

## Contact cases that constrain the implementation

| Configuration | MySQL validity |
| --- | --- |
| Hole strictly inside shell | Valid |
| Hole outside or crossing shell | Invalid |
| Hole touches shell at one vertex or edge point | Valid |
| Hole shares a shell edge | Invalid |
| Hole touches opposite shell meridians at two points | Invalid |
| Two holes touch at one point | Valid |
| Nested or duplicated holes | Invalid |
| Two holes and shell form a contact cycle | Invalid |
| Three holes meet at a single common point | Valid |
| Component lies inside another component's hole | Valid |
| Component lies inside another component's filled region | Invalid |
| Components touch at two points without interior overlap | Valid |
| Component has all vertices on the other shell but fills its interior | Invalid |

A graph containing only rings and pairwise-contact edges would falsely
reject three holes meeting at one common point. Boost's
[complement graph](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/is_valid/complement_graph.hpp)
uses separate ring and intersection-point nodes. Its connectivity check also
belongs to each polygon: applying one contact-cycle rule across multipolygon
components would reject the audited valid two-point component contact.

The boundary-only fixture uses a shell spanning `(0,0)` through `(10,10)`
and an inscribed triangle with vertices `(0,5)`, `(5,10)`, and `(5,0)`.
Both servers report `touches=1,within=0` for each triangle vertex against
the shell, while MySQL rejects their combined multipolygon. Checking
containment only at vertices therefore cannot decide filled-interior overlap.
These contacts use meridians and the equator, avoiding a sampled curved-edge
boundary assumption. Likewise, the current proper-crossing predicate
deliberately excludes endpoint contact; it does not classify ring turns at
shared vertices or contacts within another edge. These cases need explicit
turn and overlap classification before the unsupported guard can be removed.

The reference pipeline is Boost's
[polygon validator](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/is_valid/polygon.hpp)
and [multipolygon validator](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/algorithms/detail/is_valid/multipolygon.hpp):
ring checks, acceptable turns, hole containment and nesting, polygon interior
connectivity, and disjoint filled interiors between components. The oracle
matrix tests these distinctions independently of any F# implementation.

## Reproduction and validation

Follow the pinned-server setup in the
[oracle README](../probes/geographic-endpoints/README.md), then run:

```sh
uv run python check.py --polygons
```

The command prints the complete fixed-fixture oracle outcomes, aggregate
error counts, and the first 20 disagreement examples, then asserts parity
over every case. On the baseline it failed with `geographic polygon validity differs`;
the expanded current corpus passes after the implementation.
The shared-endpoint/ring default mode is unchanged, and `--crossing` still
passes all eight recorded crossing orderings.

Ruff linting and formatting pass. `just check` passes all 3,956 tests. The
previous crossing implementation's [CI run](https://github.com/HelgeSverre/fsdb/actions/runs/44167277538)
also passes build/package, regression,
and branch-coverage jobs. These checks validate the existing implementation
and the new harness; they do not establish hole/multipart validity support.
