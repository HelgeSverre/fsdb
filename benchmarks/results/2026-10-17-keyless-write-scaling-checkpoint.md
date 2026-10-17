# Current-head keyless-write scaling checkpoint

<!--
sha: 425bc48a (spatial-index branch; insert and WAL paths match origin/main 4537d4ef)
date: 2026-10-17
os: Darwin arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

This repeat checks whether the durable wire-write scaling gap seen in the
[October 15 checkpoint](3aa41906-durable-keyless.md) persists on the current
code. Each target used fresh seeded databases, two trials, one second of
warmup, and five measured seconds. The `insert-keyless` workload used one or
eight clients. MySQL was native, durable InnoDB with binary logging disabled.
The in-memory fsdb run is an engine control; its ratio to durable MySQL is not
a durability-matched comparison.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load

# Repeat with FSDB_LOAD_FSDB_TARGET=fsdb for the in-memory control.
```

| Writers | fsdb + WAL inserts/s | fsdb memory inserts/s | Durable MySQL inserts/s, WAL run | fsdb WAL / MySQL |
|---:|---:|---:|---:|---:|
| 1 | 6,609 (0.3% RSD) | 10,172 (0.8% RSD) | 16,927 (1.4% RSD) | 0.39× |
| 8 | 11,946 (2.2% RSD) | 17,189 (0.2% RSD) | 47,612 (0.4% RSD) | 0.25× |

The in-memory run's separate durable-MySQL control measured 16,601 and
47,890 inserts/s at one and eight writers. Neither engine reported retries.
fsdb scaled 1.81× with WAL and 1.69× in memory, while durable MySQL scaled
2.81× in the WAL run. At eight writers WAL-backed fsdb reached 69% of its
in-memory rate. The absolute fsdb rates remain close to the earlier control;
the lower ratio here mainly reflects a faster MySQL host control. These short
trials confirm that WAL work is material but cannot alone explain the weak
worker-scaling slope shared by both fsdb modes. They do not identify a safe
queue or publication-policy change.

A [same-run handler-stage profile](2026-10-17-keyless-write-handler-stage-profile.md)
separates parse, post-parse execution, and durable acknowledgement time. It
finds material growth outside acknowledgement at eight writers as well as in
the acknowledgement itself.
