<!--
sha: 0f6b791
baseline: e56d6a5
date: 2026-09-07T12:29:43Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated pass-through CTE equality

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The earlier fsdb result used the same benchmark, schema, dataset,
and machine. These figures are directional rather than release-grade latency
estimates.

| Workload | Earlier fsdb | Indexed fsdb | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Correlated CTE equality | 108.714 ms | 1.331 ms | 81.7x | 221.7 us | 6.0x |

Simple pass-through CTE bindings now retain a lazy physical projection. A
correlated equality can use the underlying persistent index without forcing
the complete CTE result. Other CTE shapes retain eager materialization and the
statement-local typed lookup path.
