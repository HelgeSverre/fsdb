# Durable native database peers at `ac7d4a67`

Fsdb `ac7d4a67`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7 ran
one at a time on a shared 12-core Apple M2 Max with 32 GiB RAM and
macOS 15.6 on 2026-11-26. The host had about 12 GiB of swap in use.
Each server used a fresh data directory and IPv4 loopback. Pass 2
reversed engine and suite order. MySQL 8.4.11 is the compatibility
reference.

Fsdb ran in Release on .NET 10.0.12 with WAL and `DOTNET_gcServer=1`.
MySQL and MariaDB used a 256 MiB InnoDB buffer pool, commit-time flush,
and disabled binary logging. MySQL used ngram token size 2. Dolt used
its default journal and ordinary SQL commits with
`dolt_transaction_commit=0`. MySqlConnector 2.6.2 sent text commands
with TLS and pooling disabled.
[Dolt transaction settings](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sysvars/#dolt_transaction_commit).

## Point reads and durable updates

Each database uses 10,000 rows, READ COMMITTED, and one outstanding
request per client. Each trial runs 1.5 seconds of warmup and six
seconds of measurement. The client checks the ID, name, and age of
each point read. Every update increments an indexed column in
autocommit. The harness reconciles acknowledged updates with the
table counter after each window. Cells average the ops/s and p99 in
milliseconds across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 6,880 / 0.26 | 27,290 / 0.05 | 29,152 / 0.04 | 16,154 / 0.12 |
| 1 | Disjoint updates | 6,878 / 0.22 | 14,335 / 0.14 | 19,492 / 0.09 | 158 / 10.76 |
| 1 | Hot-row updates | 7,312 / 0.21 | 13,873 / 0.15 | 17,956 / 0.08 | 153 / 11.30 |
| 16 | Primary-key reads | 30,217 / 8.98 | 117,641 / 0.21 | 119,170 / 0.20 | 79,122 / 0.65 |
| 16 | Disjoint updates | 20,596 / 9.44 | 55,920 / 0.51 | 66,725 / 0.60 | 159 / 118.44 |
| 16 | Hot-row updates | 7,520 / 11.34 | 19,288 / 3.65 | 42,448 / 0.61 | Counter mismatch |

With 16 clients Fsdb reached 26% of MySQL's point-read throughput,
37% of its disjoint-update throughput, and 39% of its hot-row
throughput. Doubling Fsdb's measured read rate to 60.4k/s would reach
51% of MySQL and MariaDB and 76% of Dolt. The two Fsdb read passes
delivered 28.7k and 31.8k reads/s. Its read p99 averaged 8.98 ms
against 0.21 ms on MySQL.

Dolt acknowledged 1,156 and 1,164 hot-row updates in the two timed
windows. The committed counter advanced by 79 in each. The other
timed write workloads reconciled.

### Fsdb garbage collection control

The same Release binary ran the point-read fixture again with WAL in
both modes. Fresh Fsdb servers alternated between CLI defaults and
`DOTNET_gcServer=1`, with the order reversed in pass 2.

| Clients | CLI defaults, ops/s and p99 ms | Server GC, ops/s and p99 ms |
| ---: | ---: | ---: |
| 1 | 5,243 / 3.08 | 7,590 / 0.24 |
| 16 | 7,637 / 5.74 | 28,461 / 8.05 |

Server GC raised 16-client throughput 3.7× in this control. The p99
rose from 5.74 to 8.05 ms while the clients completed more reads.
The [earlier WAL attachment control](ddbd4602-peer-comparison.md#wal-attachment-control)
found similar read throughput with WAL attached and in memory when
both used Server GC.

## SQL features

The correlated fixtures use 500 parents and 10,000 children. Joined
view fixtures check affected rows and committed values. The planar
fixture uses a 10,000-point spatial index. Each geographic edge
query checks four predicates on WGS 84 inputs. The binary-collated
CJK fixture checks the returned ID sum for each query over 10,000
articles. The numbers average two per-pass p50s, in milliseconds.
Rejections and result mismatches have no timing.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 1.992 | 1.430 | 1.063 | 2.770 |
| Correlated range count | 4.008 | 1.708 | 1.164 | 7.019 |
| Joined-view update | 1.013 | 0.118 | 0.136 | Rejected |
| Indexed planar `MBRContains` | 0.226 | 0.119 | Mismatch | Rejected |
| Geographic antimeridian distance | 0.131 | 0.065 | Rejected | Rejected |
| Japanese ngram phrase | 0.643 | 0.448 | Parser DDL rejected | Parser DDL rejected |
| Chinese ngram phrase | 0.692 | 0.463 | Parser DDL rejected | Parser DDL rejected |
| Korean ngram phrase | 0.612 | 0.432 | Parser DDL rejected | Parser DDL rejected |
| Physical `COUNT(*)` control | 2.975 | 1.647 | 1.177 | 7.099 |
| Nested derived `COUNT(*)` | 3.674 | 1.695 | 1.238 | 11.892 |
| Nested derived `COUNT(payload)` | 4.443 | 2.398 | 1.488 | 11.673 |
| Nested derived `SUM(sequence_no)` | 5.724 | 1.735 | 1.259 | 11.705 |
| CTE `COUNT(*)` | 4.162 | 1.685 | 1.187 | 11.503 |
| Derived `COUNT(*)` with a source null test | 4.476 | 2.442 | 1.466 | 11.430 |
| Joined-view update, 501 partner rows | 1.128 | 0.124 | 0.145 | Rejected |
| Joined-view update, 5,001 partner rows | 1.123 | 0.130 | 0.140 | Rejected |
| Geographic mixed turns, forward | 2.020 | 0.161 | Mismatch | Rejected |
| Geographic mixed turns, reverse | 1.882 | 0.140 | Mismatch | Rejected |
| Geographic near-hole touch | 1.845 | 0.131 | Mismatch | Rejected |
| Geographic near-hole crossing | 2.403 | 0.148 | Mismatch | Rejected |
| Japanese ngram wildcard | 0.446 | 0.332 | Parser DDL rejected | Parser DDL rejected |
| Japanese ngram exclusion | 0.650 | 0.467 | Parser DDL rejected | Parser DDL rejected |

Fsdb's correlated equality and range counts took 1.4× and 2.3×
MySQL's median latency. MariaDB led those queries and Fsdb beat Dolt
on both. Fsdb's joined-view update took 8.6× MySQL's median latency.
The two prefix-join cases stayed near 1.1 ms in Fsdb when the partner
table grew from 501 to 5,001 rows.

Fsdb and MySQL used range access on the planar spatial index.
MariaDB also chose its index and returned nine rows where MySQL and
Fsdb returned one. Fsdb matched all four geographic edge fixtures
at 12.6–16.3× MySQL's median latency. The planar result is consistent
with the earlier fix for collation-tagged SQL literals.
[Spatial index before-and-after](2026-11-18-spatial-literal-index.md).

Fsdb matched all five CJK result checks at 1.3–1.5× MySQL's median
latency. MariaDB and Dolt rejected the tested `WITH PARSER ngram`
DDL.

The native MySQL oracle separately checked row membership and
relevance in 594 comparisons across 18 cache lifecycle stages.
All matched. The per-table and cross-database ngram pressure
probes also matched. Accounting for default word-parser tables
under shared cache pressure remains open.
[Oracle](../../torture/scripts/ngram-multicol-oracle.py).
[Full-text gaps](../../GAPS.md#11-full-text-search).

## Other topologies

The earlier single-host TiDB 8.5.8 stack reached 23.5k reads/s, 10.6k
disjoint updates/s, and 2.5k hot-row updates/s at 16 clients. Its read
p99 was 1.10 ms. That run used one TiDB, one TiKV, one PD, and one
replica. [TiDB measurement and settings](eeced6b1-peer-refresh.md#other-topologies).
[TiDB compatibility reference](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

Percona Server 8.4.11-12 and MySQL each reached about 44.7k point
reads/s at 16 clients in matched ARM64 Linux containers. Fsdb reached
11.9k/s in that setup. The current container runtime did not respond
to a health check. [Percona and MySQL container measurements](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

## Reproduction

Start a disposable native server on a free loopback port using the
settings above. Pass 1 runs Fsdb, MySQL, MariaDB, then Dolt. Pass 2
reverses both the engine order and the command order below.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" projected-range-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Raw per-pass CSVs cover [point reads](ac7d4a67-peer-comparison-reads.csv),
[durable updates](ac7d4a67-peer-comparison-writes.csv),
[SQL features](ac7d4a67-peer-comparison-features.csv), and the
[Fsdb GC control](ac7d4a67-peer-comparison-gc.csv).
