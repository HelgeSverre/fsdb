# Unindexed UPDATE worker scaling

<!--
sha: 92b019ff
date: 2026-10-14T09:01:33Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: in-memory fsdb; durable MySQL
dataset: 1000 users, 1000 orders, 1000 articles
-->

The opt-in `update-scan-distinct` workload repeatedly increments `scan_age`
for one row selected by unindexed `name`. Workers choose distinct ID slices.
The harness checks that the total `scan_age` increase equals completed writes
on each engine; the check runs outside the timed interval.

Command:

```sh
FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=3 \
FSDB_LOAD_WARMUP=1 FSDB_LOAD_SECONDS=3 \
FSDB_LOAD_WORKLOADS=update-scan-distinct just _bench-run --load
```

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | update-scan-distinct | 1247 | 9.3% | 0.0% | 1761 | 0.2% | 0.0% | 0.71x |
| 8 | update-scan-distinct | 2527 | 11.6% | 0.0% | 1917 | 1.5% | 0.0% | 1.32x |

On this fixture, fsdb's measured 1-to-8-worker slope is 2.03×; MySQL's is
1.09×. The fsdb 8-worker relative standard deviation is 11.6%, so treat
small differences as noise. Absolute throughput is not durability-matched:
fsdb ran in memory and MySQL committed to disk. This is a scheduling and
scaling probe, not evidence of durable-write parity or a before/after speedup.

The broader `just bench-write-gate` was attempted first. Its durable latency
phase executed, but the generated Markdown contained only a provenance header,
so no latency result is retained. The worker-scaling phase then filled the
available filesystem with disposable MySQL binary logs; MySQL aborted and the
run produced no valid scaling result. The disposable server data was removed.
The smaller campaign above completed with the row-change proof on both engines.
