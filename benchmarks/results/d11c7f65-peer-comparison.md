# Durable native database peers at `d11c7f65`

On 2026-11-26, Fsdb `d11c7f65`, MySQL 8.4.11, MariaDB 11.8.10, and
Dolt 2.7.7 ran natively on the same Apple M2 Max with 12 cores, 32 GiB
RAM, macOS 15.6, and about 12 GiB of swap in use. Each server ran alone
on the same IPv4 loopback port with a disposable data directory. The
second pass reversed engine and script order. These short windows on
a shared host measure warm-data performance and provide a directional
comparison. MySQL 8.4 supplies the expected SQL results.

Fsdb ran in Release on .NET 10.0.12 with WAL and `DOTNET_gcServer=1`.
MySQL and MariaDB used a 256 MiB InnoDB buffer pool, commit flushing
set to 1, and binary logging disabled. MySQL used ngram token size 2.
Dolt used its default journal and ordinary SQL commits with
`dolt_transaction_commit=0`. MySqlConnector 2.6.2 used text commands
with TLS and pooling disabled. [Dolt transaction settings](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sysvars/#dolt_transaction_commit).

## Primary-key reads and durable updates

The fixtures use 10,000 rows, READ COMMITTED, and one connection and
one outstanding operation per worker. Each trial has 1.5 seconds of
warmup and six measured seconds. Reads validate the ID, name, and age.
Updates increment an indexed column in autocommit. The harness
reconciles the stored counter with acknowledged updates for the warmup
and measurement windows separately. Cells show operations per second and
p99 milliseconds, averaged across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,176 / 0.26 | 26,613 / 0.08 | 29,132 / 0.05 | 16,162 / 0.12 |
| 1 | Disjoint updates | 7,265 / 0.21 | 14,001 / 0.17 | 20,485 / 0.09 | 157 / 11.10 |
| 1 | Hot-row updates | 7,281 / 0.30 | 13,887 / 0.14 | 18,371 / 0.12 | 158 / 10.79 |
| 16 | Primary-key reads | 29,492 / 9.48 | 116,728 / 0.23 | 119,312 / 0.20 | 76,460 / 0.74 |
| 16 | Disjoint updates | 19,684 / 10.12 | 55,266 / 0.67 | 67,223 / 0.59 | 158 / 115.27 |
| 16 | Hot-row updates | 7,360 / 11.57 | 21,165 / 3.22 | 42,159 / 0.63 | Counter mismatch |

At 16 clients Fsdb reached 25% of MySQL's point-read throughput, 36%
of disjoint-update throughput, and 35% of hot-row throughput. Doubling
Fsdb's point-read rate to 59.0k/s would reach 51% of MySQL, 49% of
MariaDB, and 77% of Dolt. Fsdb's point-read p99 was about 41 times
MySQL's. Fsdb measured 31.2k and 27.8k point reads/s in the two passes.

MariaDB kept pace with MySQL on reads and led both update lanes.
Dolt's stored hot-row counter advanced by 80 and 69 during the timed
windows for 1,174 and 1,023 acknowledged updates. All other engine
and workload combinations reconciled their counters.

Earlier controls on the same host found that switching the Fsdb CLI
from its default GC to Server GC improved 16-client point throughput
roughly fourfold with higher peak RSS. Both Fsdb passes here already use
Server GC. The previously measured difference between memory and WAL
point reads was about 10% under the default GC. Current allocation and
CPU profiles would help locate the remaining read cost.
[CLI GC comparison](2026-11-25-cli-gc-load.md).
[Point-read diagnosis](2026-11-18-point-read-diagnosis.md).

## SQL feature latency

Correlated queries use 500 parents and 10,000 children. The nested
derived tables and CTE rename and reorder projections of the same
composite index. The joined-view workloads check affected rows and
final values, including once-per-target updates when two rows match.
The spatial window and CJK phrase fixtures each have 10,000 entries.
The feature harness checks every measured result after preflight and
warmup. Cells show mean per-pass p50 latency in milliseconds. A rejection
or wrong result has no timing.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.038 | 1.426 | 1.056 | 2.813 |
| Correlated range count | 3.992 | 1.691 | 1.163 | 7.082 |
| Physical `COUNT(*)` control | 3.521 | 1.646 | 1.169 | 7.068 |
| Nested derived `COUNT(*)` | 3.661 | 1.688 | 1.228 | 11.823 |
| Nested derived `COUNT(payload)` | 4.363 | 2.394 | 1.496 | 11.730 |
| Nested derived `SUM(sequence_no)` | 5.884 | 1.720 | 1.259 | 11.613 |
| CTE `COUNT(*)` | 4.186 | 1.676 | 1.181 | 11.443 |
| Derived `COUNT(*)` with a source null test | 4.750 | 2.416 | 1.488 | 11.420 |
| Joined-view update | 1.026 | 0.128 | 0.132 | Rejected |
| Joined-view update, 501 partner rows | 1.138 | 0.125 | 0.145 | Rejected |
| Joined-view update, 5,001 partner rows | 1.143 | 0.134 | 0.133 | Rejected |
| Indexed planar `MBRContains` | 0.231 | 0.115 | Boundary mismatch | Rejected |
| Geographic antimeridian distance | 0.133 | 0.072 | Rejected | Rejected |
| Japanese ngram phrase | 0.668 | 0.450 | Index DDL rejected | Index DDL rejected |
| Chinese ngram phrase | 0.727 | 0.457 | Index DDL rejected | Index DDL rejected |
| Korean ngram phrase | 0.528 | 0.441 | Index DDL rejected | Index DDL rejected |

Fsdb matched MySQL's checked feature results in both passes. The nested
`COUNT(payload)` and `SUM(sequence_no)` took 1.8 and 3.4 times MySQL's
median and ran faster than Dolt on the same inputs. Fsdb's `SUM` ran in
4.8 and 7.0 ms across the passes. The earlier controlled comparison of
derived binding reuse measured a 3.9× speedup for the count and 3.4× for
the sum. [Derived binding reuse comparison](2026-11-25-correlated-integer-range-count.md#reuse-of-derived-aggregate-bindings).

The joined-view updates took 8.0–9.1 times MySQL's median. The Fsdb
prefix cases stayed near 1.14 ms as the partner table grew from 501 to
5,001 rows. Both spatial probes took about twice MySQL's median. Fsdb,
MySQL, and MariaDB used `ix_g` for the planar window. MariaDB included
eight boundary points alongside the single interior point. Fsdb's
CJK phrase queries took 1.2–1.6 times MySQL's median. The CJK fixture
exercises phrase search, and the geographic fixture exercises distance.

## Earlier TiDB and Percona results

TiDB 8.5.8 previously ran as a single-host, single-replica TiDB, TiKV,
and PD deployment. At 16 clients it measured 23.5k point reads/s,
10.6k disjoint updates/s, and 2.5k hot-row updates/s. The point-read p99
was 1.10 ms. Those results describe the local distributed topology.
[TiDB measurements and topology](eeced6b1-peer-refresh.md#other-topologies).

Percona Server 8.4.11-12 and MySQL measured 44.8k and 44.7k point
reads/s at 16 clients in the matched ARM64 container run. Fsdb measured
11.9k in the same container setup. The Docker daemon did not answer
the health probe for the current run.
[Matched Percona, MySQL, and Fsdb results](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

## Reproduction

Start one disposable server at a time with the settings above. Run
Fsdb, MySQL, MariaDB, and Dolt in that order for pass 1. For pass 2,
reverse the engine and command orders and set `pass=2`.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" projected-range-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Per-pass measurements are in the [reads](d11c7f65-peer-comparison-reads.csv),
[writes](d11c7f65-peer-comparison-writes.csv), and
[features](d11c7f65-peer-comparison-features.csv) CSVs.
