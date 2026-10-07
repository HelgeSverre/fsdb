# Literal validation cache allocations

Measured 2026-10-08 on main at `a3c1f317` with a cache-hit lookup change.
Engine source diff SHA-256:
`4a8e480872136d97922cee0ac87ec1b535eed89e1476af0a495cf37f90ffc3d5`.

## Allocation result

The [benchmark](../scripts/literal-binding-allocations.fsx) warms each
expression, performs one million diagnostic lookups, and checks the results.
A separate control performs the same result comparison without the lookup.
GC.GetAllocatedBytesForCurrentThread measures allocation, not retained memory.

| Expression | Before: excess bytes | After: excess bytes |
|---|---:|---:|
| Valid string literal | 64,000,000 | 0 |
| Invalid NULL COLLATE | 64,000,000 | 0 |

The old lookup constructs a ConditionalWeakTable factory delegate on every
call, including cache hits. A direct by-reference TryGetValue avoids that
allocation. Cache misses retain GetValue's atomic insertion and the existing
Lazy result. The weak-key lifetime is unchanged.

The ordinary F# tuple-returning TryGetValue form was also measured: it leaves
32 bytes per warmed lookup. Using the CLR out parameter directly removes that
wrapper. The committed code uses that form.

## Memory investigation and limits

An earlier unbounded full-suite run passed in 218.75 seconds. A macOS sample
taken during that process reported physical footprint 679.1M and peak 33.7G.
That observation prompted an allocation trace; it does not identify the
source of the peak or establish a memory leak.

The before-change trace used dotnet-trace's gc-verbose profile, a 64 MiB trace
buffer, DOTNET_PROCESSOR_COUNT=8, and DOTNET_GCHeapHardLimit=0x100000000
(4 GiB). All 3,019 tests passed. Sampled allocation estimates attributed
684.6 MiB to cache-factory delegates and 727.9 MiB to allocations whose first
fsdb stack frame was Expression.tryLiteralDiagnostic. The maximum reported
post-GC generation-size total was 2,414.2 MiB.

The trace demonstrates an avoidable allocation source. It does not prove that
this source accounts for the earlier 33.7G physical-footprint peak. The trace
uses a managed-heap cap, and its GC generation sizes are a different measure
from macOS physical footprint. Other substantial allocation paths include
packets, parser work, test comparer checks, and index comparisons. No overall
memory-peak reduction or latency improvement is claimed here.

## Reproduction and validation

Apple M2 Max, Darwin 24.6.0 arm64, .NET SDK 10.0.401, Debug builds.
The allocation benchmark uses DOTNET_PROCESSOR_COUNT=4. Before and after run
sequentially against separate assemblies; only the script's assembly reference
changes. The before assembly retains the original GetValue-only method and
has SHA-256:
`28852c6f37966e09636b9b24c24d1bbc8e8fee19f9bd455d59052dc97595a5c8`.

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --exec benchmarks/scripts/literal-binding-allocations.fsx
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 just check
```

The final gate passed 3,019 tests with zero warnings or errors under the
explicit managed-heap cap. Native MySQL differential contracts passed 49 cases
and 5,117 steps with zero differences. These validation runs are not timing
comparisons.

## Raw allocation measurements

Before:

```text
valid literal iterations=1000000 control-bytes=0 lookup-bytes=64000000 excess-bytes=64000000
invalid collation iterations=1000000 control-bytes=48000000 lookup-bytes=112000000 excess-bytes=64000000
```

After:

```text
valid literal iterations=1000000 control-bytes=0 lookup-bytes=0 excess-bytes=0
invalid collation iterations=1000000 control-bytes=48000000 lookup-bytes=48000000 excess-bytes=0
```
