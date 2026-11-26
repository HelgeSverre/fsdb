# Correlated range aggregates through filtered projections

A correlated count over a mergeable derived table uses the
`(parent_id, id)` composite index to find the child rows for each parent.
Before this change, a source filter such as `bucket = 1` disabled the
direct slice. The eligible path now evaluates null tests and direct
signed comparisons on `TINYINT`, `SMALLINT`, `MEDIUMINT`, and `INT`
before projecting the selected rows. It preserves SQL three-valued logic through `AND`, `OR`,
and `NOT`, including `BETWEEN` and null-safe equality. The general
path still handles source expressions that can coerce values or raise
diagnostics. [MySQL comparison rules](https://dev.mysql.com/doc/refman/8.4/en/comparison-operators.html).
[MySQL logical operators](https://dev.mysql.com/doc/refman/8.4/en/logical-operators.html).

## Native timing

Native Release Fsdb at `b8ad0c8a` and the candidate ran with WAL and
Server GC on .NET 10.0.12. MySQL 8.4.11 used commit flushing set to 1
and binary logging disabled. All three ran one at a time on the same
Apple M2 Max, macOS 15.6, over IPv4 loopback. The shared 32 GiB host
had about 12 GiB of swap in use.

The fixture has 500 parents and 10,000 children. After a preflight and
four warmups, each workload validates the aggregate on 30 timed calls.
Pass 2 reverses engine and workload order. The results average the
two per-pass p50 timings in milliseconds.
[Per-pass samples](2026-11-26-correlated-projection-filters.csv).

| Workload | Fsdb before | Fsdb after | MySQL 8.4.11 |
| --- | ---: | ---: | ---: |
| Derived null-test count | 7.93 | 8.81 | 2.43 |
| Derived equality-filter count | 300.62 | 7.84 | 2.43 |
| Nested filtered sum | 313.36 | 10.54 | 2.46 |
| CTE filtered count | 305.27 | 8.35 | 2.45 |

The three newly covered cases ran 30–38× faster and took 3.2–4.3×
MySQL's latency. The existing null-test control varied between
4.63 and 11.24 ms before and between 7.68 and 9.93 ms after.

In a separate warmed in-process fixture with 100 parents and 4,000
children, median calling-thread allocations fell from 42.65 to 1.87 MB
for the equality-filter count, from 48.97 to 2.50 MB for the nested
sum, and from 43.54 to 1.89 MB for the CTE count. The null-test control
allocated 1.87 MB in each build. Each median uses five validated calls
after three warmups.

The native correctness matrix matched 964 ordered rows, warning
lists, and the unknown-column diagnostic across 242 statements.
It covered nullable and signed boundary values, nested aliases,
CTEs, and `COUNT(*)`, `COUNT(column)`, `SUM`, and `AVG`.
Four executions of a binary prepared CTE count also matched,
including a NULL parameter to the source's null-safe comparison.

Run the timing fixture on a disposable native server with
`dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS projected-filter-controls`.
