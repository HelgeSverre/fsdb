# Durable native database peers at `cc2cb5f6`

Fsdb `cc2cb5f6`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7 ran
natively on the same Apple M2 Max on 2026-11-26. The host had 12 cores,
32 GiB of RAM, macOS 15.6, and about 12 GiB of swap in use. Benchmark
servers took turns on IPv4 loopback with fresh data directories. The
second pass reversed engine and suite order. These measurements describe
warm workloads on a shared host. The MySQL results are the compatibility
reference.

Fsdb ran in Release on .NET 10.0.12 with WAL and `DOTNET_gcServer=1`.
MySQL and MariaDB used a 256 MiB InnoDB buffer pool, commit flushing
set to 1, and binary logging disabled. MySQL used ngram token size 2.
Dolt used its default journal and ordinary SQL commits with
`dolt_transaction_commit=0`. MySqlConnector 2.6.2 used text commands
over loopback with TLS and pooling disabled.
[Dolt 2.7.7](https://github.com/dolthub/dolt/releases/tag/v2.7.7).
[Dolt transaction settings](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sysvars/#dolt_transaction_commit).

## Point reads and durable updates

Each fixture uses 10,000 rows, READ COMMITTED, and one outstanding
operation per client. Trials warm up for 1.5 seconds and measure for six
seconds. Reads validate the ID, name, and age. Each update increments an
indexed column in autocommit. The harness reconciles committed changes
against acknowledged writes for the warmup and measurement windows
separately. Cells show operations per second and p99 milliseconds,
averaged across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,127 / 0.28 | 30,462 / 0.07 | 34,252 / 0.04 | 16,055 / 0.13 |
| 1 | Disjoint updates | 6,176 / 0.26 | 13,965 / 0.16 | 19,444 / 0.09 | 155 / 11.41 |
| 1 | Hot-row updates | 6,829 / 0.22 | 13,942 / 0.18 | 18,629 / 0.08 | 154 / 11.11 |
| 16 | Primary-key reads | 30,515 / 9.36 | 116,819 / 0.23 | 118,951 / 0.22 | 79,302 / 0.64 |
| 16 | Disjoint updates | 18,969 / 10.28 | 54,280 / 0.53 | 60,638 / 0.70 | 157 / 117.93 |
| 16 | Hot-row updates | 7,387 / 12.19 | 19,635 / 3.95 | 37,598 / 2.45 | Counter mismatch |

At 16 clients, Fsdb reached 26% of MySQL's point-read throughput,
35% of disjoint-update throughput, and 38% of hot-row throughput.
Doubling the observed Fsdb read rate to 61.0k/s would reach 52% of
MySQL, 51% of MariaDB, and 77% of Dolt at their measured rates.
Fsdb's point-read p99 was about 41× MySQL's. The two Fsdb passes
delivered 31.4k and 29.6k point reads/s, comparable to the prior
run's 27.8k–31.2k/s. [Previous run](d11c7f65-peer-comparison.md).

Dolt averaged 157 verified disjoint updates/s. Its timed hot-row
counter advanced by 83 and 84 after 1,219 and 1,224 acknowledged
updates, respectively. All other measured write lanes reconciled.

## SQL feature latency

The correlated queries use 500 parents and 10,000 children. Derived
tables and CTEs rename or reorder projections of the same composite
index. Joined-view updates check affected rows and final values,
including once-per-target updates when two rows match. The planar
spatial fixture uses an indexed 10,000-point grid. The geographic
turn fixtures compare four predicates on a WGS 84 line and a polygon
with one hole. The CJK fixture uses 10,000 documents with an
`ngram` full-text index and `utf8mb4_bin`. Each query runs a verified
preflight and four warmups. Cells average the per-pass p50 latency in
milliseconds across 30–150 checked executions per pass. Rejected
queries and result mismatches have no latency.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.088 | 1.448 | 1.054 | 2.775 |
| Correlated range count | 3.754 | 1.708 | 1.177 | 7.060 |
| Joined-view update | 1.042 | 0.130 | 0.119 | Rejected |
| Indexed planar `MBRContains` | 0.223 | 0.119 | Mismatch | Rejected |
| Geographic antimeridian distance | 0.132 | 0.071 | Rejected | Rejected |
| Japanese ngram phrase | 0.689 | 0.454 | Parser DDL rejected | Parser DDL rejected |
| Chinese ngram phrase | 0.741 | 0.477 | Parser DDL rejected | Parser DDL rejected |
| Korean ngram phrase | 0.496 | 0.440 | Parser DDL rejected | Parser DDL rejected |
| Physical `COUNT(*)` control | 3.564 | 1.677 | 1.197 | 7.146 |
| Nested derived `COUNT(*)` | 3.733 | 1.734 | 1.272 | 11.909 |
| Nested derived `COUNT(payload)` | 4.698 | 2.451 | 1.527 | 11.783 |
| Nested derived `SUM(sequence_no)` | 5.891 | 1.766 | 1.290 | 11.601 |
| CTE `COUNT(*)` | 4.213 | 1.710 | 1.220 | 11.348 |
| Derived `COUNT(*)` with a source null test | 4.598 | 2.467 | 1.482 | 11.341 |
| Joined-view update, 501 partner rows | 1.145 | 0.125 | 0.155 | Rejected |
| Joined-view update, 5,001 partner rows | 1.161 | 0.132 | 0.148 | Rejected |
| Geographic mixed turns, forward | 4.021 | 0.164 | Mismatch | Rejected |
| Geographic mixed turns, reverse | 4.082 | 0.142 | Mismatch | Rejected |
| Geographic near-hole touch | 3.940 | 0.132 | Mismatch | Rejected |
| Geographic near-hole crossing | Mismatch | 0.152 | Mismatch | Rejected |
| Japanese ngram wildcard | 0.653 | 0.341 | Parser DDL rejected | Parser DDL rejected |
| Japanese ngram exclusion | 1.135 | 0.466 | Parser DDL rejected | Parser DDL rejected |

The correlated queries took 1.4–3.3× MySQL's median latency.
Every correlated query ran faster in Fsdb than in Dolt. MariaDB led
these fixtures. Joined-view updates took 8.0–9.2× MySQL's median.
Fsdb stayed near 1.15 ms as the prefix fixture grew from 501 to 5,001
partner rows. The joined-view fixture used one autocommit client.

Fsdb, MySQL, and MariaDB reported range access through `ix_g` for
the planar spatial window. Fsdb took 1.9× MySQL's median. MariaDB
returned nine points where MySQL and Fsdb returned one. The three
matching geographic turn statements took 25–30× MySQL's median.
Each statement evaluates intersects, crosses, touches, and within
on the same WGS 84 inputs. In the remaining case, Fsdb reported a
touch where MySQL reported a crossing. MariaDB reported both
crossing and touching in all four fixtures. Dolt rejected
`ST_Crosses`.

Fsdb matched the checked ID sums for all five CJK queries. The
three phrase queries took 1.1–1.6× MySQL's median. The Japanese
wildcard `東京*` took 1.9×, and `+東京 -"東京駅"` took 2.4×.
MariaDB and Dolt both rejected the tested ngram index DDL.

## Geographic precision coverage

A separate differential run at the same revision, with
native MySQL 8.4.11, matched all 448 structured line/area cases
and all 1,000 cases in the initial seeded WGS 84 set, in both directions.
The 810-case dense WGS 84 offset sweep had one remaining mismatch,
down from 41 before the ordered-turn change. The independent 6,000-case
holdout had 15 mismatches, down from 17. Larger WGS 84 sweeps had
8/5,000 and 9/5,000 mismatches by direction. ETRS89 and NAD27 each had
2/1,000 mismatches. These sets deliberately overrepresent near-edge
inputs, and several sets probe nearby offsets of the same shapes.
[Geographic probe and reproduction](../../torture/scripts/geographic-edge-oracle.py).

In a standalone trace of the remaining crossing, retaining the original
reduced-latitude tangent made the trial intersection and its incoming
azimuth match Boost 1.84 exactly. The Fsdb SQL benchmark used the existing
solver. The tangent change still needs differential coverage across the
other geographic workloads.
[Boost's Sjöberg intersection implementation](https://github.com/boostorg/geometry/blob/boost-1.84.0/include/boost/geometry/formulas/sjoberg_intersection.hpp).

## TiDB and Percona

The earlier local TiDB 8.5.8 comparison used one TiDB, TiKV, and PD
process with one replica. With 16 clients it measured 23.5k primary-key
reads/s at 1.10 ms p99, 10.6k disjoint updates/s, and 2.5k hot-row
updates/s. The matched ARM64 container run measured 44.8k point
reads/s on Percona Server 8.4.11-12, 44.7k on MySQL, and 11.9k on Fsdb.
Those results describe their respective topologies. TiDB and Percona
figures come from the earlier runs. The Docker health probe timed out
during this refresh.
[TiDB results](eeced6b1-peer-refresh.md#other-topologies).
[Percona results](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

## Reproduction

Run Fsdb, MySQL, MariaDB, and Dolt in that order with a disposable data
directory per process. Run the first five commands in order for pass 1.
For pass 2, reverse the engine and command orders and set `pass=2`.
Run the edge controls separately in the same engine order for each pass.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" projected-range-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
```

Per-pass measurements are in the [reads](cc2cb5f6-peer-comparison-reads.csv),
[writes](cc2cb5f6-peer-comparison-writes.csv), and
[features](cc2cb5f6-peer-comparison-features.csv) CSVs.
