# Native Fsdb and MySQL comparison, 2026-11-18

Fsdb source `316b0292493664e56679f79223dbfc6ace5667ac`, built in Release
from the stable 0.1.9 line. The comparison used native Homebrew MySQL 8.4.11
and Fsdb on the same Apple M2 Max Mac, with 12 cores, 32 GiB RAM,
macOS 15.6 (24G84), and .NET SDK 10.0.401. Both clients used MySqlConnector
over loopback with pooling disabled.

## Method

- The base dataset has 10,000 users, 50,000 orders, and 10,000 articles.
  The large dataset has 100,000 users, 500,000 orders, and 100,000 articles.
  The fixture also seeds spatial and functional-index tables at the user count.
  Every engine trial uses a fresh database with the same deterministic fixture.
- The latency sampler calls the existing
  [server benchmark methods](../Fsdb.Benchmarks/ServerBenchmarks.fs).
  There are three trials per target, with engine order reversed in the middle
  trial and workload order rotated. Each read has 0.35 s of warmup and at
  least 1.25 s of measurement. Each write trial runs the same number of
  timed operations for both targets, with an additional 25% warmup.
  Timed counts are 96 for batches, 128 for bursts, and 1,200 for other
  writes. A burst is one 16-operation invocation. Seeding and connection
  setup are outside timing.
- MySQL's observed InnoDB buffer pool was 1,207,959,552 bytes on both
  instances. Binary logging was off. The durability pair is Fsdb WAL
  with commit fsync against MySQL with `innodb_flush_log_at_trx_commit=1`.
  The non-durable pair is in-memory Fsdb against MySQL with
  `innodb_flush_log_at_trx_commit=0`. The read lane uses in-memory Fsdb
  against the durable MySQL server and issues only reads.
- The concurrency lane uses the existing
  [load harness](../Fsdb.Benchmarks/LoadBenchmarks.fs) on the base dataset
  with Fsdb WAL and durable MySQL. At each worker count it alternates
  engine order across three fresh trials of 1 s warmup and 3 s measured.
  Read, disjoint update, hot update, insert, and mixed traffic are separate
  workloads. Mixed traffic flips a coin between a read and a disjoint
  update. Throughput counts only completed operations.

Latency tables give the median of the three trial means. Spread is each
engine's trial max minus min, divided by its median. This is a measure of
observed variability. The largest aggregations and window queries sometimes
fit only one or two invocations in a trial, and large Fsdb full-text and
window timings varied substantially. The full-text queries exercise this
English-language fixture and these specific predicates.

The large Fsdb primary-key control came in at 0.731 ms in its first
trial, then 0.471 and 0.476 ms in the last two. The large natural-language
full-text case likewise settled at about 8.8 ms in the last two trials after
a 40.5 ms first trial.

The first write attempt stopped at the sampler's method binding for a
concurrent workload. The complete write matrix was rerun with the sampler
fixed. Only the complete run is included.

[Per-trial latency CSV](2026-11-18-native-mysql-comparison-latency.csv). Lower is better in the latency tables.

## Base read latency

| Workload | Fsdb ms | MySQL ms | Fsdb / MySQL | Trial spread (Fsdb / MySQL) |
| --- | ---: | ---: | ---: | ---: |
| PointSelectByPk | 0.288 | 0.039 | 7.30× | 16% / 1% |
| PreparedPointSelect | 0.159 | 0.034 | 4.75× | 6% / 3% |
| FilterBySecondaryEquality | 0.467 | 0.172 | 2.72× | 10% / 2% |
| FilterBySecondaryRange | 0.270 | 0.043 | 6.25× | 3% / 1% |
| CountIndexedDisjunction | 0.357 | 0.173 | 2.07× | 8% / 5% |
| CountSelectiveIndexedRange | 0.314 | 0.043 | 7.29× | 7% / 1% |
| FilterScanOrderLimit | 3.354 | 1.992 | 1.68× | 24% / 0% |
| FilterBySpatialBounds | 48.404 | 0.058 | 837.49× | 14% / 2% |
| JoinUsersOrders | 0.435 | 0.245 | 1.77× | 19% / 30% |
| CorrelatedOrderCount | 0.681 | 0.207 | 3.29× | 16% / 9% |
| CorrelatedDerivedRange | 42.948 | 448.980 | 0.10× | 12% / 1% |
| GroupByAggregate | 56.739 | 30.680 | 1.85× | 21% / 9% |
| JsonExtract | 0.324 | 0.063 | 5.17× | 14% / 4% |
| WindowTopOrders | 89.859 | 47.173 | 1.90× | 17% / 18% |
| FullTextNaturalSearch | 0.393 | 0.468 | 0.84× | 26% / 3% |
| FullTextPointIntersection | 0.213 | 2.488 | 0.09× | 12% / 13% |
| CountUsers | 0.161 | 0.783 | 0.21× | 25% / 1% |

## Large read latency

