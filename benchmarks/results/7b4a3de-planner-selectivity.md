# Range selectivity benchmark

Date: 2026-09-03

Platform: Apple M2 Max, .NET 10.0.0, MySQL 8.4.11

Mode: BenchmarkDotNet ShortRun, three measured iterations

The workload compares a direct indexed range with an expression-equivalent
table-scan twin. The selective predicate matches one row. The broad predicate
matches every row.

## 10,000 rows

| Query | fsdb before | fsdb after | MySQL 8.4 |
|---|---:|---:|---:|
| Selective indexed range | 207.59 us | 204.59 us | 53.64 us |
| Selective scan twin | 5.865 ms | 5.925 ms | 1.108 ms |
| Broad direct range | 8.759 ms | 2.596 ms | 1.055 ms |
| Broad scan twin | 3.498 ms | 3.289 ms | 1.032 ms |

## 100,000 rows

| Query | fsdb before | fsdb after | MySQL 8.4 |
|---|---:|---:|---:|
| Selective indexed range | 245.06 us | 254.31 us | 54.64 us |
| Selective scan twin | 56.909 ms | 57.114 ms | 10.324 ms |
| Broad direct range | 176.546 ms | 33.937 ms | 9.945 ms |
| Broad scan twin | 41.113 ms | 42.903 ms | 9.630 ms |

The planner keeps the selective index path and sends the broad range through
the table scan. The 100,000-row broad result is noisy but shows the intended
change in slope and removes the index-row-lookup cliff. ShortRun results are
directional; they are not a substitute for longer throughput measurements.

## Commands

```sh
FSDB_BENCH_METHODS=CountSelectiveIndexedRange,CountSelectiveScan,CountUnselectiveIndexedRange,CountUnselectiveScan just _bench-run --quick

FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=CountSelectiveIndexedRange,CountSelectiveScan,CountUnselectiveIndexedRange,CountUnselectiveScan just _bench-run --quick
```
