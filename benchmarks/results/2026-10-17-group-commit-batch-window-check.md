# Fixed group-commit batch-window check

<!--
date: 2026-10-17
baseline: 07e4c00e (perf/reuse-wal-stream, PR #146)
candidate: local prototype based on 07e4c00e, reverted after measurement
os: Darwin arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 10000 users, 50000 orders, 10000 articles (load harness defaults)
-->

The WAL-stream reuse change removes an open/close cycle from each commit batch,
but durable keyless inserts still scale less well than MySQL at eight writers.
This experiment tested whether waiting approximately 50 microseconds before a
group-commit leader drains the queue would improve batching enough to offset
the delay. The candidate inserted a fixed `Stopwatch`/`SpinWait` window before
`drain` in `GroupCommit.fs`; no other behavior changed. The focused concurrent
producer test passed. The prototype was not retained.

Each run used a fresh seeded database per target and trial, native processes on
the same host, WAL-backed fsdb against durable MySQL with binary logging off,
two trials, two seconds of warmup, and seven measured seconds. The A/B/A order
brackets the candidate with the same baseline. No retries were reported.

```sh
FSDB_LOAD_WORKLOADS=insert-keyless FSDB_LOAD_FSDB_TARGET=fsdb-wal \
  FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=2 \
  FSDB_LOAD_SECONDS=7 just _bench-run --load
```

| Run | Writers | fsdb + WAL inserts/s | Durable MySQL inserts/s | fsdb / MySQL |
|---|---:|---:|---:|---:|
| A1 baseline | 1 | 7,342 (1.4% RSD) | 17,015 (0.9% RSD) | 0.43× |
| A1 baseline | 8 | 10,715 (0.3% RSD) | 46,586 (0.3% RSD) | 0.23× |
| B 50 µs window | 1 | 5,301 (2.2% RSD) | 17,051 (0.1% RSD) | 0.31× |
| B 50 µs window | 8 | 8,381 (0.4% RSD) | 35,536 (26.4% RSD) | 0.24× |
| A2 baseline | 1 | 7,142 (1.3% RSD) | 16,903 (0.2% RSD) | 0.42× |
| A2 baseline | 8 | 8,236 (2.0% RSD) | 43,463 (6.6% RSD) | 0.19× |

The fixed window reduced one-writer fsdb throughput by about 26–28% against
the two baseline runs, while the MySQL one-writer control stayed near 17,000
inserts/s. Eight-writer results drifted substantially even between the two
baseline runs; the MySQL control was also unstable during the candidate run.
Those measurements do not establish an eight-writer benefit or loss from the
window. Batch sizes were not instrumented, so the experiment also does not
prove that the wait formed larger batches.

A fixed leader delay is therefore unsupported by this evidence. The next
scheduler investigation should first obtain stable eight-writer controls and
measure batch size and queue residence alongside the post-parse execution and
acknowledgement stages. The [current-head checkpoint](2026-10-17-keyless-write-scaling-checkpoint.md)
and [handler-stage profile](2026-10-17-keyless-write-handler-stage-profile.md)
provide the preceding baseline.
