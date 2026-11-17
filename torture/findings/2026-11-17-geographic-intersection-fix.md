# Geographic long-edge crossing correction

Status: fixed for both recorded long-edge cases and the audited corpus.
The later [polygon validity implementation](2026-11-17-geographic-polygon-validity.md)
also supports audited holes and multipolygons.

The [strategy investigation](2026-11-17-geographic-intersection-strategy.md)
isolated the remaining ring-76 disagreement to geographic segment crossing.
MySQL 8.4.11 reports a crossing and zero distance; FSDB's sampled arcs missed
it and returned approximately 183.6 km. Raising the sample count did not
recover MySQL's decision.

## Implementation

`GeographicIntersection.fs` implements the first-order Sjöberg calculation,
with a spherical starting point, Newton iteration, longitude checks, and the
convergence fallback. The caller preserves MySQL's exact SRS angular unit,
normalized ellipsoid, latitude-ordered endpoints, azimuth side checks, and
signed segment ranges. The Boost source attribution and licence accompany
the implementation; the NuGet package includes the licence and package
verification checks its contents.

Line topology, line distance, and simple-ring validity share the proper
crossing predicate. The obsolete spherical-arc intersection implementation
and vector helpers are removed. Other polygon sampling paths remain.

Endpoint contact and overlap retain the existing segment-membership rules.
This matters for zero distance: Andoyer can round identical-point distances
to approximately 9.5 cm. The endpoint corpus caught that integration
regression, and the full suite caught a collinear meridian boundary case.
Both retain zero distance through explicit contact handling. The latter is
covered by the existing line/polygon boundary regression; the former adds
another shared-endpoint fixture.

## Oracle evidence

- `just check` passes all 3,956 tests. `just package-check` passes isolated
  consumption and the packaged licence check. Ruff linting and formatting pass.
- The new Expecto crossing test failed before the implementation and passes
  for both endpoint directions and both operand orders. It checks
  intersects/crosses/touches, zero distance, and the full ring's validity.
- The [focused SQL comparison](2026-11-17-geographic-intersection-fixed.json)
  matches pinned MySQL 8.4.11 in all eight orderings.
- The [endpoint and ring corpus](2026-11-17-geographic-intersection-corpus.json)
  has no differences for 1,000 shared-endpoint triples and 3,000 global rings
  across seeds 9173, 9174, and 9175. Both formerly recorded rings now agree.
- The [independent line-pair comparison](2026-11-17-geographic-intersection-lines.json)
  uses seed 33076 and 1,000 global pairs per SRS for WGS 84 (4326), GRS 1980
  (4258), and Clarke 1866 (4267). All topology and zero-distance classifications
  agree. This comparison does not assess accuracy of nonzero distances.

Reproduction commands and dependency locks are in the
[oracle probe](../probes/geographic-endpoints/README.md). The reviewed validity
and segment-relation mismatch ledgers are now empty. This evidence resolves
the recorded cases; it does not establish general geographic SQL parity or
enable unsupported polygon validity shapes.
