# Join performance after relation-name validation

Measured 2026-10-07 on main at `750af1d6` with uncommitted relation-name
validation changes. Engine source diff SHA-256:
`b48140f7d89c1e4ec5fafe79b4b7ffa5699eb63947892d557a7f88d58c3f8c7d`.
The preceding full gate passed 2,986 tests, with no failures or ignored tests.

| Query | Previous median ms | Current median ms | Change |
|---|---:|---:|---:|
| Flat inner join | 2.993 | 2.961 | -1.1% |
| Grouped inner join with ON | 187.317 | 190.702 | +1.8% |
| Grouped inner join with USING | 63.648 | 67.493 | +6.0% |
| Local STRAIGHT_JOIN constraint | 3.902 | 3.044 | -22.0% |
| Global STRAIGHT_JOIN constraint | 77.122 | 81.126 | +5.2% |

The previous snapshot is in [grouped join measurements](869a4b6c-grouped-joins-wip.md).
These sequential snapshots do not isolate the cost of relation-name validation
from host noise or intervening changes. Local STRAIGHT_JOIN had noisy previous
samples. Grouped ON remains approximately 64 times the flat-query latency;
grouped USING is approximately 23 times. Both remain profiling targets.

## Method

Embedded Debug build, in-memory, Apple M2 Max, Darwin 24.6.0 arm64,
.NET SDK 10.0.401, DOTNET_PROCESSOR_COUNT=4. Background applications active.
Fixture: 500 base rows, 25,000 indexed fan-out rows, one selective row.
Five warmups, seven batches of ten executions per query. Grouped-join script
runs first, then the straight-join script; no overlapping test or benchmark
processes. Every grouped benchmark execution verifies all 50 expected rows.
Results are median batch averages, not individual latency percentiles.
No MySQL timing comparison or durable write claim is made.

```sh
DOTNET_PROCESSOR_COUNT=8 MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 just check
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/grouped-joins.fsx
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/straight-join-order.fsx
```

## Raw batch averages (ms)

```text
flat inner rows=50 median-ms=2.961 samples=[3.03309; 2.94471; 7.28213; 2.95725; 2.88327; 2.96147; 3.00849]
grouped inner rows=50 median-ms=190.702 samples=[190.93676; 192.94831; 193.03427; 189.4596; 188.75118; 189.7681; 190.70201]
grouped USING rows=50 median-ms=67.493 samples=[67.25247; 67.98935; 67.49253; 67.97703; 70.97305; 66.75518; 66.12397]
table constraint rows=50 median-ms=3.044 samples=[3.00185; 3.00317; 7.07332; 3.13601; 3.14794; 3.04375; 3.02283]
global constraint rows=50 median-ms=81.126 samples=[84.10869; 80.90086; 80.89897; 80.65202; 81.12594; 83.67505; 84.62078]
```
