# Native peer benchmark at `2da21f23`

The 2026-11-25 run compares Fsdb `2da21f23`, MySQL 8.4.11, MariaDB
11.8.10, and Dolt 2.7.7 on an Apple M2 Max with 12 cores, 32 GiB of RAM,
and macOS 15.6. One benchmark server runs at a time on native loopback
TCP, with a fresh data directory. Pass 2 reverses the engine order and
the applicable workload order. The host is shared and had about 12 GiB
of swap in use. The tables average the two per-pass statistics.

Fsdb runs in Release on .NET 10.0.12 with Server GC and WAL. Both InnoDB
servers use a 256 MiB buffer pool, commit flushing set to 1, and binary
logging disabled. Dolt uses its default journal and ordinary SQL commits
with Git-style commits disabled. MySqlConnector 2.6.2 runs with TLS and
pooling disabled. MySQL 8.4 defines the expected SQL results.

## Durable throughput

The fixture uses 10,000 rows at READ COMMITTED and one outstanding
request per connection. Each lane warms up for 1.5 seconds and measures
for six seconds. Random primary-key reads verify the ID, name, age, and
number of rows. Updates increment an indexed column in autocommit.
The harness compares acknowledged updates with committed counters in
both the warmup and measured phases.

Each cell shows operations per second and p99 latency in milliseconds.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 6,981 / 0.28 | 27,576 / 0.06 | 28,912 / 0.06 | 16,247 / 0.14 |
| 1 | Disjoint updates | 6,359 / 0.26 | 13,317 / 0.19 | 17,945 / 0.15 | 170 / 10.31 |
| 1 | Hot-row updates | 6,500 / 0.26 | 13,412 / 0.19 | 17,685 / 0.11 | 171 / 10.41 |
| 16 | Primary-key reads | 28,089 / 10.04 | 110,823 / 0.26 | 111,048 / 0.25 | 71,599 / 0.78 |
| 16 | Disjoint updates | 17,640 / 11.50 | 51,978 / 0.58 | 59,958 / 0.68 | 165 / 126.25 |
| 16 | Hot-row updates | 6,774 / 12.84 | 23,049 / 3.17 | 41,053 / 0.66 | Counter mismatch |

At 16 clients, Fsdb reached 25% of MySQL's point-read throughput,
34% of its disjoint-update throughput, and 29% of its
hot-row-update throughput. Doubling Fsdb point reads to 56.2k/s
would reach 51% of MySQL, 51% of MariaDB, and 78% of Dolt at their
observed rates. Fsdb's point-read p99 was 38× MySQL's.

Dolt's 16-client hot-row runs acknowledged 1,288 and 1,278 measured
updates. The stored counter advanced by 87 and 86, respectively. Both
warmup counters also diverged. Timing for those runs is excluded.

## SQL feature latency

Each lane has a preflight, four warmups, and 15 to 150 measured
operations. The correlated fixtures use 500 parents and 10,000
children and verify the sum of the scalar results across parents.
The joined-view updates check affected rows and committed values,
including duplicate partner matches. The spatial probes check
containment over 10,000 points and distance over the antimeridian.
The CJK fixture checks Boolean phrase matches over 10,000 documents.

Cells show median latency in milliseconds.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.155 | 1.441 | 1.050 | 2.883 |
| Correlated equality and range count | 4.496 | 1.730 | 1.171 | 7.393 |
| Nullable fixture, `COUNT(*)` | 3.104 | 1.660 | 1.178 | 7.272 |
| Nullable fixture, `COUNT(c.bucket)` | 3.560 | 2.406 | 1.454 | 11.681 |
| Nullable fixture, filtered `COUNT(c.bucket)` | 7.003 | 2.454 | 1.476 | 14.970 |
| Expression fixture, `COUNT(*)` | 3.175 | 1.661 | 1.182 | 7.349 |
| `COUNT(CASE … END)` | 20.554 | 2.428 | 1.483 | 12.632 |
| `COUNT(DISTINCT c.bucket)` | 18.161 | 2.440 | 1.490 | 11.789 |
| `COUNT(DISTINCT CASE … END)` | 20.526 | 2.460 | 1.509 | 13.126 |
| Aggregate fixture, `COUNT(*)` | 3.298 | 1.657 | 1.169 | 7.189 |
| `SUM(CASE … END)` | 20.835 | 2.501 | 1.486 | 13.126 |
| `AVG(c.bucket)` | 18.682 | 2.435 | 1.473 | 11.717 |
| `MIN(c.bucket+1)` | 21.252 | 2.449 | 1.459 | 12.269 |
| `MAX(DISTINCT c.bucket)` | 22.744 | 2.625 | 1.456 | 12.210 |
| Nested joined-view update | 1.053 | 0.133 | 0.119 | Rejected query |
| Joined-view update, 501 partner rows | 1.122 | 0.137 | 0.131 | Rejected query |
| Joined-view update, 5,001 partner rows | 1.152 | 0.135 | 0.134 | Rejected query |
| Indexed planar `MBRContains` | 0.213 | 0.126 | Boundary mismatch | Rejected query |
| Geographic antimeridian distance | 0.134 | 0.070 | Rejected query | Rejected query |
| Japanese ngram phrase | 0.711 | 0.456 | Rejected index DDL | Rejected index DDL |
| Chinese ngram phrase | 0.738 | 0.464 | Rejected index DDL | Rejected index DDL |
| Korean ngram phrase | 0.493 | 0.437 | Rejected index DDL | Rejected index DDL |

Fsdb and MySQL passed every feature check. Fsdb used 1.5× MySQL's
median time for the equality count and 2.6× for the range count.
The conditional and distinct counts took 7.4–8.5× as long, and the
exact numeric aggregates took 7.7–8.7× as long. Joined-view updates
took 7.9–8.5× as long. The spatial queries took 1.7–1.9× as long, and
CJK phrases took 1.1–1.6× as long.

Fsdb, MySQL, and MariaDB used range access on `ix_g` for the planar
fixture. MariaDB included eight additional boundary points. It
rejected the tested geographic constructor and the ngram index DDL.
Dolt rejected the tested joined-view updates, spatial queries, and
ngram index DDL.

The [prior native comparison](3de535d8-peer-comparison.md) put Fsdb at
24% of MySQL's 16-client point-read throughput. The current point-read
gap is broadly consistent with that run. The shared host and short
sampling windows limit comparisons of small differences between runs.

## Other peers

The [previous peer comparison](2026-11-25-peer-comparison.md) records
Percona Server 8.4.11-12 in matched ARM64 Linux containers and TiDB 8.5.8
in a one-host TiDB/TiKV/PD stack. At 16 clients, Percona and MySQL both
reached about 44.7k reads/s in the container setup, where Fsdb reached
11.9k. The local TiDB stack reached 24.3k reads/s at 1.09 ms p99.
Those figures use the respective environments and an earlier Fsdb
revision. [Percona Server documentation](https://docs.percona.com/percona-server/8.4/).
[TiDB MySQL compatibility documentation](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Reproduction

Run the existing harnesses against disposable servers with the settings
above. Use Fsdb, MySQL, MariaDB, and Dolt in pass 1 and reverse the order
in pass 2. Reverse the command order for pass 2.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" nullable-count-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" expression-count-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" exact-aggregate-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

The per-pass CSVs contain [reads](2da21f23-peer-reads.csv),
[writes](2da21f23-peer-writes.csv), and [feature latencies](2da21f23-peer-features.csv).
In the feature CSV, the three `count-rows` controls follow fixture order.
