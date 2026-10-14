# Durable write dependency check

<!--
sha: 0750bb20 plus a temporary TableLocks experiment
date: 2026-10-14
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed fsdb; durable MySQL, binary log disabled
dataset: 1000 users, 1000 orders, 1000 articles
-->

After the statement-admission change, an eight-writer `insert-keyless` run
still trailed durable MySQL. An eight-second `dotnet-sampled-thread-time` trace
of a WAL-backed run showed wait and thread-pool frames dominating sampled
thread time. Among the sampled `Monitor.Enter_Slowpath` stacks, a prominent
fsdb caller was `TableLocks.expandDependencies`, entering the thread-safe
`Lazy` used to read trigger metadata. Other stacks included insert publication
and group-commit completion waits. These are sampled stacks, not measured lock
hold times or a CPU attribution for completed writes.

I temporarily changed only the trigger-metadata `Lazy` to
`LazyThreadSafetyMode.None`. It is local to one synchronous dependency
expansion, so this removes its initialization monitor without changing the
metadata it returns. `just check` passed all 3,544 tests. The candidate was
then compared with the original on the same host using fresh FSDB directories
and seeded MySQL for each of two trials at each worker count:

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,4,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | Original FSDB + WAL ops/sec | Candidate FSDB + WAL ops/sec | Original MySQL ops/sec | Candidate MySQL ops/sec |
|---:|---:|---:|---:|---:|
| 1 | 6,108 | 5,551 | 15,946 | 15,246 |
| 4 | 10,380 | 9,189 | 31,231 | 26,592 |
| 8 | 11,912 | 11,406 | 45,681 | 43,836 |

The host was slower for both engines on the second run, and the eight-worker
FSDB/MySQL throughput ratio remained about 0.26. This short, non-alternating
comparison gives no evidence that the `Lazy` monitor is the throughput limit.
The candidate was reverted. A useful next profile should isolate active
execution from thread-pool and WAL completion waits before changing lock or
group-commit scheduling.
