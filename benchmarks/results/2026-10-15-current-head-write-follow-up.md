# Durable keyless-write checkpoint after correlated planning work

<!--
sha: 427c74d1
date: 2026-10-15
os: macOS 15.6 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This repeats the durability-matched keyless autocommit insert workload after
the correlated `LIKE`, view, spatial, and CJK changes. FSDB and MySQL ran as
native processes on the same host. The wire FSDB target used a fresh WAL-backed
directory; MySQL used InnoDB commit-time flushing with binary logging disabled.
The embedded control used a separate fresh WAL-backed directory and verified
committed row counts. Ports 3441 and 3442 were disposable; the default FSDB
listener was not touched.

Each path ran two trials with two seconds of warmup and ten measured seconds.
The host was not resource-isolated, so small differences between checkpoints
can reflect background activity. Neither wire target reported retries.

```sh
FSDB_BENCH_PORT=3441 FSDB_BENCH_MYSQL_PORT=3442 \
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load

FSDB_EMBEDDED_TARGET=wal FSDB_EMBEDDED_WORKERS=1,8 \
FSDB_EMBEDDED_TRIALS=2 FSDB_EMBEDDED_WARMUP=2 \
FSDB_EMBEDDED_SECONDS=10 FSDB_BENCH_USERS=1000 \
FSDB_BENCH_ORDERS=1000 FSDB_BENCH_ARTICLES=1000 \
dotnet fsi --nologo benchmarks/scripts/embedded-keyless-load.fsx
```

| Writers | FSDB wire + WAL inserts/sec | MySQL durable inserts/sec | FSDB/MySQL | FSDB embedded + WAL trial inserts/sec |
|---:|---:|---:|---:|---:|
| 1 | 7,236 (3.0% RSD) | 16,660 (0.9% RSD) | 0.43× | 12,091; 11,284 |
| 8 | 12,346 (0.1% RSD) | 45,640 (0.5% RSD) | 0.27× | 33,482; 31,861 |

The eight-writer embedded mean was 32,672 inserts/sec, about 2.65× the wire
FSDB result. The earlier [checkpoint](2026-10-15-current-head-wire-embedded-write-check.md)
measured 0.27× MySQL for eight wire writers and 2.58× embedded-versus-wire
FSDB. The present run is consistent with that gap; its one-writer results vary
too much across checkpoints to claim a trend. These sequential runs measure
end-to-end paths, not a specific lock or thread scheduling mechanism. The
previously measured WAL completion and non-WAL handler growth both remain
plausible contributors. No scheduler prototype was retained from this check.
