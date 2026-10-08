# Join-hint readability refactor comparison

Measured on 2026-10-08, arm64 macOS, .NET SDK 10.0.401, Debug assemblies.
Before is `4f8fb69d`; after is the accompanying Executor refactor. The baseline
assembly and its dependencies were copied before editing or rebuilding.
Both versions use `DOTNET_PROCESSOR_COUNT=8` and
`DOTNET_GCHeapHardLimit=0x100000000`.

The harness is `benchmarks/scripts/hint-handling.fsx`, with its assembly reference
pointing to each version. Runs execute serially in before/after/after/before
order. Each invocation warms each query 200 times and measures five samples of
2,000 calls. Medians combine both invocations. The in-memory store contains one
INT row; result rows are checked. Network and durability are excluded.

| Query | Before µs/call | After µs/call | Before bytes/call | After bytes/call |
|---|---:|---:|---:|---:|
| plain | 101.66 | 103.32 | 217568.24 | 217569.43 |
| timeout | 113.55 | 112.43 | 225232.49 | 225232.49 |
| combined | 118.06 | 117.88 | 229776.86 | 229776.86 |
| join-warning | 196.24 | 197.41 | 308785.11 | 308705.11 |
| join-eliminated | 208.17 | 210.75 | 342497.23 | 340385.23 |

The refactor separates source materialization and safe query-planning probes
from warning traversal, and reuses the bound WHERE elimination result.
The warning and eliminated-query cases allocate 80 and 2,112 fewer bytes per
call, respectively. Elapsed medians vary between approximately -1.0% and +1.6%
on this shared host; these samples establish neither a speedup nor a meaningful
throughput regression. Common-path allocations are effectively unchanged.
Raw samples are in the adjacent `4f8fb69d-readability-*.csv` files.

Validation: `just check` passes all 3,115 tests with no build warnings or errors.
The native MySQL 8.4.11 wire comparison passes 80 contracts / 10,430 steps with
zero differences, using a disposable server with 64 MiB buffer pool and redo.
The contract artifact is
`torture/artifacts/runs/20261008T191821672-68095/contracts`.
