<!--
sha: d8e985ef
date: 2026-11-04T18:13:21Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: WAL-backed fsdb; durable MySQL
dataset: 1000 users, 1000 orders, 1000 articles
-->

Targets: fsdb-wal and durable MySQL. Workers: 1, 8. 3 trial(s), 10.0s measured after 2.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | insert-keyless | 8193 | 1.8% | 0.0% | 16279 | 0.1% | 0.0% | 0.50x |
| 8 | insert-keyless | 12629 | 1.0% | 0.0% | 45441 | 0.7% | 0.0% | 0.28x |

The benchmark used a fresh database for each target and trial, two seconds
of warmup, and ten measured seconds. MySQL used InnoDB commit-time flush with
binary logging disabled. The earlier [October 15 checkpoint](3aa41906-durable-keyless.md)
measured 0.45× and 0.28× at one and eight writers; this run measures 0.50×
and 0.28×. Both engines' absolute rates moved, while the eight-writer ratio
remained unchanged. The worker-scaling gap remains open. This checkpoint
does not isolate a new bottleneck or justify a queue-policy change.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=1,8 FSDB_LOAD_TRIALS=3 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=10 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just bench-load
```
