# Ordered optimizer-hint parser snapshot

Measured on 2026-10-08 on arm64 macOS, .NET SDK 10.0.401, Debug build,
with the ordered-parser cleanup based on `cfd3f070`.
`DOTNET_PROCESSOR_COUNT=8`, `DOTNET_GCHeapHardLimit=0x100000000`.
No other build, test, oracle, or benchmark ran during measurement.

Command: `dotnet fsi --nologo benchmarks/scripts/hint-handling.fsx`.
The script checks results and performs 100 warm-ups followed by five samples
of 2,000 in-memory QueryHandler calls. Network and durability are excluded.
Times below are sample medians; allocations use the final sample and include
harness costs. Raw samples are in `cfd3f070-ordered-hints.csv`.

| Query | Median ms / 2,000 | Bytes / call |
|---|---:|---:|
| plain | 190.079 | 209264.24 |
| timeout | 203.722 | 216816.74 |
| combined | 201.774 | 220808.86 |

This is a current-state snapshot, not a controlled before/after comparison.
Early samples show JIT warm-up. The tokenizer allocates tokens for hinted
queries; the plain-query fast path remains. These results do not establish
server throughput or a performance improvement.

Validation: 3,096 tests and the existing 68-case / 8,496-step native MySQL
8.4.11 wire suite pass. Table-hint name resolution remains an open gap.
