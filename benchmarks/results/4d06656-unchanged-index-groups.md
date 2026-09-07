<!--
sha: 4d06656
baseline: 8df04cf
date: 2026-09-07T21:54:48Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb before/after; durable MySQL
dataset: 10000 users, 50000 orders
-->

# Unchanged index groups

BenchmarkDotNet ShortRun: one launch, three warmups, and three measured
iterations. The fsdb measurements use the same schema, deterministic dataset,
workloads, and machine.

| Workload | fsdb before | fsdb after | Reduction | MySQL 8.4 after |
|---|---:|---:|---:|---:|
| 16 concurrent point updates | 3.838 ms | 3.467 ms | 9.7% | 1.010 ms |
| Transaction with two point updates | 962.7 µs | 903.4 µs | 6.2% | 216.3 µs |

An update now preserves the immutable root of each unique, equality, or
ordered index group whose projected key and row identity did not change. Key
changes, insertions, deletions, and replacements retain their full index
maintenance paths.

These short-run figures isolate the affected update paths. They are not a
general throughput claim, and the MySQL figure for the concurrent burst is
noisy enough that only the fsdb before/after comparison should guide this
change.
