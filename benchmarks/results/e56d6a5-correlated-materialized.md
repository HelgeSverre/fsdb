<!--
sha: e56d6a5
baseline: 4e11f97
date: 2026-09-07T11:47:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated materialized-source equality

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same benchmark host, schema,
dataset, and machine. These figures are directional rather than release-grade
latency estimates.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 |
|---|---:|---:|---:|---:|
| Correlated derived equality | 1.596 s | 125.433 ms | 12.7x | 209.9 us |
| Correlated CTE equality | 1.809 s | 108.714 ms | 16.6x | 210.6 us |

The fsdb path now builds one typed equality lookup over the materialized rows
instead of scanning those rows for every outer value. MySQL remains much faster
because it can merge these simple query blocks into its indexed physical plan;
fsdb still pays to materialize and key the complete source on every statement.
