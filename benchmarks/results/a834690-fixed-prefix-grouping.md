<!--
sha: a834690
baseline: 8ca2439
date: 2026-09-07T18:24:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 100 users, 100000 orders, 1 article
-->

# Fixed-prefix indexed grouping

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same schema, deterministic
dataset, workload, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| `WHERE user_id = 1 GROUP BY status` | 47.886 ms | 333.985 µs | 143.4× | 257.561 µs | 1.30× |

The planner now carries exact leading-key values into the immutable ordered
index lookup. Storage seeks the matching composite-key interval, and simple
fully covered aggregates consume adjacent key counts directly. Predicates not
fully represented by the fixed prefix still evaluate against every candidate
row in the bounded slice.

These short-run figures establish the performance slope of this query shape;
they are not a general throughput claim for either engine.
