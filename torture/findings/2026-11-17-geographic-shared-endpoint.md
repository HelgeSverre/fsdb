# Long geographic segments sharing an endpoint

Status: fixed for the audited shared-endpoint case and corpus. The unrelated
nonadjacent intersection and distance discrepancy in ring 76 remains open.

Pinned MySQL 8.4.11 reports `(intersects,crosses,touches,distance)=(1,0,1,0)`
for these two EPSG 4326 lines:

```text
LINESTRING(-17.23251258 -93.57657163,-1.62166186 119.40335360)
LINESTRING(-1.62166186 119.40335360,-27.07466083 -119.20350262)
```

FSDB reported a proper crossing instead of a touch, also making the recorded
ring 92 invalid. The focused regression reproduced `ST_Crosses=1` instead
of zero. Sampled long arcs introduced a spurious interior crossing despite
the original segments sharing their boundary endpoint.

`geographicSegmentsProperlyCross` now handles an exactly shared endpoint
before sampling. Whole-line topology still separately tests whether a shared
segment vertex lies in both complete line interiors. Collinear overlap and
backtracking checks also remain separate from this proper-crossing test.

## Validation

- `just check` passed all 3,955 tests.
- The F# regression checks all four line orientations, intersects/crosses/
  touches classification, zero distance, and the full ring's validity.
- A fresh pinned-oracle comparison found no differences for 1,000 global
  shared-endpoint triples generated with seed 33092.
- On the existing three-seed, 3,000-ring corpus, only case 76 in seed 9173
  differs: MySQL invalid, FSDB valid. Case 92 now agrees, and no other ring
  difference was introduced.
- The reusable [oracle probe](../probes/geographic-endpoints/README.md) has
  pinned dependencies in uv.lock and passes Ruff linting and formatting.

The [machine-readable result](2026-11-17-geographic-shared-endpoint.json)
records the oracle version, seeds, counts, and mismatch sets. These tests do
not establish general geodesic intersection parity. The remaining ring-76
crossing and approximately 183.6 km distance error still require the full
geographic intersection strategy described in the earlier finding.
