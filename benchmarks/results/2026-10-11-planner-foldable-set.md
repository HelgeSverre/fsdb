# Reusing the planner's foldable-function set

The prepared point-lookup allocation trace attributed about 997 MB of sampled
allocation to `Executor.plannerConstantEvaluator` during 400,000 calls. One
avoidable piece was its fixed set of builtin function names: constructing
each evaluator also reconstructed the same immutable set. The set is now a
module value, while each evaluator still checks the current registry and
expression before folding.

The shared 10,000-row `PointLookupPipeline` fixture ran 30 trials of 2,000
reused prepared-handler calls after 1,000 warmups. The preceding
[raw trials](2026-10-11-sql-mode-reparse-after.csv) and
[new raw trials](2026-10-11-planner-foldable-set.csv) are separate Release
builds on the same host.

| Version | Median allocated bytes/call | Median latency/call |
|---|---:|---:|
| Rebuilt set | 104,030 | 48.60 µs |
| Shared set | 98,899 | 52.16 µs |

Allocation fell by about 5.1 KiB/call. The separate-process timing runs do
not establish a latency gain. `just check` passed 3,385 tests. The remaining
handler allocation and broader MySQL-relative point-lookup gap stay open.
