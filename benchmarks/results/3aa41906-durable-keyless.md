# Durable keyless-write checkpoint at 3aa41906

<!--
sha: 3aa4190689757bac69c71e37f4b16f731d4d2fe8
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This current-head repeat compares native WAL-backed FSDB with native durable
MySQL on the keyless autocommit insert workload. The disposable MySQL server
uses InnoDB commit-time flush and disables binary logging. Each side uses a
fresh seeded database, two seconds of warmup, and three ten-second trials.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | FSDB writes/sec | FSDB RSD | MySQL writes/sec | MySQL RSD | FSDB/MySQL | Retry rate |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 6,384 | 8.5% | 14,168 | 14.0% | 0.45× | 0% both |
| 8 | 11,848 | 1.5% | 42,027 | 7.7% | 0.28× | 0% both |

The [earlier matched checkpoint](2026-10-15-durable-keyless-write-check.md)
measured ratios of 0.44× and 0.28× at one and eight workers. The absolute
rates moved on both sides, and the one-worker MySQL trials have high
variation. The ratios provide no evidence of a material scaling change from
the recent query-planning and CJK work. Eight-worker FSDB throughput is still
well below MySQL's, so write scheduling remains open; this run does not
justify a queue or WAL policy change by itself.
