# Durable keyless-write checkpoint

<!--
sha: 125afe52a12b32b39a017f56bd57080dd3e6780b
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This is a repeatable throughput check after the recent query-planning and
full-text changes. It compares native, WAL-backed FSDB with native MySQL using
InnoDB commit-time flush. The disposable MySQL server has binary logging
disabled, so the comparison does not include replication logging. Both sides
run keyless autocommit inserts against fresh 1,000-row users, orders, and
articles seeds. Each result has two seconds of warmup and three ten-second
trials.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | FSDB writes/sec | FSDB RSD | MySQL writes/sec | MySQL RSD | FSDB/MySQL | Retry rate |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 6,846 | 3.0% | 15,639 | 2.4% | 0.44× | 0% both |
| 8 | 11,563 | 5.8% | 41,879 | 10.2% | 0.28× | 0% both |

The [earlier bracketed scheduler check](2026-10-15-group-commit-leader-handoff-check.md)
measured 7,179 FSDB versus 15,782 MySQL writes/sec at one worker (0.45×),
and 12,083–12,294 FSDB versus 45,582–45,990 MySQL writes/sec at eight
workers (0.27×) with the original scheduler. This run has lower absolute
throughput on both sides; the eight-worker MySQL trials also have 10.2% RSD.
The ratio of 0.28× versus 0.27× is too small to establish an improvement.
There is no evidence here of a material change in concurrent-write scaling.
This checkpoint does not justify a scheduling change or a regression claim.
