# Embedded-versus-wire keyless insert control

<!--
sha: 53c34f2e
date: 2026-10-14
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [WAL-versus-memory control](2026-10-14-write-path-control.md) showed a
scaling limit common to both FSDB storage modes. This experiment keeps the
same schema, deterministic seed, keyless insert SQL, WAL-backed data directory,
and writer counts, but runs one FSDB path through the embedded `Db.Connection`
API and the other through the MySQL wire server and MySqlConnector. The
embedded runner verifies the committed row count after each trial.

Each path used two fresh-database trials per worker count, a one-second warmup,
and five measured seconds. The wire run also started a native durable MySQL
server as a host control. These were sequential runs on the same host; the
short duration and run order limit precision.

```sh
FSDB_EMBEDDED_TARGET=wal FSDB_EMBEDDED_WORKERS=1,4,8 \
  FSDB_EMBEDDED_TRIALS=2 FSDB_EMBEDDED_WARMUP=1 FSDB_EMBEDDED_SECONDS=5 \
  FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 FSDB_BENCH_ARTICLES=1000 \
  dotnet fsi --nologo benchmarks/scripts/embedded-keyless-load.fsx

FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=1,4,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Writers | Embedded FSDB + WAL ops/sec | Wire FSDB + WAL ops/sec | Embedded / wire | Durable MySQL ops/sec |
|---:|---:|---:|---:|---:|
| 1 | 12,000 | 6,557 | 1.83× | 15,675 |
| 4 | 24,153 | 10,729 | 2.25× | 31,334 |
| 8 | 31,683 | 11,912 | 2.66× | 45,653 |

The repeated wire result agrees with the earlier 11,971 ops/sec eight-writer
run. Embedded FSDB scales 2.64× from one to eight writers, while the wire path
scales 1.82×. The difference bounds the combined cost of the server, protocol,
client library, and their scheduling behavior. It does not by itself identify
which part dominates, and it is not evidence that embedded execution and the
wire protocol have identical semantics or resource ownership. A focused
wire-path profile and a measured before/after change are needed before moving
locks or changing cancellation behavior.

An isolated scheduling experiment replaced the per-statement detached socket
disconnect watcher with one timer owned by each connection, armed only while a
statement ran. The existing disconnect and `MySqlCommand.Cancel()` integration
tests passed. Repeating the same load gave 6,441, 10,592, and 11,879 FSDB
inserts/sec at one, four, and eight writers; the accompanying durable MySQL
control gave 16,362, 32,080, and 46,149. These numbers are effectively level
with the baseline, so the timer experiment was reverted. Per-statement watcher
startup is not the dominant explanation for the embedded-versus-wire gap under
this workload. The next profile should examine active protocol processing,
response construction, client behavior, and connection scheduling.
