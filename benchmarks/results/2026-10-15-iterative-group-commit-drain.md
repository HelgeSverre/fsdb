# Iterative group-commit leader drain

<!--
base-sha: 019e8b8c
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The group-commit leader previously called `drain` recursively after each
batch. If producers kept the queue nonempty, its stack grew with the number
of batches. A new regression has the flush callback replenish the queue for
256 successive single-write batches. It failed on the recursive implementation
because the leader's stack depth grew, then passed with an iterative drain.
The other five queue tests cover batch ordering, checkpoint barriers, failure
propagation, backpressure, and concurrent producers; all six pass. `just check`
passed 3,618 tests with no build warnings.

The change leaves the same thread as leader until the queue empties. It does
not change batch policy, durability, or completion handoff. A three-trial
durability-matched wire check used fresh WAL-backed FSDB and native MySQL
databases, each with two seconds of warmup and ten measured seconds. The
default FSDB benchmark port was owned by a separate session, so this run used
free ports 3441 and 3442 without stopping that process.

```sh
FSDB_BENCH_PORT=3441 FSDB_BENCH_MYSQL_PORT=3442 \
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Workers | FSDB inserts/sec | FSDB RSD | MySQL inserts/sec | MySQL RSD | FSDB/MySQL |
|---:|---:|---:|---:|---:|---:|
| 8 | 11,548 | 1.8% | 41,406 | 1.1% | 0.28× |

No retries were reported. The preceding [current-head
check](2026-10-15-current-head-review-write-check.md) measured 0.27× at eight
workers, but it used two trials and the default ports. These runs do not
establish a throughput improvement; they show no obvious regression and leave
the concurrent-write performance gap open.
