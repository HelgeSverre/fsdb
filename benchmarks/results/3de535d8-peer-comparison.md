# Native peer benchmark at `3de535d8`

The 2026-11-25 run compares Fsdb `3de535d8`, MySQL 8.4.11, MariaDB
11.8.10, and Dolt 2.7.7 on an Apple M2 Max with 12 cores, 32 GiB of RAM,
and macOS 15.6. MySQL is the correctness reference. Each engine runs on
native loopback TCP in isolation from the other benchmark engines. Pass 2
reverses engine and workload order. Tables average the two per-pass
statistics. The host is shared and had about 12 GiB of swap in use.

Fsdb uses Release .NET 10.0.12 with WAL and Server GC. MySQL and MariaDB
use InnoDB with a 256 MiB buffer pool, commit flushing set to 1, and binary
logging disabled. Dolt uses its default journal and ordinary SQL commits.
The client uses MySqlConnector 2.6.2, with TLS and pooling disabled.

## Read and write throughput

Each lane uses 10,000 rows, one outstanding operation per connection,
READ COMMITTED, 1.5 seconds of warmup, and six seconds of measurement.
Random primary-key lookups verify the ID, name, age, and row count. Updates
increment an indexed column in autocommit. The harness checks each affected
row count and reconciles the stored counter with acknowledged writes in
both the warmup and measured phases.

Cells show operations per second and p99 latency in milliseconds.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,389 / 0.27 | 27,260 / 0.05 | 29,272 / 0.05 | 16,072 / 0.12 |
| 1 | Disjoint updates | 7,075 / 0.22 | 13,795 / 0.15 | 20,140 / 0.09 | 156 / 10.78 |
| 1 | Hot-row updates | 6,987 / 0.23 | 14,181 / 0.14 | 18,454 / 0.09 | 154 / 10.52 |
| 16 | Primary-key reads | 28,663 / 8.88 | 117,706 / 0.21 | 118,559 / 0.20 | 79,146 / 0.65 |
| 16 | Disjoint updates | 20,308 / 9.59 | 56,213 / 0.50 | 66,867 / 0.59 | 157 / 121.90 |
| 16 | Hot-row updates | 7,566 / 10.94 | 19,592 / 3.65 | 42,724 / 0.62 | Counter mismatch |

At 16 clients, Fsdb reached 24% of MySQL's point-read throughput, 36%
of its disjoint update throughput, and 39% of its hot-row update
throughput. Its point-read rate ranged from 26.8k to 30.5k ops/s.
Doubling the mean to 57.3k ops/s would reach 49% of MySQL, 48% of
MariaDB, and 72% of Dolt at their measured rates. Fsdb's p99 point
read was 8.9 ms versus 0.21 ms on MySQL.

Dolt's 16-client hot-row runs acknowledged 1,200 and 1,159 updates.
The stored counter advanced by 82 and 79, respectively. Both warmup
counters also diverged. The table excludes timing for these runs.

## SQL feature latency

The correlated fixtures use 500 parents and 10,000 children. The nullable
count fixture checks the count with and without a residual filter. The
joined-view fixtures check affected counts and the stored value, including
when two rows on the partner side match one target. The spatial fixtures
check indexed planar containment across 10,000 points and geographic
distance across the antimeridian. The full-text fixture verifies
Japanese, Chinese, and Korean Boolean phrase results on 10,000 documents
using the MySQL ngram parser and binary collation. Each lane has one
preflight, four warmups, and 15 to 150 measured calls per pass.

Cells show median latency in milliseconds.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.063 | 1.426 | 1.049 | 2.759 |
| Correlated equality and range count | 4.271 | 1.685 | 1.163 | 7.081 |
| Nullable fixture, `COUNT(*)` control | 3.342 | 1.642 | 1.167 | 7.097 |
| Nullable fixture, `COUNT(c.bucket)` | 4.240 | 2.374 | 1.426 | 11.180 |
| Nullable fixture, filtered `COUNT(c.bucket)` | 8.156 | 2.400 | 1.439 | 14.668 |
| Nested joined-view update | 1.013 | 0.122 | 0.126 | Rejected query |
| Joined-view prefix, 501 partner rows | 1.121 | 0.134 | 0.150 | Rejected query |
| Joined-view prefix, 5,001 partner rows | 1.093 | 0.124 | 0.142 | Rejected query |
| Indexed planar `MBRContains` | 0.229 | 0.120 | Boundary mismatch | Rejected query |
| Geographic antimeridian distance | 0.144 | 0.075 | Rejected query | Rejected query |
| Japanese phrase | 0.656 | 0.448 | Rejected index DDL | Rejected index DDL |
| Chinese phrase | 0.708 | 0.457 | Rejected index DDL | Rejected index DDL |
| Korean phrase | 0.595 | 0.426 | Rejected index DDL | Rejected index DDL |

Fsdb and MySQL passed all feature checks in both passes. Both used
range access on `ix_g`. MariaDB also used the index and included eight
additional boundary points. MariaDB rejected the geographic constructor
and the ngram index definition. Dolt rejected the tested joined-view
updates, spatial queries, and ngram index definition.

Fsdb took 1.4× MySQL's median time on the equality count, 2.5× on the
range count, and 3.4× on the filtered nullable count. The joined-view
updates took 8.3–8.8×. The spatial queries took about 1.9×, and the CJK
phrases took 1.4–1.6×. The [joined-view scheduling checks](2026-11-25-bound-joined-view-update.md)
cover lock behavior under concurrent transactions separately. The CJK
benchmark covers Boolean phrases, with wildcard ranking outside its scope.

The [earlier peer comparison](2026-11-25-peer-comparison.md) records
matched ARM64 containers for Percona and a separate TiDB 8.5.8 run on
one host. At 16 clients, Percona and MySQL each reached about 44.7k
point reads/s in the container setup, and Fsdb reached 11.9k.
The local TiDB stack reached 24.3k reads/s at 1.09 ms p99. Those
measurements use their respective setups and an earlier Fsdb revision.

## Reproduction

Run against fresh disposable loopback servers with an empty-password root
account and the settings above. For pass 1, use Fsdb, MySQL, MariaDB, and Dolt
in that order. Reverse the engine and command order for pass 2. The scripts
reverse applicable workloads within each invocation.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" nullable-count-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Per-pass data includes [reads](3de535d8-peer-reads.csv),
[writes](3de535d8-peer-writes.csv), and [feature latencies](3de535d8-peer-features.csv).
