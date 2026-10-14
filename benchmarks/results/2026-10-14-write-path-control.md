# Keyless-insert write-path control

<!--
sha: fb8fd5f5
date: 2026-10-14
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed and in-memory fsdb; durable MySQL, binary log disabled
dataset: 1000 users, 1000 orders, 1000 articles
-->

The `insert-keyless` load was repeated on one host with fresh databases for
each trial. Each target used one, four, and eight writers, two trials per
worker count, a one-second warmup, and five measured seconds. The WAL-backed
run preceded the in-memory control. MySQL retained its durable InnoDB flush
setting in both runs; its numbers are context for the host, not a durability-
matched comparison with in-memory fsdb.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,4,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load

# Repeat with FSDB_LOAD_FSDB_TARGET=fsdb for the in-memory control.
```

| Writers | FSDB + WAL ops/sec | FSDB in memory ops/sec | Durable MySQL ops/sec, WAL run | Durable MySQL ops/sec, control run |
|---:|---:|---:|---:|---:|
| 1 | 6,689 | 9,974 | 15,638 | 16,857 |
| 4 | 10,724 | 15,597 | 30,980 | 31,963 |
| 8 | 11,971 | 17,048 | 45,678 | 46,213 |

At eight writers, WAL-backed fsdb reached 70% of its in-memory throughput.
One-to-eight-writer scaling was 1.79× with WAL and 1.71× in memory, compared
with 2.92× for MySQL in the WAL run. Neither engine reported retries. The
short trials and sequential run order limit precision, but the similar fsdb
scaling slopes show that removing WAL work alone would not remove the common
write-path scaling limit. The eight-writer durable ratio, 0.26× MySQL, agrees
with the earlier [dependency check](2026-10-14-write-dependency-profile.md).

A separate traced eight-writer trial ran for 25 measured seconds. During an
eight-second `dotnet-sampled-thread-time` capture, idle thread-pool semaphore
waits occupied 55.7% of inclusive sampled thread time; `QueryHandler.handle`
appeared in 18.3%, `GroupCommit` drain in 2.8%, and `Monitor.Enter_Slowpath`
in 5.2%. This sampling profile includes idle workers, socket waits, and the
trace's own overhead. It is not a CPU breakdown or proof that any named lock
limits throughput. The traced FSDB trial measured 10,975 ops/sec, so its rate
should not be compared directly with the untraced trial as a before/after.

The next diagnosis should separate active SQL/write processing, publication,
and durable completion timing under the same load. A wire-versus-embedded
control could first bound protocol and scheduling overhead. No lock or group-
commit change follows from this trace alone.
