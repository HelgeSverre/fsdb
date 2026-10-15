# Durable keyless-write checkpoint after preview.11

<!--
sha: 89f3cf9a
date: 2026-10-15
os: macOS 15.6 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This repeats the durability-matched keyless autocommit `INSERT` workload after
the exact-key-lock, correlated-planner, ngram outer-join, and layered-view
oracle merges. FSDB used a fresh WAL-backed data directory. MySQL used native
InnoDB with commit-time flushing and binary logging disabled. Both ran on the
same host with disposable ports 3441 and 3442. Each path had two trials, two
seconds of warmup, and ten measured seconds; neither reported retries.

```sh
FSDB_BENCH_PORT=3441 FSDB_BENCH_MYSQL_PORT=3442 \
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Writers | FSDB WAL inserts/sec | Durable MySQL inserts/sec | FSDB/MySQL |
|---:|---:|---:|---:|
| 1 | 6,804 (2.0% RSD) | 15,973 (0.4% RSD) | 0.43× |
| 8 | 12,568 (1.5% RSD) | 46,952 (0.9% RSD) | 0.27× |

The [earlier current-head checkpoint](2026-10-15-current-head-write-follow-up.md)
reported 0.43× and 0.27× at one and eight writers. This run confirms that the
recent correctness and planning changes did not close the durable wire-write
scaling gap. The host was not resource-isolated; these rates do not identify a
particular lock or scheduler cause. Prior stage timings put substantial wait
inside WAL completion and additional time inside execution. A further change
to queue ownership or WAL policy still needs an isolated causal profile and
repeated throughput evidence.
