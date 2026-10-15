# Durable keyless-write checkpoint

<!--
sha: 38574aa625ba7f7a0dcee2a7da4e6cebad57a519
date: 2026-10-15
os: Darwin arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This native-host run compares WAL-backed FSDB with MySQL's durable InnoDB
commits after the correlated-query changes. The disposable MySQL server has
commit-time flush enabled and binary logging disabled. Each engine uses a
fresh database for each trial; each result has two seconds of warmup and
three ten-second measured trials.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | FSDB writes/sec | FSDB RSD | MySQL writes/sec | MySQL RSD | FSDB/MySQL | Retry rate |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 6,125 | 7.0% | 14,183 | 1.6% | 0.43× | 0% both |
| 8 | 11,252 | 2.2% | 41,693 | 0.6% | 0.27× | 0% both |

The [earlier same-host checkpoint](2026-10-15-durable-keyless-write-check.md)
measured 0.44× and 0.28× at one and eight writers. Absolute throughput is
lower here on both engines, and FSDB's one-writer RSD is 7.0%. The ratio
change is too small to establish a performance regression or improvement.
FSDB scales 1.84× from one to eight writers in this run, versus MySQL's
2.94×; the concurrent-write gap remains. No scheduler change follows from
this checkpoint. The next useful experiment still needs to separate the
handler's non-WAL work under load and explain the smaller wire WAL batches
observed against the embedded control.
