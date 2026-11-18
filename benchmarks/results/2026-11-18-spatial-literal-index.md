# Spatial index lookup from client SQL

Client SQL carries connection collation on string literals. The spatial
planner matched only bare `VString` literals, which the direct executor
tests used. It skipped the index for client queries even though the index
was present. Commit `899d38db` extracts text through the shared AST literal
pattern and uses the index for supported planar predicates involving the
original builtins.

The new client regression failed with `ALL` on the old implementation.
It now verifies range access and equal rows with and without the index
under utf8mb4 and utf8mb3. The full 3,958-test suite and the isolated package
consumer passed. A three-point probe produced the same rows on native
MySQL 8.4.11, which also chose the spatial index.

## Native latency

The same [FilterBySpatialBounds](../Fsdb.Benchmarks/ServerBenchmarks.fs)
fixture as the [broader comparison](2026-11-18-native-mysql-comparison.md)
ran on an Apple M2 Max Mac, macOS 15.6, and .NET 10.0.12
(SDK 10.0.401). The fixture includes 10,000 indexed points, 10,000 users,
50,000 orders, and 10,000 articles. The baseline binary matches the
production source at `316b0292`, and the candidate is `899d38db`.
Both Fsdb binaries were built in Release and ran in memory. MySQL 8.4.11
ran natively with its normal InnoDB commit flush and binary logging off.

Each trial starts from a fresh seeded database. The order of targets varies
across three trials. Both workload cases warm for at least 0.35 s and time
operations for at least 1.25 s. Results are medians of trial means.
[Raw samples](2026-11-18-spatial-literal-index.csv).

| Workload | Fsdb before | Fsdb after | MySQL 8.4.11 |
| --- | ---: | ---: | ---: |
| Spatial bounding-box lookup | 44.167 ms | 0.277 ms | 0.057 ms |
| Primary-key read control | 0.332 ms | 0.334 ms | 0.039 ms |

For the representative query centered at (5000, 5000), every trial
returned the same IDs (4999, 5000, and 5001) on all three targets.
Fsdb changed from `ALL` with 10,000 estimated rows to `range` on
`sx_places_location` with three candidate rows. MySQL used the same index
with a one-row estimate. The query is about 160× faster than before.
MySQL remains about 4.8× faster on this fixture.
