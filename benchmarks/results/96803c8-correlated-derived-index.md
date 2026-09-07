<!--
sha: 96803c8
baseline: e56d6a5
date: 2026-09-07T12:11:57Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb after; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

# Correlated pass-through derived equality

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The earlier fsdb result used the same benchmark, schema, dataset,
and machine. These figures are directional rather than release-grade latency
estimates.

| Workload | Earlier fsdb | Indexed fsdb | Improvement | MySQL 8.4 | fsdb / MySQL |
|---|---:|---:|---:|---:|---:|
| Correlated derived equality | 125.433 ms | 1.211 ms | 103.5x | 227.3 us | 5.3x |

The derived source is a direct projection over one physical table. fsdb now
maps its correlated equality back to the stored column and resolves candidates
through that column's persistent index. Complex derived sources continue to use
the materialized lookup or general correlated execution paths.
