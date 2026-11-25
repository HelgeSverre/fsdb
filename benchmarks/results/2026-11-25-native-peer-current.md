# Fsdb against native MySQL, MariaDB, and Dolt

The 2026-11-25 benchmark compares Fsdb `64f67e1f`, MySQL 8.4.11,
MariaDB 11.8.10, and Dolt 2.7.7. MySQL supplies the expected SQL
results. The servers ran one at a time over native loopback TCP on
an Apple M2 Max with 12 cores, 32 GiB RAM, macOS 15.6, and about
12.7 GiB of swap in use. This is a shared host, and the workloads
use warm data.

Fsdb ran in Release on .NET 10.0.12 with WAL and `DOTNET_gcServer=1`.
The MySQL and MariaDB configuration uses InnoDB, a 256 MiB buffer pool,
commit flushing set to 1, and binary logging disabled. Dolt ran with
its default journal and ordinary SQL commits. The client used
MySqlConnector 2.6.2 with TLS and pooling disabled. Pass 2 reversed the
engine and workload order. Tables average the two pass statistics.

## Primary-key reads and durable updates

Both fixtures seed 10,000 rows. Each worker uses one connection, sends
one operation at a time, and requests READ COMMITTED. Each lane runs
1.5 seconds of warmup and six seconds of measurement. The point-read client checks
the ID, name, age, and row count for each random primary-key lookup.
The write client increments an indexed column and checks the affected
count for every update. The table-wide counter must agree with the
successful acknowledgements in both phases before a write lane receives
a timing.

Cells show operations per second followed by p99 latency in milliseconds.

| Clients | Workload | Fsdb | MySQL | MariaDB | Dolt |
| ---: | --- | ---: | ---: | ---: | ---: |
| 1 | Primary-key reads | 6,190 / 0.34 | 18,688 / 0.26 | 20,396 / 0.35 | 16,147 / 0.16 |
| 1 | Disjoint updates | 4,855 / 0.47 | 7,912 / 2.46 | 17,890 / 0.16 | 180 / 10.18 |
| 1 | Hot-row updates | 4,883 / 1.11 | 8,217 / 1.17 | 17,961 / 0.15 | 179 / 9.97 |
| 16 | Primary-key reads | 23,010 / 13.24 | 102,525 / 0.78 | 102,649 / 0.52 | 69,027 / 0.92 |
| 16 | Disjoint updates | 12,604 / 15.69 | 38,165 / 1.72 | 55,996 / 0.87 | 176 / 146.43 |
| 16 | Hot-row updates | 5,583 / 19.02 | 17,885 / 4.55 | 38,264 / 0.83 | Counter mismatch |

At 16 clients, Fsdb delivered 22% of MySQL's point-read throughput,
33% of its disjoint update throughput, and 31% of its hot-row update
throughput. Doubling the point-read rate would reach 46.0k ops/s,
or 45% of MySQL, 45% of MariaDB, and 67% of Dolt at their measured
rates. Fsdb's point-read p99 was 17× MySQL's.

Fsdb reached 19–25% of MySQL's 16-client point-read rate across the
two passes. Host activity varied during the run. MySQL's single-client
disjoint updates fell from 13.8k to 2.1k ops/s between passes, and its
16-client disjoint updates fell from 50.4k to 26.0k ops/s. Those ranges
limit the precision of the write comparisons and the single-client rankings.

Dolt acknowledged 1,355 and 1,421 measured hot-row updates, while the stored
counter increased by 93 and 100. The warmup also showed a mismatch.
The disjoint and one-client Dolt lanes passed the counter check.

The [earlier GC control](2026-11-25-cli-gc-load.md) measured higher
throughput and higher point-read p99 with Server GC than with workstation
GC. The current peer comparison uses Server GC, and the hypothetical
2× gain applies to that already tuned baseline.

## SQL feature latency

The correlated counts use 500 parent and 10,000 child rows. The
spatial index fixture uses 10,000 points. Geographic distance checks
a pair of points across the antimeridian. The CJK fixture checks
Japanese, Chinese, and Korean phrases over 10,000 documents using
MySQL's `ngram` parser and `utf8mb4_bin` collation. The joined-view probes
check the affected counts and final stored value, including when two partner rows match
the same target. Each pass takes 60 to 150 measurements per successful
lane after one preflight and four warmups.

| Workload, p50 ms | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 2.302 | 1.935 | 1.451 | 2.938 |
| Correlated equality and range count | 5.775 | 2.070 | 1.671 | 7.357 |
| Nested joined-view update | 1.187 | 0.191 | 0.287 | Rejected update |
| Joined-view prefix, 501 partner rows | 1.271 | 0.368 | 0.136 | Rejected update |
| Joined-view prefix, 5,001 partner rows | 1.292 | 0.327 | 0.144 | Rejected update |
| Indexed planar `MBRContains` | 0.224 | 0.175 | Boundary mismatch | Unsupported query |
| Geographic antimeridian distance | 0.145 | 0.084 | Rejected query | Rejected query |
| Japanese phrase | 0.854 | 0.776 | Rejected index DDL | Rejected index DDL |
| Chinese phrase | 0.841 | 0.598 | Rejected index DDL | Rejected index DDL |
| Korean phrase | 0.703 | 0.889 | Rejected index DDL | Rejected index DDL |

Fsdb passed all ten MySQL result checks in both passes. MySQL, Fsdb,
and MariaDB reported range access on the spatial index. MariaDB counted
eight additional boundary points. MariaDB and Dolt rejected the tested
ngram index DDL.

The complete-key joined-view update took 6.2× MySQL's median time,
and the 5,001-row prefix probe took 4.0×. The correlated range
took 2.8× MySQL's median time. The spatial index query took
1.3×, and the three CJK phrase queries took 0.8–1.4×. The Korean query
was faster on MySQL in pass 1 and on Fsdb in pass 2.
The joined-view timing is an autocommit, one-client control. The
[separate lock-scheduling checks](2026-11-25-bound-joined-view-update.md#lower-isolation-range-scans-in-larger-joined-updates)
exercise the latest behavior under concurrent transactions.

## Percona and TiDB

The [separate matched ARM64 container run](2026-11-25-peer-rerun.md#arm64-linux-containers)
compared Fsdb `d3b25c8a`, MySQL 8.4.11, and Percona Server 8.4.11-12.
With 16 clients, point-read throughput averaged 11.1k, 47.1k, and
46.9k ops/s, respectively. MySQL and Percona stayed within 2% in each
pass. All three passed the checked feature lanes. Large differences
between write passes limit the durable write comparison.

TiDB was outside the measured comparison. A separately configured
TiDB cluster would be a distributed reference for reads, ordinary
writes, and correlated SELECTs. Its self-managed compatibility
documentation lists updatable views, spatial features, and MySQL's
full-text index type as unsupported.
[TiDB MySQL compatibility](https://docs.pingcap.com/tidb/stable/mysql-compatibility/).

## Reproduction

Start one server on a free loopback port with a disposable data store,
the settings above, and an empty-password root account. Run the commands
below for each engine in the order Fsdb, MySQL, MariaDB, Dolt. For pass 2,
reverse the engine and command orders and change the pass argument to 2.
The scripts reverse the order of their internal workloads on pass 2.

```sh
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3389 1 point-reads
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3389 1
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3389 1 prefix-controls
dotnet fsi benchmarks/scripts/native-write-contention.fsx -- MySQL 3389 1
```

Per-pass samples are available for
[point reads](2026-11-25-native-peer-current-reads.csv),
[durable writes](2026-11-25-native-peer-current-writes.csv), and
[SQL features](2026-11-25-native-peer-current-features.csv).
