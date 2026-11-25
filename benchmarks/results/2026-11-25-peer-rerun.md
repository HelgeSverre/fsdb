# Fsdb against MySQL-compatible servers

The 2026-11-25 run benchmarks Fsdb `d3b25c8a` against MySQL 8.4.11,
MariaDB 11.8.10, and Dolt 2.7.7 natively. A separate ARM64 Linux container
run compares Fsdb, MySQL 8.4.11, and Percona Server 8.4.11-12.
MySQL 8.4 supplies the expected SQL results.

The Apple M2 Max host had 12 cores, 32 GiB RAM, macOS 15.6, and about
12 GiB of swap already in use. Only one benchmark server ran at a time.
Both runs reversed engine and workload order on the second pass. Tables
average the two pass statistics, with container write throughput shown as
ranges. The warm data, short windows, and shared host make the results
directional.

## Native reads and durable writes

Each lane uses 10,000 rows, one connection and one outstanding operation
per worker, and READ COMMITTED. Clients use MySqlConnector 2.6.2 over
loopback TCP with TLS and pooling disabled. After 1.5 seconds of warmup,
each timed window runs for six seconds. The read fixture checks the ID,
name, age, and row count for randomly selected primary keys. The durable
write fixture increments an indexed column in autocommit and verifies
the committed counter against acknowledgements before and after timing.

Fsdb ran in Release on .NET 10.0.12 with Server GC and WAL enabled.
MySQL and MariaDB used a 256 MiB InnoDB buffer pool, commit flushing set
to 1, and binary logging disabled. Dolt used its default journal and
ordinary SQL commits with `dolt_transaction_commit` off.

Cells show operations per second followed by p99 latency in milliseconds.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7528 / 0.20 | 26721 / 0.05 | 29267 / 0.05 | 15931 / 0.13 |
| 1 | Disjoint updates | 6868 / 0.22 | 13585 / 0.14 | 17143 / 0.14 | 170 / 10.06 |
| 1 | Hot-row updates | 6815 / 0.22 | 13646 / 0.14 | 17614 / 0.14 | 169 / 10.52 |
| 16 | Primary-key reads | 27052 / 9.58 | 118058 / 0.21 | 119336 / 0.21 | 79573 / 0.65 |
| 16 | Disjoint updates | 18926 / 11.08 | 56593 / 0.52 | 65619 / 0.63 | 166 / 111.36 |
| 16 | Hot-row updates | 7524 / 11.84 | 24060 / 3.14 | 42858 / 0.62 | Counter mismatch |

With 16 clients, Fsdb reached 23% of MySQL throughput for point reads,
33% for disjoint updates, and 31% for hot-row updates. Its point-read
trials ranged from 24.7k to 29.4k ops/s. Doubling the mean would reach
54.1k ops/s, or 46% of MySQL at the measured rate. The mean point-read
p99 was 9.58 ms for Fsdb and 0.214 ms for MySQL.

MariaDB stayed close to MySQL for reads and led the tested updates.
Dolt read about 2.9× faster than Fsdb at 16 clients. Fsdb completed
about 114× as many disjoint updates. Dolt acknowledged 1,240 and 1,293
hot-row updates in the two timed windows, while its committed counter
increased by 85 and 88. The table withholds throughput for that lane.

## SQL feature latency

The fixture checks correlated counts over 500 parents and 10,000
children, a checked nested joined-view update, a spatial index query
over 10,000 points, geographic antimeridian distance, and Boolean CJK
phrases over a 10,000-document ngram index using `utf8mb4_bin`. Each
workload has 60 to 150 measured warm operations per engine. The client verifies every timed result or
affected-row count. Values are p50 latency in milliseconds.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 3.772 | 1.410 | 1.048 | 2.795 |
| Correlated equality and range count | 8.222 | 1.676 | 1.185 | 7.157 |
| Checked joined-view update | 1.850 | 0.129 | 0.147 | Rejected |
| Indexed planar `MBRContains` | 0.246 | 0.115 | Different result | Rejected |
| Geographic antimeridian distance | 0.137 | 0.074 | Rejected | Rejected |
| Japanese phrase | 0.764 | 0.447 | Rejected | Rejected |
| Chinese phrase | 0.844 | 0.456 | Rejected | Rejected |
| Korean phrase | 0.750 | 0.424 | Rejected | Rejected |

