# Durable native database peer benchmark

This run spans 2026-11-25 and 2026-11-26 on an Apple M2 Max with 12
cores, 32 GiB of RAM, macOS 15.6, and about 12 GiB of swap in use. The
native comparison uses Fsdb `eeced6b1`, MySQL 8.4.11, MariaDB 11.8.10, and
Dolt 2.7.7 on IPv4 loopback. One engine runs at a time with a fresh
data directory. Pass 2 reverses the engine and applicable workload
orders. MySQL defines the expected SQL results.

Fsdb runs in Release on .NET 10.0.12 with Server GC and WAL. MySQL and
MariaDB use a 256 MiB InnoDB buffer pool, commit flushing set to 1,
and binary logging disabled. Dolt uses its default journal with
Git-style commits disabled. MySqlConnector 2.6.2 uses text commands
with TLS and connection pooling disabled. The host is shared. Small
differences between runs and the absolute tail latencies should be
read in that context.

## Point reads and durable writes

Each lane uses 10,000 rows at READ COMMITTED and one outstanding
request per client. Each timed window runs for six seconds after
1.5 seconds of warmup. Point reads check the returned ID, name, and
age. Autocommit writes increment an indexed value. Both warmup and
timed write counters must match server acknowledgements before the
harness publishes timing. Cells show ops/s and p99 milliseconds,
averaged across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,410 / 0.23 | 27,234 / 0.05 | 29,580 / 0.05 | 16,086 / 0.13 |
| 1 | Disjoint updates | 7,151 / 0.21 | 13,821 / 0.14 | 18,094 / 0.14 | 158 / 11.12 |
| 1 | Hot-row updates | 7,355 / 0.28 | 13,508 / 0.16 | 18,131 / 0.14 | 153 / 11.10 |
| 16 | Primary-key reads | 29,553 / 9.26 | 117,991 / 0.20 | 117,501 / 0.22 | 79,004 / 0.65 |
| 16 | Disjoint updates | 20,379 / 9.37 | 55,666 / 0.51 | 65,685 / 0.62 | 155 / 129.81 |
| 16 | Hot-row updates | 7,540 / 11.21 | 23,849 / 3.16 | 42,127 / 0.62 | Counter mismatch |

At 16 clients, Fsdb reached 25% of MySQL's point-read throughput,
37% of its disjoint-update throughput, and 32% of its hot-row-update
throughput. Point reads ranged from 28.3k to 30.8k/s across the two Fsdb
passes and 117.4k to 118.6k/s across the MySQL passes. At twice its
measured mean, Fsdb would reach 59.1k/s, about 50% of MySQL and
MariaDB and 75% of Dolt. Fsdb's 16-client point-read p99 was 45×
MySQL's. The 25% throughput share is consistent with the prior
native run. [Prior peer benchmark](2da21f23-peer-comparison.md).

Dolt acknowledged 1,182 and 1,192 timed hot-row updates with
16 clients. Its stored counter advanced by 81 in both passes.
The other Dolt write lanes reconciled their counters.

## SQL features

Each lane runs a preflight, four warmups, and 15 to 150 timed
operations. Correlated queries use 500 parents and 10,000 children.
Joined-view checks validate affected rows, the committed values, and
single target updates through duplicate join matches. The planar
spatial and CJK fixtures use 10,000 entries each. The geographic
latency fixture measures distance across the antimeridian. Cells
show the mean of the two median timings in milliseconds.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.104 | 1.413 | 1.056 | 2.753 |
| Correlated equality and range count | 4.252 | 1.678 | 1.166 | 7.051 |
| Nullable fixture, `COUNT(*)` | 3.069 | 1.645 | 1.159 | 7.113 |
| `COUNT(c.bucket)` | 3.362 | 2.378 | 1.422 | 10.903 |
| `COUNT(c.bucket)` with a residual filter | 7.132 | 2.390 | 1.434 | 14.231 |
| Expression fixture, `COUNT(*)` | 3.055 | 1.632 | 1.169 | 7.073 |
| `COUNT(CASE … END)` | 5.495 | 2.428 | 1.457 | 12.182 |
| `COUNT(DISTINCT c.bucket)` | 4.287 | 2.444 | 1.469 | 11.291 |
| `COUNT(DISTINCT CASE … END)` | 5.651 | 2.507 | 1.490 | 12.359 |
| Aggregate fixture, `COUNT(*)` | 3.130 | 1.638 | 1.164 | 7.196 |
| `SUM(CASE … END)` | 5.839 | 2.438 | 1.475 | 11.997 |
| `AVG(c.bucket)` | 5.714 | 2.400 | 1.464 | 11.053 |
| `AVG(c.bucket)` with a non-NULL residual | 8.647 | 2.396 | 1.461 | 10.271 |
| `MIN(c.bucket+1)` | 5.359 | 2.408 | 1.455 | 11.590 |
| `MAX(DISTINCT c.bucket)` | 5.390 | 2.373 | 1.453 | 11.675 |
| Nested joined-view update | 1.016 | 0.116 | 0.133 | Rejected |
| Joined-view update, 501 partner rows | 1.107 | 0.133 | 0.142 | Rejected |
| Joined-view update, 5,001 partner rows | 1.127 | 0.124 | 0.141 | Rejected |
| Indexed planar `MBRContains` | 0.225 | 0.116 | Boundary mismatch | Rejected |
| Geographic antimeridian distance | 0.125 | 0.071 | Rejected | Rejected |
| Japanese ngram phrase | 0.655 | 0.443 | Rejected | Rejected |
| Chinese ngram phrase | 0.693 | 0.456 | Rejected | Rejected |
| Korean ngram phrase | 0.549 | 0.432 | Rejected | Rejected |

