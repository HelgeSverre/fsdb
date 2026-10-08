# Optimizer hint handling cleanup

Measured on 2026-10-08 on the same arm64 macOS host with .NET SDK 10.0.401,
Debug assemblies. Before is `8ca71191`; after is the hint-handling refactor
based on that revision. The refactor shares one comment scan between timeout
and geometry hints and separates timeout argument parsing from diagnostics.

`DOTNET_PROCESSOR_COUNT=8`, `DOTNET_GCHeapHardLimit=0x100000000`.
No concurrent build, test, native oracle, or benchmark ran during measurement.
The script calls QueryHandler directly against in-memory storage. It includes
SQL parsing and execution, but excludes network transport and durability.

Run `dotnet fsi --nologo benchmarks/scripts/hint-handling.fsx` after building.
Each query gets 100 warm-up calls and five samples of 2,000 calls. Every measured result
is checked. Times are sample medians; allocations are per-call values from the
last sample, including harness costs. Raw samples are stored alongside this
report. Early samples include visible JIT warm-up, so these timings are a small
local comparison rather than a server throughput estimate.

| Query | Before ms / 2,000 | After ms / 2,000 | Before bytes / call | After bytes / call |
|---|---:|---:|---:|---:|
| plain | 191.225 | 191.996 | 208944.24 | 208928.24 |
| timeout | 242.226 | 220.537 | 216672.74 | 215976.74 |
| combined | 256.808 | 235.607 | 220336.86 | 219568.86 |

Plain SELECT timing is essentially unchanged. Hinted query medians improve by
about 8–9%, with 696 fewer bytes per timeout query and 768 fewer bytes per query
containing both hints. These results support retaining the simpler shared scan;
they do not establish a general workload speedup.

Validation: all 3,092 tests pass. The full native MySQL 8.4.11 wire suite passes
66 cases / 8,445 steps with zero differences. The native server uses a disposable
64 MiB buffer pool and 64 MiB redo capacity.

Wire artifacts: `torture/artifacts/runs/20261008T090438288-35397/contracts`.
