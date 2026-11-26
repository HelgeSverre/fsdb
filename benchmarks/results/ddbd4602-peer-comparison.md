# Durable native database peers at `ddbd4602`

Fsdb `ddbd4602`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7 ran
one at a time on the same 12-core Apple M2 Max with 32 GiB of RAM
and macOS 15.6 on 2026-11-26. Each process used a fresh data directory
and IPv4 loopback. The second pass reversed engine order and suite order.
The host was shared, with about 12 GiB of swap in use. This is a
short warm-workload comparison. MySQL 8.4.11 is the compatibility
reference.

Fsdb ran in Release on .NET 10.0.12 with WAL and `DOTNET_gcServer=1`.
MySQL and MariaDB used a 256 MiB InnoDB buffer pool, commit flush
set to 1, and binary logging disabled. MySQL used ngram token size 2.
Dolt used its default journal with ordinary SQL commits and
`dolt_transaction_commit=0`. MySqlConnector 2.6.2 sent text commands
with TLS and pooling disabled.
[Dolt 2.7.7](https://github.com/dolthub/dolt/releases/tag/v2.7.7).
[Dolt transaction settings](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sysvars/#dolt_transaction_commit).

## Point reads and durable updates

The load fixture uses 10,000 rows, READ COMMITTED, and one outstanding
operation per client. Each trial warms up for 1.5 seconds and measures
for six seconds. Reads validate the row ID, name, and age. Each update
increments an indexed column in autocommit. The harness reconciles
acknowledged writes with the committed counter separately for both
windows. Cells show operations per second and p99 latency in milliseconds,
averaged across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,421 / 0.27 | 27,120 / 0.05 | 29,396 / 0.05 | 16,112 / 0.12 |
| 1 | Disjoint updates | 7,043 / 0.21 | 14,316 / 0.15 | 20,517 / 0.09 | 160 / 11.05 |
| 1 | Hot-row updates | 7,377 / 0.21 | 14,077 / 0.14 | 19,484 / 0.08 | 153 / 11.20 |
| 16 | Primary-key reads | 29,936 / 9.37 | 118,098 / 0.20 | 118,593 / 0.21 | 79,076 / 0.65 |
| 16 | Disjoint updates | 19,991 / 9.90 | 56,826 / 0.50 | 66,104 / 0.60 | 152 / 119.38 |
| 16 | Hot-row updates | 7,500 / 11.42 | 23,891 / 3.14 | 42,757 / 0.59 | Counter mismatch |

At 16 clients Fsdb reached 25% of MySQL's point-read throughput, 35%
of disjoint-update throughput, and 31% of hot-row throughput. Doubling
the measured read rate to 59.9k/s would reach 51% of MySQL and MariaDB
and 76% of Dolt at their observed rates. Fsdb read p99 was 9.37 ms
against MySQL's 0.20 ms. The two Fsdb passes delivered 28.5k and 31.4k
reads/s.

Dolt's timed hot-row counter advanced by 82 and 81 after the clients
acknowledged 1,186 and 1,190 updates, respectively. The other timed
write lanes reconciled. [Previous native run](cc2cb5f6-peer-comparison.md).

### WAL attachment control

Fsdb ran the point-read fixture again using the same binary, Server GC,
and fresh servers. Pass 1 ran WAL first and pass 2 ran memory first.
The load windows were read-only after seeding.

| Clients | WAL ops/s / p99 ms | Memory ops/s / p99 ms |
| ---: | ---: | ---: |
| 1 | 7,595 / 0.22 | 7,536 / 0.23 |
| 16 | 28,890 / 7.93 | 28,466 / 8.03 |

In memory, the 16-client read rate was 2.4% higher in pass 1 and 5.9%
lower in pass 2. The read gap follows the server even when WAL is
detached. [Storage control data](ddbd4602-peer-comparison-storage.csv).
[Earlier point-read profile](2026-11-18-point-read-diagnosis.md).

## SQL features

The correlated queries use 500 parents and 10,000 children, including
nested projections and a CTE over a composite index. The joined-view
fixtures verify the affected-row count and final committed values.
The planar spatial fixture uses an indexed 10,000-point grid.
Each geographic turn query checks four predicates on WGS 84 inputs.
The CJK fixture uses 10,000 binary-collated articles and an ngram index.
It verifies the returned ID sum for three phrases and two Boolean
operator queries. SQL cells average the two per-pass p50s in milliseconds
after a preflight and four warmups. Rejected queries and mismatched
results have no latency entry.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.017 | 1.421 | 1.044 | 2.761 |
| Correlated range count | 4.035 | 1.677 | 1.163 | 7.057 |
| Joined-view update | 1.005 | 0.111 | 0.134 | Rejected |
| Indexed planar `MBRContains` | 0.226 | 0.125 | Mismatch | Rejected |
| Geographic antimeridian distance | 0.131 | 0.077 | Rejected | Rejected |
| Japanese ngram phrase | 0.644 | 0.445 | Parser DDL rejected | Parser DDL rejected |
| Chinese ngram phrase | 0.686 | 0.454 | Parser DDL rejected | Parser DDL rejected |
| Korean ngram phrase | 0.611 | 0.427 | Parser DDL rejected | Parser DDL rejected |
| Physical `COUNT(*)` control | 3.020 | 1.652 | 1.178 | 7.175 |
| Nested derived `COUNT(*)` | 3.693 | 1.704 | 1.229 | 11.853 |
| Nested derived `COUNT(payload)` | 4.893 | 2.395 | 1.485 | 11.690 |
| Nested derived `SUM(sequence_no)` | 5.793 | 1.719 | 1.260 | 11.649 |
| CTE `COUNT(*)` | 4.175 | 1.668 | 1.187 | 11.247 |
| Derived `COUNT(*)` with a source null test | 4.695 | 2.402 | 1.470 | 11.508 |
| Joined-view update, 501 partner rows | 1.101 | 0.131 | 0.142 | Rejected |
| Joined-view update, 5,001 partner rows | 1.113 | 0.134 | 0.140 | Rejected |
| Geographic mixed turns, forward | 2.077 | 0.163 | Mismatch | Rejected |
| Geographic mixed turns, reverse | 1.920 | 0.140 | Mismatch | Rejected |
| Geographic near-hole touch | 1.865 | 0.131 | Mismatch | Rejected |
| Geographic near-hole crossing | 2.393 | 0.152 | Mismatch | Rejected |
| Japanese ngram wildcard | 0.434 | 0.337 | Parser DDL rejected | Parser DDL rejected |
| Japanese ngram exclusion | 0.665 | 0.469 | Parser DDL rejected | Parser DDL rejected |

MariaDB led the correlated queries. Fsdb took 1.4–3.4× MySQL's median
latency and beat Dolt in every correlated lane. The joined-view update
was 9.1× MySQL's median. Fsdb stayed near 1.1 ms when the partner table
grew from 501 to 5,001 rows with two matching partners.

Fsdb and MySQL used range access through the planar spatial index.
MariaDB also chose the index and returned nine rows where MySQL and
Fsdb returned one. Fsdb matched all four geographic edge fixtures.
The near-hole crossing failed the result check in the
[earlier four-engine run](cc2cb5f6-peer-comparison.md) and passed the
[later MySQL refresh](2026-11-26-spatial-cjk-refresh.md). The four
edge queries took 12.7–15.7× MySQL's median.

Fsdb matched the CJK ID sums at 1.3–1.5× MySQL's median latency.
MariaDB and Dolt both rejected the tested `WITH PARSER ngram` DDL.
The timing fixture checks membership via an ID sum. The separate
cache-lifecycle oracle checks row IDs and relevance scores.
All 221 cache-lifecycle comparisons matched MySQL in this run. Those
cases are a finite fixture. The gap for frequency counts from deleted
postings remains open at this revision.
[Oracle](../../torture/scripts/ngram-multicol-oracle.py).
[Full-text gaps](../../GAPS.md#11-full-text-search).

## TiDB and Percona

The earlier local TiDB 8.5.8 run used one TiDB, TiKV, and PD instance
with one replica. At 16 clients it measured 23.5k primary-key reads/s,
10.6k disjoint updates/s, and 2.5k hot-row updates/s. Its read p99 was
1.10 ms. [TiDB results](eeced6b1-peer-refresh.md#other-topologies).

Percona Server 8.4.11-12 recorded 44.8k primary-key reads/s at 16
clients in matched ARM64 containers, with 44.7k on MySQL and 11.9k
on Fsdb. Its read p99 was 0.71 ms versus MySQL's 0.69 ms.
The current container runtime health check timed out.
[Percona results](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

## Reproduction

Start each engine on a free loopback port with the settings above.
Use Fsdb, MySQL, MariaDB, then Dolt for pass 1. Run the following
commands per engine. Reverse engine and command order for pass 2.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" projected-range-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Per-pass measurements are in the [reads](ddbd4602-peer-comparison-reads.csv),
[writes](ddbd4602-peer-comparison-writes.csv), and
[features](ddbd4602-peer-comparison-features.csv) CSVs.
