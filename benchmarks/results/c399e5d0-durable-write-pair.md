# Focused durability-matched insert burst

<!--
sha: c399e5d0
date: 2026-10-14T09:09:20Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed fsdb; durable MySQL
dataset: 10000 users, 50000 orders, 10000 articles
-->

The `SequentialInsertBurst` case performs sixteen sequential autocommit
inserts per invocation on a warm connection. Both targets ran natively on the
same host. Each ShortRun used three warmup and three measured iterations. The
disposable MySQL directory was cleared between the two runs.

```sh
FSDB_BENCH_TARGETS=wal-mysql FSDB_BENCH_CATEGORIES=Durability \
  FSDB_BENCH_METHODS=SequentialInsertBurst just _bench-run --quick
```

| Run | fsdb + WAL | MySQL durable | fsdb / MySQL |
|---|---:|---:|---:|
| 1 | 5.022 ms | 2.203 ms | 2.28× |
| 2 | 5.038 ms | 2.224 ms | 2.27× |

This narrow case shows a stable gap between the two run means, but it is not
a before/after regression comparison or a complete durable-write score. In
the second ShortRun, MySQL's within-run standard deviation was 0.402 ms, so
smaller changes need a longer measurement campaign. The broad
`just bench-write-gate` did not complete on this host: disposable MySQL binary
logs exhausted the available filesystem during worker scaling. A separate
four-target diagnostic also filled the disk during the no-fsync target; its
partial numbers are excluded here.
