<!--
sha: f4865b1
date: 2026-08-24T18:48:41Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.400
mysql: 8.4.11 Homebrew arm64
targets: in-memory fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 FULLTEXT articles
job: BenchmarkDotNet ShortRun, 3 warmups and 3 measured iterations
ports: fsdb 3410; MySQL 3332
-->

# Full-text search modes

| Workload | fsdb | MySQL 8.4 | fsdb/MySQL |
|---|---:|---:|---:|
| Natural search | 54.191 ms | 383.577 µs | 141.3x |
| Boolean search | 48.937 ms | 1.095 ms | 44.7x |
| Accent-aware search | 49.913 ms | 215.325 µs | 231.8x |
| Boolean prefix search | 52.174 ms | 299.601 µs | 174.1x |

Accent-aware matching stays within the same 49–54 ms band as the other fsdb
modes. The dominant cost is the full table tokenization and scoring pass;
MySQL serves the same queries from its persistent inverted FULLTEXT index.

ShortRun establishes the order of magnitude and relative shape. Use the full
benchmark job before treating small differences within either engine as a
regression threshold.
