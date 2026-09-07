<!--
sha: e09b2fc
baseline: 09583a0
date: 2026-09-07T13:46:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated range projections

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same benchmark diff, schema,
dataset, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Derived range | 1,503.9 ms | 22.287 ms | 67.5x | 440.895 ms | 0.051x |
| CTE range | 1,321.7 ms | 21.718 ms | 60.9x | 442.704 ms | 0.049x |

The correlated source path now shares the literal-range bound collector and
uses the physical table's immutable ordered index through direct-column
derived and CTE projections. NULL bounds resolve to an empty candidate set.
The planner retains one-time materialization whenever the indexed slice would
cost at least as much as a table scan.

MySQL's figures reflect its plan for these exact correlated query shapes. They
are useful compatibility baselines, not evidence that fsdb is generally faster
than MySQL.
