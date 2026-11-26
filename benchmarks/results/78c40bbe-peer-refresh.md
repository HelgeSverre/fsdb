# Native durable peer benchmark at `78c40bbe`

Fsdb `78c40bbe`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7 ran
one at a time on an Apple M2 Max with 12 cores, 32 GiB of memory,
and macOS 15.6. The shared host had about 13 GiB of swap in use.
Fsdb ran in Release on .NET 10.0.12 with WAL and Server GC. MySQL and
MariaDB used 256 MiB InnoDB buffer pools, commit-time flushing, and disabled
binary logging. Dolt used its default journal and ordinary SQL commits.
MySqlConnector 2.6.2 used IPv4 loopback with TLS and pooling disabled.

Two passes reverse engine and workload order. Fixtures use MySQL
8.4.11 as their result reference. The benchmark describes warm data
and exposes throughput and latency on this host.

## Point reads and durable updates

Each workload uses 10,000 rows, READ COMMITTED, and one outstanding
request per connection. Random primary-key reads check the returned
ID, name, and age. Autocommit updates increment an indexed column on
either disjoint rows or one shared row. The measured window lasts six
seconds after a 1.5-second warmup. Each write window reconciles its
successful acknowledgements against the committed counter.

Cells show operations per second followed by p99 latency in milliseconds,
averaged across the two passes.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 7,053 / 0.36 | 29,476 / 0.08 | 31,807 / 0.06 | 16,536 / 0.13 |
| 1 | Disjoint updates | 6,253 / 0.23 | 11,735 / 0.22 | 17,758 / 0.14 | 175 / 10.63 |
| 1 | Hot-row updates | 6,216 / 0.30 | 11,849 / 0.22 | 17,701 / 0.14 | 177 / 9.97 |
| 16 | Primary-key reads | 28,380 / 10.88 | 106,675 / 0.36 | 112,536 / 0.27 | 67,206 / 0.96 |
| 16 | Disjoint updates | 18,402 / 10.82 | 38,031 / 2.34 | 61,628 / 0.80 | 173 / 106.84 |
| 16 | Hot-row updates | 6,473 / 16.32 | 23,519 / 3.24 | 41,492 / 0.65 | Counter mismatch |

At 16 clients, Fsdb reached 27% of MySQL's point-read throughput,
48% of its disjoint update throughput, and 28% of its hot-row update
throughput. Point-read p99 averaged 10.88 ms against 0.36 ms for MySQL,
about 30× higher. Doubling Fsdb's point-read throughput would reach
56.8k/s, or 53% of MySQL, 50% of MariaDB, and 84% of Dolt in this run.

