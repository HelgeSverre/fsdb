# Durable native peers and projected correlated ranges

The 2026-11-26 run compares Fsdb `2e54f0c7`, MySQL 8.4.11, MariaDB
11.8.10, and Dolt 2.7.7 on an Apple M2 Max with 12 cores, 32 GiB of RAM,
macOS 15.6, and about 12 GiB of swap in use. One benchmark server runs at
a time with a fresh data directory on the same IPv4 loopback port. The
second pass reverses engine and script order. MySQL 8.4 supplies the
expected SQL results.

Fsdb uses Release, .NET 10.0.12, Server GC, and WAL. MySQL and MariaDB
use a 256 MiB InnoDB buffer pool, commit flushing set to 1, and binary
logging disabled. Dolt uses its default journal and ordinary SQL commits
with `dolt_transaction_commit=0`. MySqlConnector 2.6.2 uses text commands
with TLS and pooling disabled. [Dolt transaction settings](https://www.dolthub.com/docs/sql-reference/version-control/dolt-sysvars/#dolt_transaction_commit).

## Point reads and durable writes

Each workload uses 10,000 rows at READ COMMITTED, one connection and one
outstanding operation per worker, a 1.5-second warmup, and six measured
seconds. Reads check the ID, name, age, and row count. Writes increment an
indexed column in autocommit and reconcile the stored counter with server
acknowledgements after warmup and after measurement. Cells show ops/s and
p99 milliseconds averaged across the two passes. Short samples on a
shared host give directional results.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,439 / 0.26 | 27,029 / 0.05 | 29,368 / 0.04 | 16,202 / 0.12 |
| 1 | Disjoint updates | 7,041 / 0.21 | 14,156 / 0.13 | 19,468 / 0.08 | 153 / 10.84 |
| 1 | Hot-row updates | 7,249 / 0.21 | 14,003 / 0.14 | 18,242 / 0.08 | 156 / 10.62 |
| 16 | Primary-key reads | 30,294 / 9.01 | 117,820 / 0.21 | 119,715 / 0.21 | 79,628 / 0.65 |
| 16 | Disjoint updates | 19,983 / 9.65 | 56,739 / 0.50 | 66,307 / 0.62 | 159 / 115.65 |
| 16 | Hot-row updates | 7,494 / 11.41 | 24,030 / 3.16 | 42,857 / 0.60 | Counter mismatch |

At 16 clients Fsdb delivered 26% of MySQL's point-read throughput,
35% of disjoint-update throughput, and 31% of hot-row throughput. Twice
Fsdb's measured point-read rate would be 60.6k ops/s, or 51% of MySQL's
rate. Fsdb's point-read p99 was about 42 times MySQL's. MariaDB tracked
MySQL on reads and led durable update throughput in this fixture.
Dolt read 2.6 times as fast as Fsdb at 16 clients. Its stored hot-row
counter advanced by 83 and 80 for 1,202 and 1,188 acknowledged updates.
The other Dolt write lanes reconciled their counters.

## SQL features

Correlated queries use 500 parents and 10,000 children. The derived-table
and CTE cases rename the columns of the same `(parent_id, id)` index.
Each projected case has one preflight, four warmups, and 30 measured
queries. The joined-view workloads check affected rows and final stored
values. The prefix cases also verify that duplicate join matches update
each target once. The spatial and CJK fixtures use 10,000 entries each.
The harness checks results during timing and publishes latency only for
matching results. Values are the mean of the two median timings in
milliseconds.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.039 | 1.416 | 1.039 | 2.760 |
| Correlated range count | 4.010 | 1.687 | 1.153 | 7.065 |
| Physical `COUNT(*)` control | 2.913 | 1.655 | 1.169 | 7.142 |
| Nested derived `COUNT(*)` | 3.019 | 1.693 | 1.235 | 11.846 |
| Nested derived `COUNT(payload)` | 13.939 | 2.388 | 1.508 | 11.822 |
| Nested derived `SUM(sequence_no)` | 18.155 | 1.727 | 1.250 | 11.597 |
| CTE `COUNT(*)` | 3.796 | 1.673 | 1.191 | 11.365 |
| Derived `COUNT(*)` with a source null test | 4.770 | 2.373 | 1.462 | 11.328 |
| Joined-view update | 1.019 | 0.120 | 0.127 | Rejected |
| Joined-view update, 501 partner rows | 1.158 | 0.124 | 0.149 | Rejected |
| Joined-view update, 5,001 partner rows | 1.153 | 0.126 | 0.134 | Rejected |
| Indexed planar `MBRContains` | 0.221 | 0.118 | Boundary mismatch | Rejected |
| Geographic antimeridian distance | 0.139 | 0.076 | Rejected | Rejected |
| Japanese ngram phrase | 0.677 | 0.452 | Index DDL rejected | Index DDL rejected |
| Chinese ngram phrase | 0.713 | 0.459 | Index DDL rejected | Index DDL rejected |
| Korean ngram phrase | 0.508 | 0.431 | Index DDL rejected | Index DDL rejected |

Fsdb matched MySQL on all checked SQL results in both passes. Projected
`COUNT(*)` cases took 1.8–2.3 times MySQL's median. The nested
`COUNT(payload)` took 5.8 times as long and `SUM(sequence_no)` took 10.5
times as long. Fsdb's `SUM` medians were 15.0 and 21.3 ms across the two
passes. Dolt returned the same results for all six projected cases and
was faster than Fsdb on the two nested aggregates that read a payload.
[Previous projected-range before and after measurements](2026-11-25-correlated-integer-range-count.md#composite-ranges-through-projections).

The joined-view update stayed near 1.15 ms while the partner table grew
tenfold. It took about nine times MySQL's time in both prefix cases.
Fsdb, MySQL, and MariaDB all used `ix_g` for the planar query. MariaDB
included eight boundary points in addition to the one interior point.
Fsdb's spatial probes took 1.8–1.9 times MySQL's median and the three
CJK phrase queries took 1.2–1.6 times as long.

## Other topologies

The earlier matched ARM64 container run measured 44.7k point reads/s
with 16 clients on MySQL, 44.8k on Percona Server 8.4.11-12, and 11.9k
on Fsdb. All three passed its ten feature lanes. Docker did not respond
to the daemon probe for this refresh. [Percona container results](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

The earlier TiDB 8.5.8 run used one local TiDB, TiKV, and PD process
with one replica. With 16 clients, it measured 23.5k point reads/s,
10.6k disjoint updates/s, and 2.5k hot-row updates/s, with p99 of 1.10,
2.55, and 10.28 ms respectively. Its tested correlated equality and range
counts took 2.59 and 61.29 ms. The joined-view writes, spatial queries,
and CJK searches were rejected in that setup. These results describe the
single-host configuration. [TiDB measurements and topology](eeced6b1-peer-refresh.md#other-topologies).
[TiDB feature support](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Reproduction

Start a disposable server with the settings above. Run the engines in
the order Fsdb, MySQL, MariaDB, Dolt. On pass 2 reverse both engine and
command order and set `pass=2`. The scripts reverse their internal lanes
on even-numbered passes.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" projected-range-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Per-pass measurements are in the [reads](2e54f0c7-peer-refresh-reads.csv),
[writes](2e54f0c7-peer-refresh-writes.csv), and
[features](2e54f0c7-peer-refresh-features.csv) CSVs.
