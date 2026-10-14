# Durable write completion check

<!--
sha: d9d37330 plus a temporary GroupCommit experiment
date: 2026-10-14
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed fsdb; durable MySQL, binary log disabled
dataset: 1000 users, 1000 orders, 1000 articles
-->

An eight-writer durable insert run still trails MySQL after the statement
admission change. A short native Time Profiler capture showed running samples
in thread yields, WAL/file operations, and GC; it did not symbolize managed
fsdb frames. A separate eight-second `dotnet-sampled-thread-time` capture
included `Task.SpinWait` under the group-commit acknowledgement path, but that
profile counts wall-time thread samples, not active CPU time. Neither trace
proves that completion scheduling limits throughput.

To test one bounded hypothesis, the group-commit queue's
`TaskCompletionSource` was temporarily constructed without
`RunContinuationsAsynchronously`. The public queue contract and WAL batching
were otherwise unchanged. Both versions used this same-host command with
fresh FSDB directories and seeded MySQL for each trial:

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | Original FSDB + WAL ops/sec | Candidate FSDB + WAL ops/sec | Original MySQL ops/sec | Candidate MySQL ops/sec |
|---:|---:|---:|---:|---:|
| 1 | 6,277 | 6,938 | 15,823 | 15,611 |
| 8 | 12,353 | 12,232 | 42,798 | 45,714 |

There were no retries. The candidate's single-writer increase is within the
noise of two short, non-alternating runs; its eight-writer result did not
improve. The candidate was reverted. Retain the existing completion option and
measure active managed execution, WAL flush frequency, and commit batch size
before changing group-commit scheduling.