Fsdb and MySQL passed every checked feature in both passes. Relative
to the earlier native run at `2da21f23`, the conditional and distinct
counts and four exact aggregates with matching fixtures ran
3.3–4.2× faster. These lanes now take 1.8–2.4× MySQL's median time.
The additional `AVG` with a residual filter took 3.6× MySQL's time.
Simple correlated equality and range counts took 1.5× and 2.5× as long.
The short samples and shared host limit the precision of cross-run
speedups. [Direct-fold benchmark and allocation checks](2026-11-25-correlated-integer-range-count.md#direct-folds-over-correlated-ranges).

The joined-view updates took 8.3–9.1× MySQL's median. Those timings measure
one autocommit connection. The separate joined-view lock probe records
different behavior under concurrent modification of a matching outer
join row. [Joined-view lock results](2026-11-25-bound-joined-view-update.md#outer-join-operand-locks).

Fsdb and MySQL used range access on `ix_g` and returned one interior
point. MariaDB used the same index and included eight boundary points.
Fsdb's two spatial probes took 1.8–1.9× MySQL's time. The Japanese,
Chinese, and Korean phrases took 1.3–1.5× as long. MariaDB and Dolt
rejected the tested geographic expression and ngram index DDL.
Dolt also rejected the tested joined-view updates and planar query.

## Other topologies

TiDB 8.5.8 ran separately in two fresh local TiUP Playground clusters
with one TiDB, one TiKV, one PD, one replica, a 256 MB TiKV block cache,
and monitoring and TiFlash disabled. READ COMMITTED used pessimistic
autocommit, and one-phase commit and async commit were enabled.
These figures describe a single-host cluster.
[TiUP Playground documentation](https://docs.pingcap.com/tidb/stable/tiup-playground/).
[TiDB pessimistic transaction configuration](https://docs.pingcap.com/tidb/stable/tidb-configuration-file/#pessimistic-txn).

| Workload, ops/s and p99 ms | TiDB, 1 client | TiDB, 16 clients |
| --- | ---: | ---: |
| Primary-key reads | 4,700 / 0.31 | 23,540 / 1.10 |
| Disjoint updates | 2,519 / 0.61 | 10,624 / 2.55 |
| Hot-row updates | 2,530 / 0.60 | 2,528 / 10.28 |

At 16 clients, Fsdb delivered 26% more point reads
per second and had 8.4× TiDB's point-read p99. TiDB reconciled
all update counters. TiDB's correlated equality and range queries took
2.594 and 61.286 ms. On the nullable-bucket fixture,
`COUNT(DISTINCT c.bucket)` took 1.415 ms while
`COUNT(DISTINCT CASE … END)` took 134.103 ms.
The tested conditional counts and `SUM(CASE … END)` took about 135 ms.
TiDB reported the nested joined view as read-only and rejected the
prefix-join updates and both spatial probes. The ngram index DDL
succeeded and all three `MATCH ... AGAINST` queries were rejected.
[TiDB MySQL compatibility documentation](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

The earlier matched ARM64 container run put MySQL and Percona Server
8.4.11-12 at about 44.7k point reads/s with 16 clients and Fsdb at
11.9k/s. All three passed the ten feature checks in that run. Docker
did not respond to its health check for this refresh, so Percona's
figures come from the earlier run. [Matched container results](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers).

## Reproduction

Start disposable servers with the settings above. Run the commands
in this order on pass 1 and in reverse order on pass 2. The first
pass runs Fsdb, MySQL, MariaDB, and Dolt in that order.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" nullable-count-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" expression-count-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" exact-aggregate-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

| Setup | Reads | Writes | SQL features |
| --- | --- | --- | --- |
| Native processes | [CSV](eeced6b1-peer-refresh-reads.csv) | [CSV](eeced6b1-peer-refresh-writes.csv) | [CSV](eeced6b1-peer-refresh-features.csv) |
| Local TiDB stack | [CSV](eeced6b1-peer-refresh-tidb-reads.csv) | [CSV](eeced6b1-peer-refresh-tidb-writes.csv) | [CSV](eeced6b1-peer-refresh-tidb-features.csv) |

The feature CSVs identify each fixture in the `suite` column. A rejected
query or a result mismatch has no latency. The historical Percona run
has its own [raw results](2026-11-25-peer-comparison.md#reproduction).
