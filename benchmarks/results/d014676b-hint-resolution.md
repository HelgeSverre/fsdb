# Optimizer-hint resolution common-path comparison

Measured on 2026-10-08, arm64 macOS, .NET SDK 10.0.401, Debug assemblies.
Before is `d014676b`; after is the SELECT hint-resolution implementation based
on that revision. The baseline was built from a fresh archive of that commit.
Both use `DOTNET_PROCESSOR_COUNT=8` and `DOTNET_GCHeapHardLimit=0x100000000`.

Command: `dotnet fsi --nologo benchmarks/scripts/hint-handling.fsx`.
Measurements run serially in before/after/before/after order, after builds and
native verification have completed. The host also runs unrelated browser work;
its load average was approximately 45 before the baseline build. This activity
prevents clean attribution of elapsed-time changes to this patch.

Each script invocation checks results, warms each query 100 times, then takes
five samples of 2,000 in-memory QueryHandler calls. Timing values below are
sample medians. Network transport and durability are excluded. The queries
exercise common plain/timeout/SET_VAR paths, not the new table-resolution path.
Raw samples are in the adjacent `d014676b-hint-resolution-*.csv` files.

| Query | Before 1 ms | After 1 ms | Before 2 ms | After 2 ms | Before bytes/call | After bytes/call |
|---|---:|---:|---:|---:|---:|---:|
| plain | 331.650 | 213.169 | 259.411 | 224.492 | 209264.24 | 209392.24 |
| timeout | 339.284 | 298.365 | 264.150 | 247.281 | 216816.74 | 216960.49 |
| combined | 387.678 | 349.116 | 351.076 | 312.747 | 220808.86 | 220968.61 |

Allocations increase by approximately 128–160 bytes per query, below 0.1% of
these queries' existing allocations. The paired runs do not reproduce the
initial standalone snapshot's elapsed slowdown, but they do not establish a
speedup: host activity and JIT warm-up remain visible. A throughput claim needs
a quiet-host benchmark with representative table-hint workloads.

Correctness validation: 3,098 root tests pass; native MySQL 8.4.11 wire contracts
pass 70 cases / 8,665 steps with zero differences. Native processes use disposable
64 MiB buffer pools and redo capacity.
