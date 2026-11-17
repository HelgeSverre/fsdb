# Geographic intersection strategy: long-edge crossing

Status: open. This investigation provides a focused failing SQL comparison
and a native reference for the remaining ring-76 crossing. No production
geometry algorithm changes are included.

## Reproduction

The [SQL result](2026-11-17-geographic-intersection-oracle.json) records pinned
MySQL 8.4.11 and FSDB at `73d289e3`. Both endpoint directions and both operand
orders are tested, giving eight equivalent comparisons. Every MySQL result
is `(intersects,crosses,touches,distance)=(1,1,0,0)`. FSDB reports `(0,0,0)`
and a distance of approximately 183,641.78 metres.

The focused assertion intentionally fails until parity is implemented:

```sh
docker compose -f torture/compose.yaml up -d --wait mysql
dotnet build src/Fsdb.Cli -c Release --nologo
cd torture/probes/geographic-endpoints
uv sync --locked
uv run python check.py --crossing
```

The default `check.py` mode retains the existing known-gap corpus contract.
The focused mode asserts parity rather than accepting the current mismatch.

Validation: `just check` passed all 3,955 tests; the default probe matched
all 1,000 endpoint triples and retained only ring 76 in the 3,000-ring corpus.
Ruff linting and formatting passed. The native probe compiled without warnings
under `-Wall -Wextra` and passed its audited outcome assertions.

## Native reference

[`geographic-intersection-probe.cpp`](../native/geographic-intersection-probe.cpp)
compares Boost's public geographic intersection strategy with its first-order
Sjöberg solver and signed Andoyer distance ratios. It orders segment endpoints
by latitude and uses the normalized WGS 84 spheroid, following the strategy.
It separately samples Boost's Andoyer/Thomas interpolation and intersects the
resulting spherical arcs.

Reference: Boost 1.84.0 source archive, SHA-256
`cc4b893acf645c9d4b698e9a0f08ca8846aa5d6c68275c14c3e7949c24109454`.
This matches the archive's published checksum. The recorded run uses Apple
Clang 17.0.0 on arm64, compiled with:

```sh
clang++ -std=c++17 -O3 -Wall -Wextra -I/path/to/boost_1_84_0 \
  torture/native/geographic-intersection-probe.cpp -o /tmp/geographic-intersection-probe
/tmp/geographic-intersection-probe
```

The probe exits nonzero if the audited outcomes change. Its
[recorded output](2026-11-17-geographic-intersection-native.txt) shows:

| Conversion | Public intersections | Solver candidate, latitude/longitude | Signed segment ratios |
| --- | --- | --- | --- |
| MySQL unit `0.017453292519943278` | 1 | 2.3790772643°, 89.9572144136° | 0.9255432020, 0.1175485587 |
| MySQL unit, operands swapped | 1 | 2.3790772643°, 87.2273886258° | 0.0992357485, 0.9169937269 |
| π/180 | 0 | 38.0370569117°, 115.1710510692° | −0.8689069275, 0.5693333102 |

Endpoint reversal does not change these results. Side tests report opposite
signs for both segments in both conversion modes, so those signs alone do
not explain the public intersection difference. The candidate from π/180 is
outside one segment, while the exact-unit candidates pass both range checks.
The operand-dependent candidates also caution against treating the returned
coordinate as a generally accurate intersection location.

Temporary tracing of the reference header found that all tested solver calls
reach the Sjöberg convergence fallback after the Newton result fails the
longitude check. The exact-unit, swapped-operand calculation can terminate
with a nonzero longitude residual of approximately 0.047644448 radians.
The tracing is removed from the reference used for the recorded output.

For each angular unit, interpolating with 256 and 4,096 samples still reports
no spherical-arc intersection. This rules out those denser sampling choices
as a fix for the audited case; it does not establish a general sampling bound.

## Implementation consequence

Compatibility requires the complete geographic segment decision: exact SRS
conversion, spheroid normalization, endpoint ordering, side and degenerate
cases, the iterative intersection calculation, and signed segment ranges.
A shared kernel must feed line topology, line distance, and ring validity.
Replacing only the side test or increasing samples does not implement this
contract. Earlier side-only trials introduced false invalid rings.

The next implementation must preserve shared-endpoint and overlap handling
and validate the focused eight comparisons, the 1,000 endpoint triples,
the three-seed 3,000-ring corpus, and existing geographic relation tests.

Primary algorithm sources: Boost 1.84
[`geographic/intersection.hpp`](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/strategies/geographic/intersection.hpp)
and [`sjoberg_intersection.hpp`](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/formulas/sjoberg_intersection.hpp).
