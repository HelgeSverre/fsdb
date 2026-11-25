# Fsdb against MySQL-compatible peers

This 2026-11-25 run uses Fsdb `f6fd1471`, the source revision used for
the Release binary. MySQL 8.4.11 supplies the expected SQL results.
MariaDB 11.8.10 and Dolt 2.7.7 ran in the same native comparison. The
host was an Apple M2 Max with 12 cores, 32 GiB RAM, macOS 15.6, and
about 12.4 GiB of swap in use. Only one benchmarked server ran at a
time. Pass 2 reversed engine and load-lane order. Shared host activity
and short warm-data samples limit how precisely the numbers generalize.

Fsdb used .NET 10.0.12, Server GC, and WAL. MySQL and MariaDB used a
256 MiB InnoDB buffer pool, commit flushing set to 1, and binary logging
disabled. Dolt used its default journal with Git-style commits disabled.
The client used MySqlConnector 2.6.2 over IPv4 loopback with TLS and
connection pooling disabled.

## Native throughput and tail latency

The read and write fixtures use 10,000 rows, one outstanding request per
connection, and READ COMMITTED. Each measurement lasts six seconds
after 1.5 seconds of warmup. Random primary-key reads validate the
returned ID, name, and age. Each write increments an indexed column
in autocommit. Both warmup and timed intervals must reconcile successful
acknowledgements with the stored counter before the fixture reports a rate.

Cells show operations per second followed by p99 latency in milliseconds.
Each is the mean of the two per-pass statistics.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,168 / 0.24 | 26,870 / 0.06 | 28,905 / 0.05 | 16,148 / 0.13 |
| 1 | Disjoint updates | 6,882 / 0.22 | 13,935 / 0.21 | 20,233 / 0.09 | 169 / 10.37 |
| 1 | Hot-row updates | 7,238 / 0.20 | 14,038 / 0.18 | 19,983 / 0.09 | 173 / 10.14 |
| 16 | Primary-key reads | 30,289 / 8.84 | 116,087 / 0.23 | 117,432 / 0.22 | 77,454 / 0.68 |
| 16 | Disjoint updates | 18,964 / 10.44 | 54,585 / 0.54 | 64,585 / 0.63 | 165 / 113.33 |
| 16 | Hot-row updates | 7,425 / 11.45 | 23,623 / 3.21 | 41,592 / 0.64 | Counter mismatch |

With 16 clients, Fsdb delivered 26% of MySQL's point-read throughput,
35% of its disjoint update throughput, and 31% of its hot-row update
throughput. Fsdb's point-read rate ranged from 29.1k to 31.5k ops/s, and
MySQL's ranged from 113.6k to 118.5k. Doubling Fsdb's mean to 60.6k
would reach 52% of MySQL, 52% of MariaDB, and 78% of Dolt at their
measured rates. Fsdb's point-read p99 was about 39× MySQL's.

