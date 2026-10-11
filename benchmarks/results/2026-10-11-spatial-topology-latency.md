# Spatial topology wire latency snapshot

Measured at `9f56c9d4` on 2026-10-11 with native MySQL 8.4.11 and a native
Release fsdb process on the same Apple Silicon host. Fsdb used its in-memory
mode; every query was read-only. Persistent PyMySQL connections fetched each
one-row result, and both servers returned the same values before timing.

Each query had 30 warmups, then five batches of 100 queries. The first pass
used the listed order, the second reversed it, and three more passes alternated
direction. Target order alternated by batch. The first two passes showed large
warmup or host drift: fsdb's `SELECT 1` median fell from 264 to 117 µs/query;
some spatial cases shifted even more. The table therefore reports the median
of the **last three pass medians**, with their range for fsdb. The complete
batch timings and SQL are in the [raw results](2026-10-11-spatial-topology-latency.json).

| Query | MySQL µs/query | fsdb µs/query | fsdb pass range |
|---|---:|---:|---:|
| `SELECT 1` | 50.8 | 121.2 | 99.6–121.6 |
| Planar crossing lines: `ST_Intersects` | 56.6 | 130.0 | 118.3–187.2 |
| Same lines: `ST_Crosses` | 56.5 | 132.4 | 128.5–147.7 |
| Planar overlapping polygons: `ST_Intersects` | 56.0 | 128.8 | 125.7–130.0 |
| Same polygons: `ST_Overlaps` | 59.2 | 141.3 | 140.0–143.1 |
| Geographic multipoint/line: `ST_Intersects` | 62.6 | 128.4 | 127.4–137.9 |
| Same geometries: `ST_Crosses` | 62.8 | 128.1 | 123.1–141.0 |

## Results

In the stabilized passes, the new predicates are close to the corresponding
`ST_Intersects` controls. The planar polygon `ST_Overlaps` query is modestly
higher, but these short wire queries are dominated by fixed request costs. The
fsdb `SELECT 1` control is itself roughly twice MySQL's in the final passes.
Early drift, a limited number of batches, and the absence of a pre-change
baseline prevent a precise speedup or regression claim. This snapshot does
not identify predicate arithmetic as the next performance bottleneck.
