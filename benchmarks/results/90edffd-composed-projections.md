<!--
sha: 90edffd
baseline: e8364a2
date: 2026-09-07T12:47:59Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Composed correlated projections

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same benchmark diff, schema,
dataset, and machine. These figures are directional; the chained-CTE baseline
and both MySQL samples showed substantial iteration variance.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Nested derived equality | 227.545 ms | 6.782 ms | 33.6x | 401.5 us | 16.9x |
| Chained CTE equality | 291.452 ms | 1.584 ms | 184.0x | 345.0 us | 4.6x |

The planner now composes direct-column mappings across nested derived tables
and chained CTEs, retaining one immutable physical table root and its stored
index. Each layer can rename or reorder columns without materializing the full
intermediate result. Complex projections retain the existing fallback.
