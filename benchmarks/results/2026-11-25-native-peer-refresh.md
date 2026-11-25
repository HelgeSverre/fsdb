# Native MySQL-protocol peer benchmark

The 2026-11-25 rerun compares Fsdb `cdc1a2d0` with MySQL 8.4.11,
MariaDB 11.8.10, and Dolt 2.7.7. Each server ran natively over loopback TCP
on the same Apple M2 Max Mac (12 cores, 32 GiB RAM, macOS 15.6), with one
benchmark server active at a time. Fsdb used Release .NET 10.0.12, WAL,
and `DOTNET_gcServer=1`. MySQL and MariaDB used InnoDB with a 256 MiB
buffer pool, commit flushing set to 1, and binary logging disabled.
Dolt used default journal settings with `dolt_transaction_commit=OFF`.
MySQL 8.4 supplies the expected SQL results. The host was shared and
had about 13 GiB of swap in use, so small differences are directional.

## Feature latency

The [same feature fixture](2026-11-25-native-feature-peers.md) uses
500 parent rows, 10,000 children, 10,000 indexed spatial points, and
10,000 articles. The client checks every timed result or affected-row
count. Each pass samples 60 to 150 executions per lane after a preflight
and four warmups. The second pass reverses the engine and workload order.
The table averages the two pass medians.

| Workload, p50 ms | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 4.949 | 1.444 | 1.079 | 2.808 |
| Correlated equality and range count | 8.296 | 1.718 | 1.206 | 7.267 |
| Nested joined-view update | 3.651 | 0.150 | 0.123 | Rejected update |
| Indexed planar `MBRContains` | 0.298 | 0.118 | Different boundary result | Missing function |
| Geographic antimeridian distance | 0.175 | 0.082 | Rejected constructor | Unsupported SRID |
| Japanese phrase | 0.951 | 0.453 | Rejected parser | Rejected parser syntax |
| Chinese phrase | 1.059 | 0.473 | Rejected parser | Rejected parser syntax |
| Korean phrase | 0.887 | 0.439 | Rejected parser | Rejected parser syntax |

Fsdb matched MySQL on all eight checks in both passes. Both engines used
`range` access on `ix_g`. MariaDB also used the spatial index and included
eight boundary points that MySQL excluded. Its stock build rejected the
tested `ngram` declaration.

The correlated range is now 4.8× MySQL latency. The controlled
[before-and-after benchmark](2026-11-25-correlated-integer-range-count.md)
measured the index-count fix at 399 ms before and 12.1 ms after.
The joined-view update has the largest remaining latency ratio in this
fixture at 24×. The spatial and CJK queries are about 2.0–2.5×.

[Per-pass feature results](2026-11-25-native-peer-refresh-features.csv)

## Durable writes under contention

Each of 1 or 16 clients increments an indexed column by primary key in
autocommit. In the disjoint case each worker chooses a row from its own
set of 500. In the hot-row case every worker updates row 1. Each lane
uses 1.5 seconds of warmup and 6 seconds of measurement against a
10,000-row table. Sessions use READ COMMITTED. The client checks one
affected row per successful update and takes the table-wide counter sum
before and after each phase. Only lanes whose stored counter increments
match the successful acknowledgements in both phases receive a timing.
Retries count lock timeouts and deadlocks. Two passes reverse engine and
lane order. Throughput and p99 are averages of per-pass measurements.

| Workers | Allocation of rows | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Disjoint | 6,296 / 0.25 | 12,804 / 0.22 | 18,025 / 0.13 | 165 / 10.90 |
| 1 | One hot row | 6,781 / 0.23 | 13,326 / 0.25 | 17,165 / 0.14 | 177 / 9.62 |
| 16 | Disjoint | 19,104 / 9.86 | 42,273 / 1.88 | 63,537 / 0.66 | 165 / 164.43 |
| 16 | One hot row | 7,467 / 11.62 | 21,996 / 3.11 | 25,083 / 5.55 | Counter mismatch |

Cells show operations per second followed by p99 in milliseconds.
Fsdb reached 45% of MySQL's 16-worker disjoint throughput and 34% of
its hot-row throughput. The hot-row share of disjoint throughput was
39% for Fsdb and 52% for MySQL. Across the two passes, MariaDB's 16-client
hot-row rate varied from 16,783 to 33,383 ops/s. Fsdb's 16-client disjoint
throughput ranged from 18,075 to 20,133 ops/s in the recorded passes and
was 9,897 ops/s in an exploratory run on the shared host.

Dolt passed the one-worker and disjoint-row checks. With 16 clients on
one row it acknowledged 1,241 and 1,301 timed updates, while the stored
counter increased by 85 and 88, respectively. Warmup showed the same
discrepancy. The table omits timings for those two trials.

[Per-pass write results](2026-11-25-native-peer-refresh-writes.csv)

The [earlier OLTP run](2026-11-25-native-oltp-peers.md) measured 21,055
point reads/s for Fsdb and 107,728 for MySQL at 16 workers. Another 2×
gain over that Server GC baseline would reach about 39% of MySQL's rate
at the measured MySQL throughput.

## Reproduction

Start each engine on a free loopback port with a disposable store and
an empty-password root account. Keep one benchmark server active at a
time. Use engine order Fsdb, MySQL, MariaDB, Dolt for pass 1 and the
reverse for pass 2. Substitute the engine, port, and pass below.

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3357 1
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3357 1
```
