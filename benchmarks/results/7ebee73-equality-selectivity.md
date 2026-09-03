# Equality selectivity benchmark

Date: 2026-09-03

Platform: Apple M2 Max, .NET 10.0.11, MySQL 8.4.11

Mode: BenchmarkDotNet ShortRun, three measured iterations

The workload measures a selective equality, a literal `IN` list covering
half the indexed values, and an `IN` list covering every indexed value. The
scan twins use a separate unindexed column containing the same values, so
both sides retain direct-column comparison semantics.

## 10,000 rows

| Query | fsdb before | fsdb after | fsdb scan twin | MySQL 8.4 |
|---|---:|---:|---:|---:|
| Selective equality | 380.11 us | 267.89 us | 2.286 ms | 67.84 us |
| Half-value literal `IN` | 27.523 ms | 24.188 ms | 75.396 ms | 741.06 us |
| All-value literal `IN` | 93.186 ms | 74.415 ms | 78.999 ms | 1.248 ms |

The all-value figures use the low-variance four-case repeat after the broader
matrix exposed one noisy fsdb sample. ShortRun results are directional.

## 100,000 rows

| Query | fsdb before | fsdb after | fsdb scan twin | MySQL 8.4 |
|---|---:|---:|---:|---:|
| Selective equality | 1.684 ms | 1.444 ms | 25.319 ms | 206.77 us |
| Half-value literal `IN` | 317.658 ms | 280.126 ms | 433.192 ms | 6.253 ms |
| All-value literal `IN` | 896.568 ms | 740.288 ms | 737.737 ms | 11.246 ms |

The planner retains bucket lookup for selective and half-table predicates,
but sends the all-value predicate through the row-store scan without
materializing the bucket union. MySQL's much smaller constant factors remain
open; this change targets the avoidable plan-shape cliff rather than parity
with its mature executor.

## Commands

```sh
FSDB_BENCH_USERS=10000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=CountSelectiveIndexedEquality,CountSelectiveEqualityScan,CountHalfIndexedLiteralIn,CountHalfLiteralInScan,CountBroadIndexedLiteralIn,CountBroadLiteralInScan just _bench-run --quick

FSDB_BENCH_USERS=100000 FSDB_BENCH_ORDERS=1 FSDB_BENCH_ARTICLES=1 FSDB_BENCH_METHODS=CountSelectiveIndexedEquality,CountSelectiveEqualityScan,CountHalfIndexedLiteralIn,CountHalfLiteralInScan,CountBroadIndexedLiteralIn,CountBroadLiteralInScan just _bench-run --quick
```
