# Eight-writer WAL wire-path method trace

<!--
sha: 704d5514
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401; dotnet-trace 10.0.750501
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [WAL queue-stage profile](2026-10-15-wal-queue-stage-profile.md) left open
whether the eight-writer wire gap is dominated by server execution or WAL
batch scheduling. This follow-up samples managed thread stacks during the
same keyless-insert workload. FSDB used a fresh WAL directory and MySQL kept
InnoDB commit-time flush enabled with binary logging disabled. Each trial had
two seconds of warmup and fifteen measured seconds. The native-host MySQL
result in each trial is a host-load control, not a trace of MySQL methods.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=1 FSDB_LOAD_WARMUP=2 \
  FSDB_LOAD_SECONDS=15 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load

dotnet-trace collect --process-id "$fsdb_pid" \
  --profile dotnet-sampled-thread-time --duration 00:00:10 \
  --output /tmp/fsdb-wire-load.nettrace
dotnet-trace report /tmp/fsdb-wire-load.nettrace topN -n 180 --inclusive
```

The trace attached to the Release server during the measured FSDB phase.
The first and third trials were untraced controls; the middle trial was traced.

| Trial | FSDB inserts/sec | MySQL inserts/sec | FSDB/MySQL |
|---|---:|---:|---:|
| Before trace | 11,984 | 44,651 | 0.27 |
| Traced | 10,933 | 44,464 | 0.25 |
| After trace | 12,198 | 45,272 | 0.27 |

The managed sampled-thread-time report includes idle and blocked threads, so
its percentages are shares of sampled thread-stack time, not wall-clock
latency per insert or CPU utilization. The most relevant inclusive stacks
were `QueryHandler.handle` at 17.27%, `Executor.executeAs` at 12.19%,
`Storage`'s insert preparation at 11.69%, `GroupCommit.loop` at 2.57%, and
`Persistence.appendBatch` at 2.39%. `LowLevelLifoSemaphore.Wait` appeared on
55.84% of sampled stack time, much of it idle thread-pool waiting.
`Monitor.Enter_Slowpath` appeared as an exclusive leaf on 4.77%. Of 5,526
sampled slow-monitor leaf stacks, 2,317 included
`TableLocks.expandDependencies` and `Lazy.CreateValue`, and 1,230 included
the insert preparation path. This identifies where blocked stacks occur; it
does not prove which lock object is contended or that dependency expansion is
the throughput limit. The earlier
[dependency experiment](2026-10-14-write-dependency-profile.md) changed its
trigger-metadata path without improving the matched throughput ratio and was
reverted.

The trace gives a method-resolved view that the macOS `sample` tool could not:
that tool reported JIT frames as unknown binaries. It does not yet establish a
safe scheduling change. The bracketed untraced runs are close to each other,
while tracing reduced FSDB throughput, so use untraced trials for performance
claims. A next candidate experiment should measure dependency expansion and
statement execution wall time directly with a low-overhead, temporary counter
while retaining the same MySQL host-load control; do not infer a global lock
fix from this sample alone.
