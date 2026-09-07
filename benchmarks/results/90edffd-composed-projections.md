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
dataset, and machine. The after/MySQL figures below come from isolated reruns;
the chained-CTE baseline showed substantial iteration variance, so the
improvement figures remain directional.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Nested derived equality | 227.545 ms | 1.253 ms | 181.6x | 237.0 us | 5.3x |
| Chained CTE equality | 291.452 ms | 1.251 ms | 233.0x | 221.5 us | 5.6x |

The planner now composes direct-column mappings across nested derived tables
and chained CTEs, retaining one immutable physical table root and its stored
index. Each layer can rename or reorder columns without materializing the full
intermediate result. Complex projections retain the existing fallback.
