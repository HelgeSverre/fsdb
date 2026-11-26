# Native durable peer benchmark at `baf72b20`

Fsdb `baf72b20`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7 ran
one at a time on an Apple M2 Max with 12 cores, 32 GiB of memory,
macOS 15.6, and about 12 GiB of swap in use. Fsdb ran in Release on
.NET 10.0.12 with WAL and Server GC. MySQL and MariaDB used 256 MiB
InnoDB buffer pools, commit-time flushing, and disabled binary logging.
Dolt used its default journal and ordinary SQL commits. MySqlConnector
2.6.2 used IPv4 loopback with TLS and pooling disabled. These measurements
describe warm fixtures on a shared host.

The two passes reverse engine and workload order. MySQL supplies the
expected SQL results for every feature timing.

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
| 1 | Primary-key reads | 6,708 / 0.29 | 28,100 / 0.08 | 32,000 / 0.06 | 15,908 / 0.14 |
| 1 | Disjoint updates | 5,415 / 0.47 | 12,038 / 0.46 | 14,530 / 0.27 | 183 / 10.29 |
| 1 | Hot-row updates | 6,542 / 0.24 | 11,879 / 0.27 | 15,551 / 0.22 | 180 / 10.64 |
| 16 | Primary-key reads | 28,242 / 9.84 | 110,696 / 0.27 | 112,039 / 0.26 | 71,493 / 0.80 |
| 16 | Disjoint updates | 19,507 / 10.12 | 43,327 / 1.69 | 55,417 / 2.00 | 170 / 114.20 |
| 16 | Hot-row updates | 7,272 / 11.96 | 17,596 / 7.00 | 24,818 / 4.43 | Counter mismatch |

At 16 clients, Fsdb delivered 26% of MySQL's point-read throughput,
45% of its disjoint update throughput, and 41% of its hot-row update
throughput. Fsdb's point-read rate was 25.8–30.7k/s across passes,
versus 109.7–111.7k/s for MySQL. The average point-read p99 was 36×
MySQL's. Doubling Fsdb's mean point-read rate would reach 56.5k/s,
or 51% of MySQL, 50% of MariaDB, and 79% of Dolt.

The [earlier WAL control](ddbd4602-peer-comparison.md#wal-attachment-control)
measured similar read throughput in memory and with WAL attached
when both used Server GC. The separate
[GC control](ac7d4a67-peer-comparison.md#fsdb-garbage-collection-control)
measured a throughput benefit with Server GC. The current baseline
already uses it.

Dolt acknowledged 1,287 and 1,296 timed hot-row updates in the two
passes. Its stored counter advanced by 87 and 89. The other Dolt
write lanes and every Fsdb, MySQL, and MariaDB write lane reconciled.

## Verified SQL feature latency

The correlated fixtures use 500 parents and 10,000 children. The nested
joined-view updates check the affected row count and the stored value.
The spatial index fixture uses 10,000 points. The WGS 84 edge queries each
check four predicates. The CJK queries check the returned ID sum over
10,000 documents indexed with the `ngram` parser and `utf8mb4_bin`.

Cells average the two pass medians in milliseconds, with 30 to 150
timed calls per pass after one preflight and four warmups. SQL rejections
and result mismatches have no timing.

| Workload | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.155 | 1.449 | 1.051 | 3.044 |
| Correlated equality and range count | 4.218 | 1.713 | 1.199 | 7.343 |
| Nested joined-view update | 1.094 | 0.125 | 0.114 | Rejected |
| Indexed planar `MBRContains` | 0.220 | 0.113 | Result mismatch | Rejected |
| Geographic antimeridian distance | 0.144 | 0.061 | Rejected | Rejected |
| Japanese ngram phrase | 0.683 | 0.458 | Parser DDL rejected | Parser DDL rejected |
| Chinese ngram phrase | 0.738 | 0.458 | Parser DDL rejected | Parser DDL rejected |
| Korean ngram phrase | 0.601 | 0.435 | Parser DDL rejected | Parser DDL rejected |
| Unfiltered nested derived count | 3.229 | 1.826 | 1.270 | 12.583 |
| Derived column-to-column equality count | 4.049 | 2.564 | 1.545 | 13.261 |
| Nested derived disjunction sum | 4.938 | 2.612 | 1.613 | 13.091 |
| CTE `NOT IN` count | 5.087 | 2.523 | 1.505 | 12.362 |
| Correlated outer comparison and null-test sum | 6.500 | 2.457 | 1.560 | 13.203 |
| Joined-view update, 501 partner rows | 1.117 | 0.128 | 0.143 | Rejected |
| Joined-view update, 5,001 partner rows | 1.138 | 0.137 | 0.151 | Rejected |
| Geographic mixed turns, forward | 2.011 | 0.142 | Result mismatch | Rejected |
| Geographic mixed turns, reverse | 1.946 | 0.138 | Result mismatch | Rejected |
| Geographic near-hole touch | 1.942 | 0.130 | Result mismatch | Rejected |
| Geographic near-hole crossing | 2.495 | 0.146 | Result mismatch | Rejected |
| Japanese ngram wildcard | 0.519 | 0.338 | Parser DDL rejected | Parser DDL rejected |
| Japanese ngram exclusion | 0.726 | 0.471 | Parser DDL rejected | Parser DDL rejected |

Fsdb matched all 21 result checks in both passes. The four newly
optimized correlated filters took 1.6–2.6× MySQL's median latency.
An [isolated before-and-after comparison](2026-11-26-correlated-range-residuals.md)
measured 33–41× gains for those four filters. MariaDB led the
correlated fixtures and Fsdb beat Dolt on all seven.

The joined-view updates took 8.3–8.8× MySQL's median latency. The
Fsdb prefix cases stayed near 1.1 ms as the partner table grew
tenfold. The benchmark exercises one autocommit client.

Fsdb, MySQL, and MariaDB used range access on the planar spatial
index. MariaDB returned nine points where MySQL and Fsdb returned
one. Fsdb took 1.9× MySQL's median time on that query and 14–17×
on the geographic edge cases. The CJK probes took 1.4–1.6×
MySQL's median time. MariaDB and Dolt rejected the tested
`WITH PARSER ngram` index definition.

## Other measured configurations

A [prior matched ARM64 container comparison](2026-11-25-peer-comparison.md#percona-in-matched-arm64-containers)
measured 44.7k point reads/s for MySQL, 44.8k for Percona Server
8.4.11-12, and 11.9k for Fsdb with 16 clients. All three passed
that run's ten feature checks.

A [prior local TiDB comparison](eeced6b1-peer-refresh.md#other-topologies)
measured 23.5k point reads/s with 1.10 ms p99, 10.6k disjoint updates/s,
and 2.5k hot-row updates/s at 16 clients. That setup used TiDB 8.5.8,
one TiKV, one PD, and one replica. The tested spatial and joined-view
operations were unavailable. The self-managed server rejected the
tested full-text searches after accepting the index DDL.
[TiDB compatibility reference](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Reproduction

Start one server at a time on a free IPv4 loopback port with a disposable
data directory. Pass 1 runs Fsdb, MySQL, MariaDB, then Dolt. Pass 2 reverses
the engine and command orders. The scripts reverse their workloads in
pass 2 and create and drop their own fixtures.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" correlated-residual-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Raw per-pass CSVs cover [point reads](baf72b20-peer-refresh-reads.csv),
[durable updates](baf72b20-peer-refresh-writes.csv), and
[SQL features](baf72b20-peer-refresh-features.csv).
