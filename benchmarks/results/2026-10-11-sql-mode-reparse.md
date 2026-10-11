# Unchanged SQL mode in prepared statement handling

A `gc-verbose` trace of the reused prepared point lookup attributed roughly
580 MB of sampled allocation to `SqlMode.scanModes`. `QueryHandler` reapplies
the session's `sql_mode` to the current store before each statement, and
`Storage.setSqlMode` parsed and rebuilt the settings record even when the text
already matched. The change makes that assignment a no-op for ordinally
identical mode text. Changed mode text still goes through the same parser.

The 10,000-row `PointLookupPipeline` fixture ran the reused prepared handler
for 30 trials of 2,000 calls after 1,000 warmups. The two separate Release
builds ran on the same host. Raw trials are in
[before](2026-10-11-sql-mode-reparse-before.csv) and
[after](2026-10-11-sql-mode-reparse-after.csv).

| Version | Median allocated bytes/call | Median latency/call |
|---|---:|---:|
| Before | 105,165 | 39.66 µs |
| After | 104,030 | 48.60 µs |

The allocation reduction is about 1.1 KiB/call. This separate-process timing
comparison does not establish a latency gain; the after run was slower, and
host conditions were not controlled. The large residual handler allocation
remains open. `just check` passed 3,385 tests.
