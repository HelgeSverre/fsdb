# Current-head durable wire and embedded write check

<!--
sha: 7834ba4c
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This checkpoint repeats the keyless autocommit insert workload after the
recent planner, view, and CJK changes. FSDB and MySQL ran as native processes
on the same host. The wire comparison used a fresh WAL-backed FSDB directory
and durable MySQL with InnoDB commit-time flushing and binary logging disabled.
The embedded control used a fresh WAL-backed FSDB directory, the same schema
and seed, and verified committed row counts after each trial.

Each path ran two trials with two seconds of warmup and ten measured seconds.
The default FSDB port was occupied by an unrelated server, so the wire run
used ports 3441 and 3442 without touching that listener.

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
| 1 | 6,344 (11.5% RSD) | 15,295 (3.8% RSD) | 0.41× | 10,010; 6,456 |
| 8 | 11,401 (1.8% RSD) | 41,615 (0.1% RSD) | 0.27× | 31,109; 27,699 |

Neither wire target reported retries. The embedded eight-writer mean is
29,404 inserts/sec, about 2.58× the wire FSDB result. The one-writer embedded
trials vary too much for a precise ratio. The eight-writer FSDB/MySQL ratio
matches the earlier [0.27× current-head checkpoint](2026-10-15-current-head-review-write-check.md)
and the [embedded/wire control](2026-10-14-embedded-wire-write-control.md)
still shows a large path difference. These are separate sequential runs, so
they bound combined server, protocol, client, and scheduling cost rather than
isolating one function or lock. No scheduler change is justified by this
checkpoint alone; the next experiment should isolate a measured wire stage
with a correctness-preserving before/after comparison.

A diagnostic repeat used one 25-second eight-writer wire trial and attached
`dotnet-trace` to that run's FSDB process for 12 seconds with the
`dotnet-sampled-thread-time` profile. It measured 10,152 FSDB and 35,696
MySQL inserts/sec (0.28×). The trace's exclusive top methods were dominated
by idle thread-pool semaphore waits (37.9% and 19.1%) and `WaitHandle` (9.1%);
`Monitor.Enter_Slowpath` was 4.1% and `Persistence.flushToDisk` 1.1%.
`QueryHandler.handle` appeared on 16.9% of sampled stacks inclusively. These
percentages cover all threads, including idle workers, and cannot apportion
the eight-writer throughput loss to a particular lock, protocol stage, or
WAL operation. The temporary trace was not used as a before/after performance
claim.