Dolt acknowledged 1,239 and 1,245 updates in the 16-client hot-row
timed windows. Its stored counter increased by 85 in each pass.
The disjoint and single-client lanes reconciled their counters.
Dolt's ordinary SQL commits update the working set using its versioned
storage. [Dolt transaction documentation](https://www.dolthub.com/docs/sql-reference/sql-support/supported-statements/).

## Native SQL feature latency

The correlated counts use 500 parents and 10,000 children. The joined
views use a nested filter with a cascaded check option and joins on a
complete key or a composite key prefix. The prefix fixture verifies
that two matches update the target once. The spatial fixtures check
an indexed planar window over 10,000 points and geographic distance
across the antimeridian. The CJK fixture uses 10,000 documents, an
`ngram` index, and `utf8mb4_bin`. The table averages the medians from
two passes of 60 to 150 verified measurements, after one preflight and
four warmups per lane.

| Workload, p50 ms | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.209 | 1.421 | 1.055 | 2.872 |
| Correlated equality and range count | 5.204 | 1.685 | 1.168 | 7.344 |
| Nested joined-view update | 1.057 | 0.133 | 0.125 | Rejected update |
| Joined-view prefix, 501 partner rows | 1.113 | 0.145 | 0.127 | Rejected update |
| Joined-view prefix, 5,001 partner rows | 1.113 | 0.129 | 0.128 | Rejected update |
| Indexed planar `MBRContains` | 0.254 | 0.118 | Boundary mismatch | Unsupported function |
| Geographic antimeridian distance | 0.143 | 0.075 | Rejected constructor | Rejected SRID |
| Japanese phrase | 0.725 | 0.446 | Rejected index DDL | Rejected index DDL |
| Chinese phrase | 0.765 | 0.456 | Rejected index DDL | Rejected index DDL |
| Korean phrase | 0.674 | 0.428 | Rejected index DDL | Rejected index DDL |

Fsdb passed every checked MySQL feature result in both passes. The
correlated counts took 1.6× and 3.1× MySQL's median time. Joined-view
updates took 7.7–8.6×, spatial queries took 1.9–2.2×, and the CJK
phrases took 1.6–1.7×. MySQL, Fsdb, and MariaDB all reported range
access for the planar spatial query. MariaDB included eight boundary
points, and Fsdb and MySQL returned one interior point.

The joined-view timings use one autocommit client. In the separate
concurrency probe, MySQL waited for pending changes to an outer join
row, and Fsdb proceeded before they resolved. The behavior differed
in all 24 matching-key cases across four isolation levels, with
agreement in all eight unrelated-key controls.
[Joined-view lock results](2026-11-25-bound-joined-view-update.md#outer-join-operand-locks).

## TiDB on one host

TiDB 8.5.8 ran in a separate pair of passes through TiUP Playground
1.17.1. The local topology used one TiDB, one TiKV, and one PD process,
with one replica, a 256 MB TiKV block cache, and monitoring and TiFlash
disabled. Pessimistic autocommit was enabled for the READ COMMITTED load
fixture. One-phase commit and async commit retained their enabled defaults.
This topology measures a local TiDB stack. A production cluster adds
replication and network round trips. [TiUP Playground documentation](https://docs.pingcap.com/tidb/stable/tiup-playground/).
[TiDB pessimistic transaction configuration](https://docs.pingcap.com/tidb/stable/tidb-configuration-file/#pessimistic-txn).

| Workload | 1 client | 16 clients |
| --- | ---: | ---: |
| Primary-key reads | 4,636 / 0.32 | 24,306 / 1.09 |
| Disjoint updates | 2,439 / 0.62 | 10,806 / 2.46 |
| Hot-row updates | 2,528 / 0.59 | 2,503 / 10.63 |

The cells show ops/s and p99 ms, as in the native table. TiDB reconciled
all update counters. Fsdb was about 25% faster for 16-client reads,
while TiDB's p99 was about eight times lower. TiDB's correlated equality
and range medians were 3.362 and 60.689 ms. The tested joined-view
updates and spatial operations were unavailable. The full-text DDL ran,
and each of the three `MATCH ... AGAINST` queries was rejected.
[TiDB MySQL compatibility](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Percona in matched ARM64 containers

Fsdb, MySQL 8.4.11, and Percona Server 8.4.11-12 each ran in an
ARM64 Linux container with four CPUs, 2 GiB of memory, no container
swap, a fresh named data volume, and a localhost-published port.
The macOS client and checked fixtures were the same as in the native
runs. The initial attempt ended with a MySQL point-read command
timeout. The matched retry started all engines fresh and ran only
point reads and SQL feature probes. The native writes above remain
the current durable write comparison. Earlier container write rates
varied by more than fivefold between passes for some InnoDB lanes.
[Earlier container measurements](2026-11-25-peer-rerun.md#arm64-linux-containers).
[Percona Server documentation](https://docs.percona.com/percona-server/8.4/).

| Primary-key reads, ops/s and p99 ms | Fsdb | MySQL | Percona |
| --- | ---: | ---: | ---: |
| 1 client | 2,945 / 2.71 | 10,397 / 0.18 | 11,270 / 0.16 |
| 16 clients | 11,940 / 5.46 | 44,719 / 0.69 | 44,769 / 0.71 |

With 16 clients, MySQL and Percona were within 0.3% of each other
in each pass. Fsdb reached 27% of MySQL's throughput and had about
8× its p99 latency. All three passed the ten feature checks in both
passes. Joined-view update timing varied across the two passes.

| Workload, p50 ms | Fsdb | MySQL | Percona |
| --- | ---: | ---: | ---: |
| Correlated equality count | 3.696 | 1.165 | 1.224 |
| Correlated equality and range count | 7.051 | 1.495 | 1.527 |
| Nested joined-view update | 2.970 | 2.359 | 3.801 |
| Joined-view prefix, 501 partner rows | 2.711 | 1.766 | 1.691 |
| Joined-view prefix, 5,001 partner rows | 2.944 | 2.871 | 1.449 |
| Indexed planar `MBRContains` | 0.402 | 0.140 | 0.144 |
| Geographic antimeridian distance | 0.281 | 0.098 | 0.093 |
| Japanese phrase | 1.126 | 0.513 | 0.496 |
| Chinese phrase | 1.238 | 0.497 | 0.518 |
| Korean phrase | 1.110 | 0.471 | 0.472 |

The MySQL image uses the [oracle pin](../../torture/compose.yaml).
The other ARM64 images were Percona Server
`sha256:e759516b1b2f446ec83c06e5cd47ac9a4d13b3a19a8d155da3c555feed621ac0`
and the .NET runtime
`sha256:ec7cd715c9ab3831d134c7041d0d65891b97a185717a49147a22f75090b44338`.
Both InnoDB servers used the native benchmark's flush and buffer
settings. Fsdb used the same Release build with WAL and Server GC.

## Reproduction

Use a disposable server and a free loopback port. The scripts connect as
root on the selected port and create and drop their own fixtures. Set
`engine` to `Fsdb`, `MySQL`, `MariaDB`, `Dolt`, `TiDB`, or
`Percona` and `pass` to 1 or 2. Run the write workload first and point
reads last on pass 2. Each fixture reverses its internal workloads as
applicable.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Per-pass data includes status, result counts for writes, and tail latency
for successful samples.

| Setup | Reads | Writes | SQL features |
| --- | --- | --- | --- |
| Native servers | [CSV](2026-11-25-peer-comparison-native-reads.csv) | [CSV](2026-11-25-peer-comparison-native-writes.csv) | [CSV](2026-11-25-peer-comparison-native-features.csv) |
| Local TiDB stack | [CSV](2026-11-25-peer-comparison-tidb-reads.csv) | [CSV](2026-11-25-peer-comparison-tidb-writes.csv) | [CSV](2026-11-25-peer-comparison-tidb-features.csv) |
| ARM64 Linux containers | [CSV](2026-11-25-peer-comparison-linux-reads.csv) | — | [CSV](2026-11-25-peer-comparison-linux-features.csv) |