Fsdb agreed with MySQL on every checked result. MariaDB used the planar
index, as did MySQL and Fsdb. MariaDB returned nine points including eight
on the boundary, while MySQL and Fsdb returned one interior point. The
MariaDB build rejected the geographic constructor and the MySQL ngram
parser. Dolt rejected the checked joined-view update, lacked the tested
spatial function and geographic SRID, and rejected the ngram DDL.

## ARM64 Linux containers

The same macOS client connected over localhost-published ports to one
OrbStack Linux container at a time. Each server had a four-CPU limit,
2 GiB of memory, and a disposable named volume for data. Fsdb used
the same managed Release build, WAL, and Server GC on the ARM64 .NET
10.0.12 runtime. Both InnoDB engines used a 256 MiB buffer pool, commit
flushing set to 1, and binary logging disabled. The [MySQL image pin](../../torture/compose.yaml)
is shared with the oracle harness.

- Percona image digest `sha256:e759516b1b2f446ec83c06e5cd47ac9a4d13b3a19a8d155da3c555feed621ac0`.
- .NET runtime image digest `sha256:ec7cd715c9ab3831d134c7041d0d65891b97a185717a49147a22f75090b44338`.

Cells show operations per second followed by p99 latency in milliseconds.

| Clients | Workload | Fsdb | MySQL | Percona |
| ---: | --- | ---: | ---: | ---: |
| 1 | Primary-key reads | 2977 / 3.32 | 11020 / 0.17 | 11160 / 0.16 |
| 16 | Primary-key reads | 11085 / 6.07 | 47056 / 0.68 | 46886 / 0.68 |

| Workload, ops/s at 16 clients | Fsdb | MySQL | Percona |
| --- | ---: | ---: | ---: |
| Disjoint updates | 1683–3910 | 797–6774 | 1420–7879 |
| Hot-row updates | 785–1029 | 4160–4759 | 5534–7366 |

MySQL and Percona stayed within 2% of each other on 16-client point
reads in each pass. Fsdb averaged 24% of MySQL's throughput. Both
InnoDB engines varied by more than fivefold between passes on 16-client
disjoint updates, so these samples give a clearer read comparison than
a durable write ranking. The raw data includes both client counts
and per-pass tail latency. All three engines passed the same SQL
feature and write counter checks in both passes.

Feature values are p50 latency in milliseconds.

| Workload | Fsdb | MySQL | Percona |
| --- | ---: | ---: | ---: |
| Correlated equality count | 3.945 | 1.158 | 1.121 |
| Correlated equality and range count | 8.580 | 1.464 | 1.425 |
| Checked joined-view update | 3.712 | 1.078 | 1.056 |
| Indexed planar `MBRContains` | 0.419 | 0.137 | 0.145 |
| Geographic antimeridian distance | 0.298 | 0.089 | 0.107 |
| Japanese phrase | 1.214 | 0.447 | 0.477 |
| Chinese phrase | 1.239 | 0.457 | 0.466 |
| Korean phrase | 0.974 | 0.420 | 0.434 |

TiDB was outside this run. A durable TiDB comparison needs a stated
cluster topology and an OLTP fixture compatible with its SQL support.
[TiDB compatibility documentation](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Reproduction and raw samples

Use an empty, disposable database server on a free loopback port. Run
one server at a time and use the same pass number for each engine.
For the second pass, change the final command arguments to pass
2 and reverse the order of engines and scripts. The scripts reverse
their own lane order on even-numbered passes. For the container
comparison, publish each server to the same free port and pass that port
to the same client commands.

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3369 1
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3369 1
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3369 1 point-reads
```

- Native [features](2026-11-25-peer-rerun-native-features.csv), [point reads](2026-11-25-peer-rerun-native-reads.csv), and [writes](2026-11-25-peer-rerun-native-writes.csv).
- Containers [features](2026-11-25-peer-rerun-linux-features.csv), [point reads](2026-11-25-peer-rerun-linux-reads.csv), and [writes](2026-11-25-peer-rerun-linux-writes.csv).
