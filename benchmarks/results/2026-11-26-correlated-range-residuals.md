# Correlated composite ranges with WHERE filters

The correlated range planner now keeps the equality prefix and ordered
range when the rest of the WHERE clause contains supported integer
predicates. It reuses the same evaluator as derived-table filters for
column-to-column comparisons, null tests, Boolean combinations,
`BETWEEN`, and scalar `IN` lists. It resolves aliases back to stored
columns and binds eligible outer integer values per probe. SQL unknown
remains unknown through negation, including `NOT IN` with a NULL
list member. [MySQL comparison rules](https://dev.mysql.com/doc/refman/8.4/en/comparison-operators.html).

If the index covers the lookup bounds, the planner can apply the
compiled predicates directly to the slice. Partially covered lookups
retain evaluation of the original WHERE through the aggregate path.
Coercing and unclassified predicates use ordinary evaluation.

## Native timing

Fsdb Release at `9ae6848b` and the candidate ran with WAL and Server
GC on .NET 10.0.12. MySQL 8.4.11 used commit-time flush and disabled
binary logging. Each server ran separately on an Apple M2 Max
with macOS 15.6 over IPv4 loopback. The fixture has 500 parents and
10,000 children. Each lane validates results during preflight,
four warmups, and 30 timed calls. Pass 2 reverses both engine and
lane order. Cells average the two per-pass p50s in milliseconds.
[Per-pass samples](2026-11-26-correlated-range-residuals.csv).

| Workload | Fsdb before | Fsdb after | MySQL 8.4.11 |
| --- | ---: | ---: | ---: |
| Unfiltered derived count | 6.45 | 8.44 | 1.71 |
| Derived column-to-column equality count | 318.22 | 8.12 | 2.38 |
| Nested derived disjunction sum | 329.08 | 8.02 | 2.49 |
| CTE `NOT IN` count | 324.29 | 8.33 | 2.41 |
| Correlated outer comparison and null-test sum | 330.09 | 9.91 | 2.44 |

The four filtered cases ran 33–41× faster and took 3.2–4.1×
MySQL's median latency. The unfiltered loopback control varied
from 3.86 to 9.05 ms before and 7.20 to 9.68 ms after.

A separate Before/After/After/Before in-process control with 100
parents and 4,000 children gave unfiltered p50s of 2.612, 2.421,
2.330, and 2.613 ms. Each run had 60 warmups and 150 checked calls.
Median calling-thread allocation moved from 1,846,264 to 1,857,464
bytes per unfiltered query. The four filtered cases fell from
44.25–50.13 MB to 2.16–2.62 MB in the same-sized fixture, with
five allocation samples after three warmups per query.

The native MySQL matrix matched 1,284 ordered rows across 322
statements, including column names, type codes, scales, warning
lists, and the unknown-column diagnostic. It exercised NULL outer
values, signed bounds, nested derived tables, and both filtered
and unfiltered CTEs. Four binary prepared executions also matched,
including NULL and non-NULL members of a `NOT IN` list.

Run the timing fixture against a disposable native server with
`dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS correlated-residual-controls`.
