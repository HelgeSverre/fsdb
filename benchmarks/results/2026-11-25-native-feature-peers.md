# Native MySQL-protocol feature comparison

Measured on 2026-11-25 on an Apple M2 Max Mac (12 cores, 32 GiB, macOS 15.6)
with Fsdb source `e8bebd10`, Release .NET 10.0.12, and MySqlConnector 2.6.2.
The four servers ran natively over loopback TCP, one at a time. Fsdb used
a disposable WAL data directory and `DOTNET_gcServer=1`. MySQL 8.4.11 and
MariaDB 11.8.10 used InnoDB with a 256 MiB buffer pool, commit flush set to 1,
and binary logging off. Dolt 2.7.7 used its default journal settings.

## Method

Each pass creates a fresh database, seeds it in batches, and checks the
returned values or affected-row counts. The client uses one connection,
with TLS and pooling disabled. It checks each operation during timing,
including the wire round trip, and excludes setup and seeding. Each lane has
one preflight, four warmup executions, and 60 to 150 timed observations.
Two passes reverse engine and workload order. The table averages their
per-pass medians, with both p50 and p95 in the [raw results](2026-11-25-native-feature-peers.csv).
The host ran other workloads and had about 13 GiB of swap in use. The large
gaps repeat across both passes. Small relative differences are directional.

- Correlated counts use 500 outer rows and 10,000 inner rows with an index
  on `(parent_id, id)` and 20 children per parent. The range adds
  `c.id BETWEEN p.id AND p.id + 1000` and matches three children per parent.
- The update targets one row in a join over a nested view with
  `CASCADED CHECK OPTION`. The client verifies view metadata, one affected
  row per autocommit, and the final committed value.
- The planar lookup checks the strict interior of a small rectangle on a
  grid of 10,000 indexed points. Both Fsdb and MySQL show `range` on
  `ix_g`. MariaDB uses the same index but returns the eight boundary points
  as well. MariaDB and Dolt receive a plain `POINT NOT NULL` declaration
  without MySQL's explicit column SRID attribute. The geographic control
  checks the rounded 219,278-metre WGS 84 distance across the antimeridian.
- The CJK searches use MySQL's `WITH PARSER ngram` declaration with the
  default bigram size and a binary collation. A 10,000-article corpus has
  100 matches for each quoted phrase. The Japanese fixture also has 100
  near matches with the same first bigram but a different continuation.
  The client checks the sum of matching IDs.

## Results

| Workload, p50 ms | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Correlated equality count | 5.355 | 3.017 | 2.157 | 5.245 |
| Correlated equality and range count | 511.520 | 3.047 | 1.502 | 10.127 |
| Nested joined-view update | 4.632 | 0.412 | 0.251 | Rejected update |
| Indexed planar `MBRContains` | 0.807 | 0.295 | Different boundary result | Missing function |
| Geographic antimeridian distance | 0.547 | 0.129 | Rejected constructor | Unsupported SRID |
| Japanese phrase | 1.501 | 1.230 | Rejected parser | Rejected parser syntax |
| Chinese phrase | 1.554 | 1.219 | Rejected parser | Rejected parser syntax |
| Korean phrase | 1.644 | 1.096 | Rejected parser | Rejected parser syntax |

Fsdb matched all eight checked MySQL results. CJK was closest at roughly
1.2–1.5× the latency, and the indexed spatial lookup was 2.7×. The joined
view update was 11× slower than MySQL. The correlated range was 168× slower
and is the largest planner gap in this set.

The stock MariaDB build rejected MySQL's parser name. MariaDB documents
[Mroonga](https://mariadb.com/docs/server/server-usage/storage-engines/mroonga/about-mroonga)
as a separate CJK-ready engine. Dolt's results here describe the tested SQL
forms. Distributed TiDB is outside this native comparison. Its
[compatibility notes](https://docs.pingcap.com/tidb/stable/mysql-compatibility/)
list limits on view writes, spatial SQL, and full-text indexing in
self-managed deployments.

## Correlated range controls

Two additional passes on MySQL and Fsdb used the same fixture and
validation, with 15 timed executions per variant.

| Predicate or hint, p50 ms | Fsdb | MySQL |
| --- | ---: | ---: |
| Unhinted `BETWEEN` | 491.587 | 2.926 |
| `FORCE INDEX (ix_child)` | 37.595 | 3.005 |
| `IGNORE INDEX (PRIMARY)` | 38.960 | 3.628 |
| Unhinted `>=` and `<=` | 492.397 | 3.552 |

Forcing the child index improved Fsdb by 13×, and removing the primary
index from consideration gave a similar result. Splitting the predicate
kept the original cost. These controls isolate the competing primary-key
range as a contributor. The planner can materialize a physical range
before comparing or intersecting its rows with the equality candidate.
The next optimization is to compare candidate cardinalities before
materialization, or to use the composite index for the equality prefix and
range together. The forced plan still takes about 12.5× as long as the
same hinted MySQL query.
[Correlated source selection](../../src/Fsdb/Engine/Executor.fs#L18679).

## Durable concurrent writes

The [separate OLTP comparison](2026-11-25-native-oltp-peers.md) supplies the
concurrency figures. Its 16-worker test used disjoint update ranges. It
measures concurrent throughput without isolating contended-row scheduling.

| Workload, ops/s at 16 workers | Fsdb | MySQL | MariaDB | Dolt |
| --- | ---: | ---: | ---: | ---: |
| Primary-key point read | 21,055 | 107,728 | 103,248 | 66,936 |
| Mixed reads and autocommit updates | 11,651 | 65,613 | 78,756 | 377 |
| Two-update transactions | 2,877 | 20,146 | 22,814 | 189 |

Fsdb reached 20%, 18%, and 14% of MySQL throughput in those workloads. A
hypothetical 2× increase would put it at 39%, 36%, and 29%, respectively,
with MySQL's measured throughput held fixed.

## Reproduction

Start the chosen native engine on a free loopback port with an empty
disposable store and an empty-password root account. Run the client against
that instance, then stop it before starting the next one.

```sh
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3349 1
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3349 2
dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- MySQL 3349 3 range-controls
```

The engine argument is `MySQL`, `Fsdb`, `MariaDB`, or `Dolt`. The pass
number determines the scratch database name and lane order. The script
fails if that database already exists. Timings and checked SQL failures
go to standard output as CSV rows. Spatial plans and error messages go to
standard error.
