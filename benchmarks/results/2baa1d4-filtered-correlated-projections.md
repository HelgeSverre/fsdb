<!--
sha: 2baa1d4
baseline: a098acb
date: 2026-09-07T13:10:00Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Filtered correlated projections

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The before and after engines used the same benchmark diff, schema,
dataset, and machine.

| Workload | fsdb before | fsdb after | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Filtered derived equality | 69.248 ms | 1.649 ms | 42.0x | 306.329 ms | 0.005x |
| Filtered CTE equality | 72.465 ms | 1.738 ms | 41.7x | 297.154 ms | 0.006x |

The planner now preserves stable source-local predicates while composing a
direct-column derived table or CTE back to its immutable physical table root.
The correlated equality probe first narrows rows through the stored index, then
evaluates the source predicate only for those candidates. Effectful predicates,
subqueries, and unsupported projection shapes retain one-time materialization.

MySQL's figures reflect its plan for these exact correlated query shapes. They
are useful compatibility baselines, not evidence that fsdb is generally faster
than MySQL.
