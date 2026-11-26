# Correlated ranges after text equality keys

A correlated aggregate with a text equality and an integer range can
use the same composite index when the equality uses the indexed
column's collation. The ordered index keeps each full-order text
variant in its own run. SQL equality can include several of those
runs, such as `cafe`, `Cafe`, and `café` under
`utf8mb4_0900_ai_ci`. The lookup seeks the remaining key parts and
the suffix range separately within each matching run. A truncated
index or a comparison under a different collation retains ordinary
predicate evaluation.

The [wire fixture](../../torture/findings/2026-11-26-correlated-text-key-ranges.sql)
matched 12 ordered rows against the [pinned MySQL image](../../torture/config/toolchain.json).
It covers mixed text and integer equality keys, inclusive and
contradictory bounds, nullable keys and payloads, a renamed CTE,
literal equality, an explicit binary collation, and a forced truncated
index. It includes both NO PAD and PAD SPACE comparisons. A seeded
matrix added 120 statements and 1,800 ordered rows with matching
warnings across five collations, two text equality keys, and alternating
index directions. Six binary prepared executions matched all 36 rows
and column types, including a NULL key, variants that compare equal,
and a trailing space that compares distinctly under NO PAD.

## Native timing

Release Fsdb at `e630f08a` and the candidate ran with WAL and Server GC
on .NET 10.0.12. MySQL 8.4.11 flushed at commit with binary logging
disabled. All servers ran natively over IPv4 loopback on an Apple M2
Max with macOS 15.6. MySqlConnector 2.6.2 validated a count of 90 per
execution with 30 parents and 1,200 children. Each pass checked the
result before four warmups and during all 20 timed executions. Pass two
reversed engine and query order. Cells average the two medians.
[Per-pass medians and p95s](2026-11-26-correlated-text-key-ranges.csv).

| Workload | Fsdb before (ms) | Fsdb after (ms) | MySQL (ms) |
| --- | ---: | ---: | ---: |
| Integer equality prefix and range | 1.36 | 1.32 | 0.28 |
| Text and integer equality prefix with range | 10.88 | 3.34 | 0.36 |

The text workload improved by 3.26× and took 9.24× MySQL's time.
The fixture can be rerun with
`dotnet fsi benchmarks/scripts/native-feature-peers.fsx -- ENGINE PORT PASS text-key-controls`.
