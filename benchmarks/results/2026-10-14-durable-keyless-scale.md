# Durability-matched keyless insert scaling

<!--
sha: 5a00d3d5
date: 2026-10-14T18:41:40Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed fsdb; durable MySQL, binary log disabled
dataset: 1000 users, 1000 orders, 1000 articles
-->

Both servers ran natively on the same host. The fsdb harness started each trial
with a fresh `--data-dir`; MySQL used normal InnoDB commit flushing with binary
logging disabled. The statement inserted `(worker_id, event_id)` into a table
with no keys, and the harness checked that all completed inserts remained in
the table. Three trials ran at each worker count, with one second of warmup and
three seconds of measurement per trial. A fresh database was seeded for each
target and trial.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,4,8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=3 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | fsdb + WAL ops/sec | fsdb RSD | MySQL durable ops/sec | MySQL RSD | fsdb / MySQL |
|---:|---:|---:|---:|---:|---:|
| 1 | 5,346 | 3.1% | 16,021 | 3.0% | 0.33× |
| 4 | 9,101 | 2.0% | 30,608 | 0.7% | 0.30× |
| 8 | 9,006 | 1.6% | 44,823 | 0.9% | 0.20× |

Neither engine reported contention retries. From one to eight workers, fsdb
scaled 1.68× and MySQL 2.80×. FSDB's four- and eight-worker means differ by
about 1%, smaller than this run's per-target variation, so the result supports
a plateau rather than a precise decline. It does not isolate whether WAL flush,
immutable-root publication, row construction, or thread scheduling dominates.
The next optimization step needs a profile of the WAL-backed eight-worker path
and a same-host before/after run; an in-memory measurement cannot attribute
this durable plateau.