Fsdb's 16-client result was 26.97–29.79k point reads/s across the two
passes. The previous run measured 25.8–30.7k/s for Fsdb. Attributing
a throughput change to the intervening commits would require an isolated
before-and-after run. An earlier control measured similar point-read
throughput with and without WAL when both used Server GC.
[Previous native run](baf72b20-peer-refresh.md),
[WAL attachment control](ddbd4602-peer-comparison.md#wal-attachment-control).

The Fsdb, MySQL, and MariaDB write counts reconciled in every window.
Dolt acknowledged 1,347 and 552 timed hot-row updates with 16 clients.
Its stored counter advanced by 92 and 37. Dolt reconciled the other
write lanes.

## Verified SQL feature latency

The correlated fixtures have 500 parents and 10,000 children.
The joined-view probes check affected rows and the stored value.
The spatial index fixture has 10,000 points and the edge queries each
check four geographic predicates. The overlay controls compare
geometry type and ordered WGS 84 coordinates to within 10⁻¹⁰ degrees.
The CJK queries check the returned ID sum over 10,000 documents
indexed with the ngram parser and `utf8mb4_bin`.

Cells average the two pass medians in milliseconds. Each pass measures
30 to 150 calls after one preflight and four warmup calls. Rejections
and mismatched results have no timing.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.051 | 1.431 | 1.073 | 2.911 |
| Correlated equality and range count | 4.091 | 1.743 | 1.188 | 7.123 |
| Nested joined-view update | 1.040 | 0.149 | 0.124 | Rejected |
| Indexed planar `MBRContains` | 0.228 | 0.122 | Result mismatch | Rejected |
| Geographic antimeridian distance | 0.141 | 0.087 | Rejected | Rejected |
| Japanese ngram phrase | 0.640 | 0.469 | Parser DDL rejected | Parser DDL rejected |
| Chinese ngram phrase | 0.686 | 0.503 | Parser DDL rejected | Parser DDL rejected |
| Korean ngram phrase | 0.605 | 0.479 | Parser DDL rejected | Parser DDL rejected |
| Unfiltered nested derived count | 3.145 | 1.775 | 1.320 | 11.940 |
| Derived column-to-column equality count | 4.107 | 2.487 | 1.640 | 12.727 |
| Nested derived disjunction sum | 4.970 | 2.593 | 1.656 | 12.380 |
| CTE `NOT IN` count | 4.803 | 2.576 | 1.590 | 11.690 |
| Correlated outer comparison and null-test sum | 6.152 | 2.606 | 1.653 | 12.489 |
| Joined-view update, 501 partner rows | 1.126 | 0.133 | 0.126 | Rejected |
| Joined-view update, 5,001 partner rows | 1.143 | 0.139 | 0.129 | Rejected |
| Geographic mixed turns, forward | 2.026 | 0.170 | Result mismatch | Rejected |
| Geographic mixed turns, reverse | 1.947 | 0.147 | Result mismatch | Rejected |
| Geographic near-hole touch | 1.889 | 0.134 | Result mismatch | Rejected |
| Geographic near-hole crossing | 2.469 | 0.152 | Result mismatch | Rejected |
| Japanese ngram wildcard | 0.445 | 0.347 | Parser DDL rejected | Parser DDL rejected |
| Japanese ngram exclusion | 0.710 | 0.485 | Parser DDL rejected | Parser DDL rejected |
| Geographic intersection near a corner | 0.366 | 0.098 | Result mismatch | Rejected |
| Geographic intersection at a polar hole | Result mismatch | 0.100 | Result mismatch | Rejected |

Fsdb matched 22 of the 23 feature checks in both passes. The correlated
queries took 1.4–2.4× MySQL's median time. MariaDB led all seven
correlated cases, and Fsdb beat Dolt in all seven. The four recently
optimized filters retained their verified results. An
[earlier isolated comparison](2026-11-26-correlated-range-residuals.md)
measured 33–41× gains for those filters against the older Fsdb path.

The joined-view updates took 7.0–8.5× MySQL's median time. Fsdb's
prefix-query latency stayed near 1.1 ms as the partner table grew
tenfold. These timing probes use one autocommit client. A separate
[MySQL schedule comparison](../../torture/findings/2026-11-26-joined-view-primary-key-gaps.md)
reports 75 matching checks for missing primary keys, lock waits, and
cross-schema foreign-key cascades.

Fsdb, MySQL, and MariaDB used range access for the planar spatial
fixture. Fsdb and MySQL returned one point, and MariaDB returned nine.
Fsdb took 1.9× MySQL's median time on that query and 12–16× on the
four geographic relation probes. The verified corner overlay took
3.7× MySQL's median time. In the polar-hole case, MySQL returned
`POINT(86 1)`, Fsdb returned `GEOMETRYCOLLECTION EMPTY`, and MariaDB
returned a multipoint. Dolt rejected the geographic probes. The
[broader geographic overlay oracle](../../torture/findings/2026-11-06-geographic-residual-vertex-audit.md#november-26-overlay-interval-classification)
recorded 26 differences in 7,168 results at the measured revision,
down from 367 before the directed-turn change.

The five CJK searches took 1.3–1.5× MySQL's median time. MariaDB and
Dolt rejected the tested `WITH PARSER ngram` index definition.

## Local TiDB cluster

TiDB 8.5.8 ran in two fresh single-host clusters with one native TiDB,
one TiKV, one PD, and one replica. TiKV used a 256 MB block cache.
TiDB used pessimistic autocommit, one-phase commit, and async commit.
The disposable TiKV store used a 1 GB capacity with disk reservations
disabled to fit the available storage. The measurements describe this
local test topology. [TiDB cluster documentation](https://docs.pingcap.com/tidb/stable/tiup-playground/).
[TiKV configuration](https://docs.pingcap.com/tidb/dev/tikv-configuration-file/).

Cells show operations per second followed by p99 latency in milliseconds.

| Clients | Workload | TiDB |
| ---: | --- | ---: |
| 1 | Primary-key reads | 4,264 / 0.46 |
| 1 | Disjoint updates | 1,639 / 2.67 |
| 1 | Hot-row updates | 1,465 / 10.99 |
| 16 | Primary-key reads | 21,093 / 1.49 |
| 16 | Disjoint updates | 7,123 / 21.97 |
| 16 | Hot-row updates | 1,642 / 32.09 |

TiDB reconciled all committed update counters. Fsdb handled 35% more
16-client point reads per second. TiDB's point-read p99 was 7.3× lower.
The seven correlated results matched MySQL in both passes and showed
a wide latency spread.

| Workload | TiDB p50 (ms) |
| --- | ---: |
| Correlated equality count | 1.955 |
| Correlated equality and range count | 57.136 |
| Unfiltered nested derived count | 75.586 |
| Derived column-to-column equality count | 167.022 |
| Nested derived disjunction sum | 1.777 |
| CTE `NOT IN` count | 174.740 |
| Correlated outer comparison and null-test sum | 2.013 |

TiDB led the nested disjunction and outer comparison cases across the
engines measured here. The tested nested joined view was read-only.
TiDB rejected the prefix updates, spatial operations, and five CJK
searches. It accepted the ngram index definition before rejecting
the searches. [TiDB MySQL compatibility](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Percona

The [previous matched ARM64 container run](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers)
measured 44.7k point reads/s for MySQL 8.4.11, 44.8k for Percona
Server 8.4.11-12, and 11.9k for Fsdb with 16 clients. All three
passed that run's ten feature checks. The three figures come
from the same earlier container configuration.
[Percona Server documentation](https://docs.percona.com/percona-server/8.4/).

## Reproduction

Start disposable servers one at a time on a free IPv4 loopback port.
Pass 1 runs Fsdb, MySQL, MariaDB, then Dolt. Pass 2 reverses the
engine and command orders. The scripts reverse their workloads in pass 2
and create and drop their own fixtures. The first Fsdb overlay pass was
repeated separately with the same settings after correcting the expected
WKT vertex order.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" correlated-residual-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" overlay-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Raw per-pass CSVs cover [point reads](78c40bbe-peer-refresh-reads.csv),
[durable updates](78c40bbe-peer-refresh-writes.csv), and
[SQL features](78c40bbe-peer-refresh-features.csv).

The same commands ran against two fresh TiDB clusters with pass 2
reversing the command order. TiDB raw results cover
[point reads](78c40bbe-peer-refresh-tidb-reads.csv),
[durable updates](78c40bbe-peer-refresh-tidb-writes.csv), and
[SQL features](78c40bbe-peer-refresh-tidb-features.csv).
