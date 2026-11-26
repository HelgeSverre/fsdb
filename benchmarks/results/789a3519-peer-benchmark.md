# Native durable peer benchmarks at `789a3519`

Fsdb `789a3519`, MySQL 8.4.11, MariaDB 11.8.10, and Dolt 2.7.7
ran one server at a time on an Apple M2 Max with 12 cores, 32 GiB
of RAM, and macOS 15.6. Fsdb ran in Release on .NET 10.0.12 with
WAL and `DOTNET_gcServer=1`. MySQL and MariaDB used 256 MiB InnoDB
buffer pools, commit-time flushing, and binary logging disabled.
Dolt used its default journal settings and ordinary SQL commits.
MySqlConnector 2.6.2 used IPv4 loopback with TLS and pooling disabled.

Pass two reversed engine and workload order. Each server started
with a disposable data directory. The shared machine had about
14 GiB of swap in use and ran a separate PHP test process during
part of the campaign. The timings describe warm loopback workloads
on this host, and the two passes show how much the measurements varied.
MySQL 8.4.11 supplies the expected results for the SQL checks.

TiDB 8.5.8 ran afterward in two fresh single-host clusters, each with
one TiDB, one TiKV, and one Placement Driver process, one replica,
and a 256 MiB TiKV block cache. Pessimistic autocommit, one-phase commit,
and async commit were enabled. Its figures describe that local topology.
[TiDB architecture](https://docs.pingcap.com/tidb/stable/tidb-architecture/).

## Primary-key reads and durable updates

The 10,000-row fixture uses READ COMMITTED and one request outstanding
per client. Reads validate the ID, name, and age. Writes increment an
indexed integer in autocommit across disjoint row sets or one shared
row. The client checks affected-row counts and reconciles each write
window's acknowledgements against the stored counter. Each window lasts
six seconds after a 1.5-second warmup. Cells show mean throughput and
mean per-pass p99 latency in milliseconds. See the per-pass
[read](789a3519-peer-reads.csv) and [write](789a3519-peer-writes.csv) results.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt | TiDB |
| ---: | --- | ---: | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 5,834 / 0.46 | 24,713 / 0.20 | 27,577 / 0.09 | 14,629 / 0.20 | 3,187 / 1.04 |
| 1 | Disjoint updates | 4,486 / 0.68 | 12,074 / 0.23 | 15,856 / 0.18 | 182 / 9.73 | 1,898 / 1.87 |
| 1 | Hot-row updates | 5,028 / 0.54 | 11,564 / 0.27 | 15,497 / 0.21 | 186 / 10.24 | 1,714 / 4.02 |
| 16 | Primary-key reads | 21,161 / 13.22 | 71,012 / 1.63 | 97,024 / 0.65 | 62,161 / 1.09 | 14,785 / 4.57 |
| 16 | Disjoint updates | 13,538 / 14.59 | 39,273 / 2.26 | 52,313 / 1.09 | 179 / 104.58 | 8,730 / 5.65 |
| 16 | Hot-row updates | 5,358 / 16.63 | 22,864 / 3.28 | 33,581 / 1.65 | Mismatch | 2,034 / 15.03 |

With 16 clients, Fsdb delivered 30% of MySQL's point reads, 34% of
its disjoint updates, and 23% of its hot-row updates. Point-read p99
averaged 13.22 ms in Fsdb and 1.63 ms in MySQL. Doubling Fsdb's point
reads would yield about 42.3k/s, or 60% of the measured MySQL rate.
Fsdb's two 16-client point-read trials ranged from 16.8k to 25.5k/s.
An earlier diagnosis measured a much larger effect from Server GC
than from WAL attachment. This run uses Server GC throughout and
collects no allocation profile.
[Point-read diagnosis](2026-11-18-point-read-diagnosis.md).

Dolt's 16-client hot-row writes failed counter reconciliation in both
passes. The timed windows acknowledged 1,279 and 759 updates, while
the stored counter advanced by 87 and 51. The reported disjoint
Dolt runs and all Fsdb, MySQL, and MariaDB write lanes reconciled.

Fsdb delivered 43% more point reads per second than the local TiDB
cluster at 16 clients. TiDB's point-read p99 was 4.57 ms, compared with
13.22 ms for Fsdb. All TiDB write counters reconciled.

## Checked SQL feature latency

The correlated fixtures use 500 parents and 10,000 children. The joined
updates check affected rows and the final value. The prefix fixtures
have 501 and 5,001 partner rows. The spatial index and ngram fixtures
use 10,000 points and 10,000 articles. Each geographic turn query
checks four predicates. The overlay fixtures check geometry type and
ordered WGS 84 coordinates within 10⁻¹⁰ degrees. The ngram fixture
checks the returned sum of matching document IDs.

Each pass checks a preflight result and measures 30 to 150 calls after
four warmups. Cells show the mean of the two p50s in milliseconds.
A result mismatch or SQL rejection leaves the timing cell unavailable.
[Per-pass p50 and p95 results](789a3519-peer-features.csv).

| Workload | Fsdb | MySQL | MariaDB | Dolt | TiDB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.378 | 1.505 | 1.206 | 3.098 | 1.858 |
| Correlated equality and range count | 4.367 | 1.934 | 1.229 | 7.510 | 75.990 |
| Nested checked-view update | 1.170 | 0.202 | 0.121 | Rejected | Mismatch |
| Indexed planar `MBRContains` | 0.234 | 0.120 | Mismatch | Rejected | Rejected |
| Geographic antimeridian distance | 0.146 | 0.091 | Rejected | Rejected | Rejected |
| Japanese ngram phrase | 0.682 | 0.470 | Parser DDL rejected | Parser DDL rejected | Rejected |
| Chinese ngram phrase | 0.733 | 0.489 | Parser DDL rejected | Parser DDL rejected | Rejected |
| Korean ngram phrase | 0.574 | 0.461 | Parser DDL rejected | Parser DDL rejected | Rejected |
| Direct key projections | 3.509 | 1.876 | 1.211 | 11.760 | 68.893 |
| Shifted range key | 3.737 | 1.920 | 1.323 | 15.917 | 70.942 |
| Shifted prefix and range through nested aliases | 4.946 | 672.992 | 478.926 | 2427.867 | 4.327 |
| Shifted CTE keys with residual filter | 10.151 | 650.993 | 469.301 | 2442.590 | 57.318 |
| Joined-view update, 501 partner rows | 1.291 | 0.138 | 0.124 | Rejected | Rejected |
| Joined-view update, 5,001 partner rows | 1.254 | 0.139 | 0.131 | Rejected | Rejected |
| Geographic mixed turns, forward | 2.419 | 0.173 | Mismatch | Rejected | Rejected |
| Geographic mixed turns, reverse | 2.340 | 0.147 | Mismatch | Rejected | Rejected |
| Geographic near-hole touch | 2.057 | 0.135 | Mismatch | Rejected | Rejected |
| Geographic near-hole crossing | 2.617 | 0.155 | Mismatch | Rejected | Rejected |
| Japanese ngram wildcard | 0.497 | 0.372 | Parser DDL rejected | Parser DDL rejected | Rejected |
| Japanese ngram exclusion | 0.786 | 0.499 | Parser DDL rejected | Parser DDL rejected | Rejected |
| Geographic corner overlay | 0.293 | 0.114 | Mismatch | Rejected | Rejected |
| Geographic polar-touch overlay | 0.355 | 0.106 | Mismatch | Rejected | Rejected |

Fsdb and MySQL passed all 22 SQL lanes in both passes. Fsdb took 1.6×
MySQL's median for ordinary correlated equality and 2.3× for the
ordinary range. For a shifted prefix and range through nested aliases,
Fsdb was 136× faster than MySQL. The shifted CTE with a residual filter
ran 64× faster. An earlier plan comparison showed MySQL scanning the
child table per parent on those two shapes. Fsdb translates the shifted
predicates into bounds on the stored key.
[Shifted key plans](2026-11-26-offset-key-ranges.md).

The nested checked-view update took 5.8× MySQL's median. The two
prefix fixtures took approximately 9× MySQL's median, with nearly flat
Fsdb latency as the partner grew tenfold. The indexed planar query
took 2.0× MySQL's median. Fsdb, MySQL, and MariaDB all reported `range`
through `ix_g`. MariaDB returned nine points, including eight on the
boundary. Fsdb and MySQL returned the single interior point.

Fsdb took roughly 14–17× MySQL's median on the four geographic turn
probes and 2.6–3.3× on the two overlays. Its five ngram cases took
1.2–1.6× MySQL's median. MariaDB and Dolt rejected the tested ngram
index DDL.

The accompanying range-lock change also passed 152 concurrency
schedule checks in MySQL and in both Fsdb storage modes. The schedule
results establish locking behavior in the sampled cases. The timing
figures above use a separate `READ COMMITTED` write fixture.
[Range-lock checks](../../torture/findings/2026-11-26-joined-view-primary-key-gaps.md#serializable-primary-key-ranges).

TiDB led the nested shifted count at 4.33 ms against Fsdb's 4.95 ms.
The CTE residual filter took 57.32 ms in TiDB and 10.15 ms in Fsdb.
TiDB returned a different updatability result for the nested view and
rejected the tested `MATCH ... AGAINST` and spatial queries.

## Matched ARM64 container point reads

Fsdb, MySQL 8.4.11, and Percona Server 8.4.11-12 also ran one at a time
in ARM64 Linux containers, with four CPUs, 2 GiB RAM, no container
swap, a new named data volume, and a localhost-published port. The
client ran on macOS with the same 10,000-row point-read fixture.
Both InnoDB servers used the native run's durability and buffer
settings. Fsdb used the same Release build with WAL and Server GC.
Pass two reversed engine and client-count order. Cells show operations
per second and p99 milliseconds averaged across passes.
[Per-pass results](789a3519-peer-container-reads.csv).
[Percona Server 8.4.11-12](https://docs.percona.com/percona-server/8.4/release-notes/8.4.11-12.html).

| Clients | Fsdb | MySQL | Percona |
| ---: | ---: | ---: | ---: |
| 1 | 2,887 / 2.63 | 10,488 / 0.19 | 10,386 / 0.20 |
| 16 | 10,465 / 7.20 | 44,158 / 0.80 | 43,350 / 0.82 |

At 16 clients, MySQL and Percona's mean point-read rates were within
2%, with their order reversing between passes. Fsdb reached 24% of
MySQL's throughput under those container limits.

## Reproduction

Use one disposable native server at a time on a free loopback port.
Run Fsdb, MySQL, MariaDB, then Dolt in pass 1 and reverse their order in
pass 2. Reverse the command order in pass 2 and pass `2` as the final
numeric argument. Set MySQL's `ngram_token_size` to `2`.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass" point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass"
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" offset-key-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" prefix-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" edge-controls
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- "$engine" "$port" "$pass" overlay-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- "$engine" "$port" "$pass"
```

Run the same commands against two fresh local TiDB clusters. Keep the
three roles on the same host and reverse the command order in pass 2.

For the container point-read control, start Fsdb, MySQL, then Percona
in pass 1 and reverse their order in pass 2. Use the differential
harness's pinned MySQL image. The other images were `percona/percona-server` at
`sha256:e759516b1b2f446ec83c06e5cd47ac9a4d13b3a19a8d155da3c555feed621ac0`
and `mcr.microsoft.com/dotnet/runtime` at
`sha256:ec7cd715c9ab3831d134c7041d0d65891b97a185717a49147a22f75090b44338`.
Run the `point-reads` command only, with the same client harness and a
fresh volume for each engine and pass.
[Pinned MySQL image](../../torture/compose.yaml).
