<!--
sha: 8db89cf
baseline: 623d1d1
date: 2026-09-07T20:43:58Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders
-->

# Fixed-prefix suffix-range grouping

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same schema, deterministic
dataset, workload, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| `status = 'paid' AND status_bucket >= 16 AND status_bucket < 32 GROUP BY status_bucket` | 3.702 ms | 702.142 µs | 5.27× | 733.770 µs | 0.96× |

The ordered index already enforces the exact string prefix and both suffix
bounds. The optimized path now consumes adjacent key cardinalities directly
when those predicates cover the complete `WHERE` clause. Failed bound
normalization, extra residual predicates, and competing bounds retain row
resolution and predicate evaluation.

These short-run figures establish the cost of this exact query shape; they
are not a general throughput claim for either engine.
