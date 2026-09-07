<!--
sha: e883956
baseline: 3bb5826
date: 2026-09-07T21:13:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders
-->

# Correlated count cardinality

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same schema, deterministic
dataset, workload, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Direct derived equality | 1.041 ms | 476.199 µs | 2.19× | 207.540 µs | 2.29× |
| CTE equality | 979.586 µs | 508.880 µs | 1.93× | 210.895 µs | 2.41× |
| Nested derived equality | 1.170 ms | 492.035 µs | 2.38× | 215.580 µs | 2.28× |
| Chained CTE equality | 997.104 µs | 516.580 µs | 1.93× | 213.257 µs | 2.42× |

The optimized path caches the physical direct-column projection once per
statement and consumes the equality index's row-id cardinality for a plain
`COUNT(*)`. Source predicates, extra residuals, repeated equalities,
collation-sensitive columns, failed normalization, and `NULL` values retain
the row-producing path.

These short-run figures establish the cost of these exact query shapes; they
are not a general throughput claim for either engine.
