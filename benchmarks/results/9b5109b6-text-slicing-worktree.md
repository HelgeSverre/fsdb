# Unicode text slicing: avoid scanning unused input

Measured on 2026-10-08 with .NET SDK 10.0.401, Debug build, on the same
host. Both variants are uncommitted worktrees based on `9b5109b6`: the first
correct Unicode slicing implementation and its bounded-scan refinement.
This is not a comparison with the committed implementation, which sliced
UTF-16 code units incorrectly for supplementary characters.

`DOTNET_PROCESSOR_COUNT=8`, `DOTNET_GCHeapHardLimit=0x100000000`.
No concurrent build, test, native oracle, or benchmark ran during measurement.
The script invokes built-in scalar functions directly; SQL parsing, storage,
networking, and durability are outside this measurement.

Each input consists of ASCII `a` characters. LEFT and RIGHT request one
character; SUBSTRING requests one character at position two. Each sample has
100 calls, after ten warm-up calls; times are medians of five samples.
Allocation is bytes per call from the final sample, including loop/harness
cost. Results are kept alive. Run `dotnet fsi benchmarks/scripts/text-slicing.fsx`
after building the Debug assembly.

| Function | Input characters | Before ms / 100 | After ms / 100 | Before bytes / call | After bytes / call |
|---|---:|---:|---:|---:|---:|
| LEFT | 1,024 | 0.5369 | 0.0287 | 242 | 242 |
| SUBSTRING | 1,024 | 0.4700 | 0.0412 | 306 | 274 |
| RIGHT | 1,024 | 1.0230 | 0.0235 | 242 | 170 |
| LEFT | 1,000,000 | 301.4322 | 0.0273 | 242 | 242 |
| SUBSTRING | 1,000,000 | 301.3874 | 0.0375 | 306 | 274 |
| RIGHT | 1,000,000 | 744.8615 | 0.0234 | 242 | 170 |

The initial implementation counted all Unicode scalars before selecting a
range, then scanned again to locate UTF-16 offsets. Positive positions now
scan forward only to the requested boundary; negative positions and RIGHT
scan backward from the end. Both respect surrogate pairs. Tiny after-times
are noisy; the useful result is removing dependence on the unused input
length for these short slices, not a general SQL throughput claim.

The root gate passed 3,082 tests after the refinement, including native-backed
UTF8MB4/GB18030 supplementary-character, negative-position, zero-position,
and empty-slice controls. The subsequent full wire run passed 62 cases and 8,285 steps without differences.
