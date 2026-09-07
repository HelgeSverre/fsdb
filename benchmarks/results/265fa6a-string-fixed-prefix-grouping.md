<!--
feature-sha: 265fa6a
measured-head: 23e3114
baseline: 9324722
date: 2026-09-07T19:39:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# String fixed-prefix indexed grouping

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. Both fsdb revisions and MySQL used the same deterministic schema,
dataset, workload, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| `WHERE status = 'paid' GROUP BY status_bucket` over `(status, status_bucket)` | 33.708 ms | 2.550 ms | 13.2× | 1.788 ms | 1.43× |

The first key uses `utf8mb4_0900_bin`, whose comparison equality is also an
ordering equivalence class. The planner can therefore seek the exact `status`
slice and stream its 64 adjacent integer groups. A folded or PAD SPACE
collation cannot make that claim and retains the scan/group fallback.

These short-run figures establish the effect of the access path; they are not
a general throughput claim for either engine.
