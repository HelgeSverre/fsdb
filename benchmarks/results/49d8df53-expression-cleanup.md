# Expression validation cleanup comparison

Measured 2026-10-08 on arm64 macOS, .NET SDK 10.0.401, Debug assemblies.
The base revision is `49d8df53`. Both versions include the pending integer CAST
and lazy conditional fixes. Before precedes the shared-validator extraction;
after includes the shared validator and INSERT validation-before-evaluation fix.
The benchmark measures SELECT paths, so it does not quantify INSERT overhead.

The harness is `benchmarks/scripts/conditional-evaluation.fsx`. A frozen copy of
the before assembly and its dependencies supplies the baseline. Runs execute
serially in before/after/after/before order, with 8 processors and a 4 GiB GC
heap limit. Each invocation warms each query 200 times and measures five samples
of 2,000 calls. Tables contain one INT row. Every result is checked.
These are in-process, in-memory measurements; network, durability, and native
MySQL throughput are outside this comparison.

| Query | Before µs/call | After µs/call | Before bytes/call | After bytes/call |
|---|---:|---:|---:|---:|
| plain | 103.62 | 101.07 | 217570.17 | 217569.48 |
| signed-cast | 114.38 | 114.96 | 232760.61 | 232760.61 |
| unsigned-cast | 112.42 | 111.05 | 224656.86 | 224656.86 |
| if | 175.77 | 173.09 | 279880.98 | 279944.98 |
| ifnull | 193.29 | 187.97 | 306225.11 | 306289.11 |
| coalesce | 199.35 | 197.65 | 316569.11 | 316633.11 |
| nested | 198.06 | 191.45 | 333121.23 | 333121.23 |

Elapsed medians vary from -3.3% to +0.5% on this shared host. These samples show
no clear timing regression and do not establish a speedup. IF, IFNULL, and
COALESCE allocate 64 additional bytes per call; other measured allocations are
effectively unchanged. Raw samples are in the adjacent CSV files.

Validation: 3,128 root tests pass; native wire comparison passes 83 contracts /
11,408 steps with zero differences. The 107-case CAST replay also matches.

Assembly SHA-256 values:
- Before: `2aca6d386fc1956d01c5853b67ffdcede83bea515ac4d13472cee4430bf82cb0`
- After: `29f4ba6c440fa5b4415de769b738b88d2ce047c91120f29e27e52051b375e102`
