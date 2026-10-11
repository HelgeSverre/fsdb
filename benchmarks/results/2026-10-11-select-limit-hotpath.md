# `sql_select_limit` on the point-lookup hot path

The new session limit is checked on every top-level SELECT without an explicit
LIMIT. This probe asks whether that check adds measurable cost to an ordinary
lookup when the variable retains its unlimited default.

`PointLookupPipeline` seeded the same 10,000-row primary-key table in each
Release process and measured cached text and reused prepared `id=3` lookups.
The Apple M2 Max host used .NET 10.0.401. Each action had 1,000 warmups,
eight trials, and 2,000 calls per trial. Processes ran in ABBA order:
current/baseline/baseline/current before the rewrite, then
optimized/baseline/baseline/optimized after it. The baseline was commit
`6eab6f38`, before `sql_select_limit`; the pre-rewrite version was
`3bbb6954`. The table uses each process's final three trials, when allocation
had settled, then takes the median of the two process medians.

| Comparison | Cached text B/call | Reused prepared B/call |
|---|---:|---:|
| Pre-limit baseline, first ABBA | 90,304 | 97,102 |
| Original limit helper, first ABBA | 90,456 | 97,222 |
| Added allocation | **152** | **120** |
| Pre-limit baseline, second ABBA | 90,324 | 97,102 |
| Simplified limit helper, second ABBA | 90,360 | 97,154 |
| Added allocation | **36** | **52** |

The rewrite checks the already-normalized session key directly and returns the
original statement when the limit is unlimited or explicit. It avoids the
per-call option-combinator closures and unnecessary AST reconstruction. The
focused `sql_select_limit` and EXPLAIN regressions pass. The remaining small
allocation difference is not attributed to one call site; the compared
revisions also include later unrelated work. Latency ranges overlap across
these short process runs, so they do not establish a speedup or slowdown.

[Raw trials](2026-10-11-select-limit-hotpath.csv) retain every per-process
measurement, including warmup drift.
