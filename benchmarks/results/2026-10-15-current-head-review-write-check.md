# Current-head durable write check for the independent review

<!--
sha: 9228587e
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The independent review's durable-write concern remains open. This check used
current HEAD, a fresh WAL-backed FSDB database, and a native MySQL server with
InnoDB commit-time flushing enabled and binary logging disabled. Each path ran
two trials with two seconds of warmup and ten measured seconds. The workload
was a keyless autocommit insert through the MySQL wire protocol.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | FSDB inserts/sec | FSDB RSD | MySQL inserts/sec | MySQL RSD | FSDB/MySQL |
|---:|---:|---:|---:|---:|---:|
| 1 | 5,625 | 20.5% | 14,311 | 7.2% | 0.39× |
| 8 | 11,355 | 2.3% | 42,344 | 2.5% | 0.27× |

Neither path reported retries. The eight-worker ratio is close to the earlier
[three-trial checkpoint](2026-10-15-durable-keyless-write-check.md), which
reported 0.28×. The one-worker result is too variable to use as a precise
change estimate. This run confirms the review concern but does not identify a
new bottleneck or justify another group-commit scheduling change. The
[queue-stage profile](2026-10-15-wal-queue-stage-profile.md) found nearly equal
original batch counts at 60,000 wire and embedded writes, so batch size is not
an established explanation for their throughput difference.