| Workload | Fsdb ms | MySQL ms | Fsdb / MySQL | Trial spread (Fsdb / MySQL) |
| --- | ---: | ---: | ---: | ---: |
| PointSelectByPk | 0.476 | 0.038 | 12.37× | 55% / 7% |
| FilterBySecondaryRange | 0.431 | 0.042 | 10.32× | 43% / 15% |
| CountSelectiveIndexedRange | 0.444 | 0.044 | 10.15× | 62% / 5% |
| FilterScanOrderLimit | 71.854 | 19.538 | 3.68× | 40% / 3% |
| JoinUsersOrders | 0.862 | 0.293 | 2.94× | 62% / 5% |
| CorrelatedOrderCount | 1.557 | 0.212 | 7.34× | 60% / 12% |
| GroupByAggregate | 838.045 | 441.682 | 1.90× | 12% / 8% |
| WindowTopOrders | 1201.530 | 790.964 | 1.52× | 127% / 9% |
| FullTextNaturalSearch | 8.810 | 4.691 | 1.88× | 360% / 5% |

## Matched write latency

| Workload | Fsdb WAL ms | MySQL durable ms | Fsdb / MySQL | Trial spread | Fsdb memory ms | MySQL no-fsync ms | Fsdb / MySQL | Trial spread |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| InsertSingle | 0.343 | 0.065 | 5.25× | 10% / 8% | 0.232 | 0.044 | 5.22× | 11% / 5% |
| UpdateSingleRow | 0.389 | 0.073 | 5.31× | 7% / 6% | 0.283 | 0.046 | 6.12× | 16% / 3% |
| InsertBatch100 | 11.110 | 0.837 | 13.27× | 30% / 20% | 7.596 | 0.765 | 9.92× | 7% / 15% |
| SequentialInsertBurst | 5.253 | 1.037 | 5.07× | 11% / 4% | 3.875 | 0.705 | 5.50× | 12% / 3% |
| ConcurrentInsertBurst | 4.278 | 0.446 | 9.60× | 12% / 14% | 3.177 | 0.285 | 11.13× | 14% / 12% |
| ConcurrentPointUpdateBurst | 3.323 | 0.471 | 7.06× | 5% / 10% | 2.405 | 0.312 | 7.71× | 9% / 27% |
| TransactionTwoPointUpdates | 1.227 | 0.151 | 8.11× | 13% / 10% | 1.046 | 0.141 | 7.39× | 4% / 1% |

## Durable concurrency

The table uses the arithmetic mean across three trials. Higher operations per second is better.

Targets: fsdb-wal and durable MySQL. Workers: 1, 8, 16. 3 trial(s), 3.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | point-read | 3942 | 8.1% | 0.0% | 26781 | 1.1% | 0.0% | 0.15x |
| 1 | update-distinct | 2907 | 0.4% | 0.0% | 13076 | 4.3% | 0.0% | 0.22x |
| 1 | update-hot | 4799 | 4.2% | 0.0% | 14162 | 5.3% | 0.0% | 0.34x |
| 1 | insert | 3171 | 6.1% | 0.0% | 14017 | 1.2% | 0.0% | 0.23x |
| 1 | mixed | 2432 | 21.7% | 0.0% | 17385 | 0.6% | 0.0% | 0.14x |
| 8 | point-read | 6062 | 0.8% | 0.0% | 107689 | 3.4% | 0.0% | 0.06x |
| 8 | update-distinct | 3869 | 4.6% | 0.0% | 33932 | 11.8% | 0.0% | 0.11x |
| 8 | update-hot | 6784 | 3.2% | 0.0% | 39967 | 0.4% | 0.0% | 0.17x |
| 8 | insert | 4566 | 4.5% | 0.0% | 41888 | 0.5% | 0.0% | 0.11x |
| 8 | mixed | 4092 | 4.9% | 0.0% | 59469 | 0.9% | 0.0% | 0.07x |
| 16 | point-read | 5751 | 2.9% | 0.0% | 118570 | 0.1% | 0.0% | 0.05x |
| 16 | update-distinct | 3313 | 9.4% | 0.0% | 54748 | 2.4% | 0.0% | 0.06x |
| 16 | update-hot | 5754 | 3.3% | 0.0% | 37573 | 3.6% | 0.0% | 0.15x |
| 16 | insert | 3889 | 1.1% | 0.0% | 53654 | 2.1% | 0.0% | 0.07x |
| 16 | mixed | 3567 | 3.6% | 0.0% | 75732 | 0.5% | 0.0% | 0.05x |

## Result checks

On the base fixture, both engines returned the same rows for a representative
spatial query, the correlated derived-range count, the primary-key-constrained
full-text query, and the whole-table count. For the sampled natural-language
query, both returned the same 20 row IDs when compared as sets.

For the representative spatial query, a box centered at (5000, 5000), both
returned IDs 4999, 5000, and 5001. Fsdb explained the query as `ALL`, with no
key and an estimate of 10,000 rows. MySQL reported `range` on
`sx_places_location`. This plan difference was observed with the same
`MBRINTERSECTS` predicate and an SRID 0 polygon literal.

## Takeaways

- MySQL led 22 of 26 read comparisons, including all nine large-data cases,
  and all seven write workloads under each durability configuration.
- Fsdb's clearest wins were the base correlated derived-range count
  (10.5×), the primary-key-constrained full-text case (11.7×), and the
  whole-table count (4.9×). Base natural-language full-text was closer
  and its trials were more variable.
- Durable Fsdb writes took 5.1–13.3× as long as MySQL across the selected
  latency workloads. MySQL also led in the pair that skips commit-time fsync.
- MySQL led all 15 throughput comparisons. Fsdb's throughput rose from one
  to eight workers and fell from eight to sixteen for all five workloads.
  With sixteen workers, durable mixed traffic averaged 3,567 operations per
  second on Fsdb and 75,732 on MySQL.
