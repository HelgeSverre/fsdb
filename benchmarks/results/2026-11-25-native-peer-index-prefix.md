# Native peer benchmarks after the joined-update index prefix change

The 2026-11-25 run compares Fsdb `c894409b` with MySQL 8.4.11,
MariaDB 11.8.10, and Dolt 2.7.7. The composite-prefix update was also
measured on Fsdb `76ad3428`, its immediate parent. MySQL 8.4 supplies
the expected SQL results.

Each server ran natively over loopback TCP with one benchmark server
active at a time. The shared Apple M2 Max host had 12 cores, 32 GiB RAM,
macOS 15.6, and about 12.4 GiB of swap in use. Fsdb ran in Release on
.NET 10.0.12 with WAL enabled and `DOTNET_gcServer=1`. MySQL and
MariaDB used InnoDB, a 256 MiB buffer pool, commit flushing set to 1,
and binary logging disabled. Dolt used its default journal settings and
ordinary SQL commits. The client used MySqlConnector 2.6.2 with TLS and
connection pooling disabled. Pass 2 reversed engine and workload order.
Tables average the two pass statistics. Timing differences on this
shared host are directional.

## Joined-view index prefix

The update joins a nested, checked view to a partner with a composite
primary key. The target ID fixes the first partner key column. Exactly
two partner rows match and both supply the same increment. The client
checks two visible join rows, one affected target row for every
execution, and the final committed value. Each pass has 60 measured
updates per size after one preflight and four warmups.

| Partner rows | Fsdb before, p50 ms | Fsdb after, p50 ms | Speedup | MySQL, p50 ms | MariaDB, p50 ms | Dolt |
| ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 501 | 5.657 | 1.249 | 4.5× | 0.130 | 0.207 | Rejected update |
| 5,001 | 7.971 | 1.309 | 6.1× | 0.128 | 0.173 | Rejected update |

The larger partner increased Fsdb's median latency by 41% before the
change and 5% after. The larger fixture still took 10.3× MySQL's
median time. Fsdb's p95 dropped from 7.00 to 1.27 ms in pass 1 and
from 10.54 to 7.88 ms in pass 2. The size of the tail gain varied
across passes.

## SQL features

The [feature fixture](2026-11-25-native-feature-peers.md) uses 500
parents and 10,000 children, spatial points, and articles per relevant
lane. The harness checks every timed result or affected-row count.
Each pass measures 60 to 150 operations per lane.

| Workload, p50 ms | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 4.710 | 1.428 | 1.055 | 2.724 |
| Correlated equality and range count | 8.731 | 1.690 | 1.183 | 7.168 |
| Nested joined-view update, complete keys | 1.918 | 0.133 | 0.162 | Rejected update |
| Indexed planar `MBRContains` | 0.322 | 0.110 | Boundary mismatch | Missing function |
| Geographic antimeridian distance | 0.199 | 0.066 | Rejected constructor | Unsupported SRID |
| Japanese phrase | 0.909 | 0.442 | Rejected parser | Rejected parser syntax |
| Chinese phrase | 0.973 | 0.449 | Rejected parser | Rejected parser syntax |
| Korean phrase | 0.819 | 0.424 | Rejected parser | Rejected parser syntax |

Fsdb matched all eight MySQL result checks in both passes. Fsdb,
MySQL, and MariaDB each reported `range` access on `ix_g` for the
planar query. MySQL and Fsdb returned one interior point. MariaDB
returned that point and eight more on the boundary. The CJK fixture
uses MySQL's built-in `ngram` parser with quoted Boolean phrases.
The MariaDB and Dolt results apply to the tested builds and DDL.

## Concurrent durable writes

The write fixture has 10,000 rows and updates the indexed `age`
column by primary key in autocommit. In the disjoint case each worker
has its own 500 IDs. In the hot-row case every worker updates row 1.
Clients use READ COMMITTED, warm up for 1.5 seconds, and measure for
6 seconds per lane. The client checks affected-row counts and verifies
that the aggregate committed counter change matches acknowledgements.
Cells show operations per second followed by p99 in milliseconds.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Disjoint rows | 6,605 / 0.24 | 13,632 / 0.14 | 17,348 / 0.14 | 173 / 9.74 |
| 1 | One hot row | 6,825 / 0.22 | 13,537 / 0.14 | 17,701 / 0.14 | 169 / 10.56 |
| 16 | Disjoint rows | 20,599 / 8.61 | 56,526 / 0.50 | 65,345 / 0.64 | 170 / 114.78 |
| 16 | One hot row | 7,856 / 10.72 | 20,257 / 3.99 | 36,338 / 2.30 | Counter mismatch |

Fsdb reached 36% and 39% of MySQL's 16-client disjoint and hot-row
throughput, respectively. Dolt acknowledged 1,270 and 1,261 timed
hot-row updates, while its stored counter increased by 87 in each
pass. Warmup also showed a discrepancy. The table excludes timings
for that lane. MariaDB's 16-client hot-row throughput ranged from
29.8k to 42.8k ops/s across the two passes.

## Primary-key reads

The read fixture has 10,000 users with an integer primary key, a
name, and an indexed integer age. The client issues text queries for
random existing IDs and checks the ID, name, age, and row count on every
response. Each client has one connection and one operation outstanding.
The warmup and measurement windows match the write trials. Cells show
operations per second followed by p99 in milliseconds.

| Clients | Fsdb | MySQL | MariaDB | Dolt |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 7,243 / 0.28 | 26,695 / 0.06 | 29,302 / 0.05 | 15,683 / 0.16 |
| 16 | 28,231 / 8.47 | 117,142 / 0.23 | 119,863 / 0.21 | 72,103 / 0.78 |

At 16 clients, Fsdb delivered 24% of MySQL throughput. Its two trials
ranged from 25.2k to 31.2k ops/s. Doubling the two-pass mean would reach
about 56.5k ops/s, or 48% of MySQL at the measured rate. Fsdb's p99
was about 37× MySQL's. The earlier [point-read diagnosis](2026-11-18-point-read-diagnosis.md)
found that WAL attachment accounted for a small share of the earlier
gap and that the point-read path allocated about 87 KB per in-process
text call. The current measurements did not profile allocations.

## Reproduction

Use a free loopback port and disposable data directories. Start one
engine at a time with the settings above and an empty-password root
account. For both phases, use Fsdb, MySQL, MariaDB, Dolt in pass 1 and
reverse the order for pass 2. Interleave the Fsdb baseline before the
current Fsdb in pass 1 and after it in pass 2. Run only the
`prefix-controls` lane on the baseline.

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3357 1
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3357 1 prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3357 1
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3357 1 point-reads
```

Per-pass data includes [SQL feature latency](2026-11-25-native-peer-index-prefix-features.csv),
[durable writes](2026-11-25-native-peer-index-prefix-writes.csv), and
[point reads](2026-11-25-native-peer-index-prefix-reads.csv).
