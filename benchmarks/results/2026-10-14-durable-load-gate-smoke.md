# Durability-matched load gate smoke run

<!--
sha: c419b2fe plus uncommitted benchmark-gate change
date: 2026-10-14
host: macOS arm64, native same-host processes
mysql: 8.4.11, native Homebrew server, binary log disabled, normal InnoDB commit flush
targets: WAL-backed fsdb; durable MySQL
dataset: 1000 users, 1000 orders, 1000 articles
-->

This bounded run checks that the new WAL-backed concurrent-load path starts,
measures both engines, and removes its disposable fsdb data directories. It is
not a regression threshold: one two-second trial per worker count is too short
to establish stable throughput.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,4 FSDB_LOAD_TRIALS=1 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=2 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | fsdb + WAL ops/sec | MySQL durable ops/sec | fsdb / MySQL |
|---:|---:|---:|---:|
| 1 | 6,080 | 16,920 | 0.36× |
| 4 | 8,579 | 30,580 | 0.28× |

The observed four-worker scaling was 1.41× for fsdb and 1.81× for MySQL.
Those ratios are useful only as a harness check until a longer, repeated run on
a quiet host establishes variance.
