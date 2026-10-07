# Join performance with literal binding validation

Measured 2026-10-08 on main at `d0ecd694` with literal binding validation
changes. Engine source diff SHA-256:
`c3360297b76fd04a7cd6744481f7c8203444fc8aff61e06c593822b8b3ef59ac`.
The full gate passed 3,019 tests with zero warnings or errors. Differential
contracts passed 49 cases and 5,117 steps with zero differences.

| Query | Changed first, ms | Unmodified HEAD, ms | Changed repeat, ms |
|---|---:|---:|---:|
| Flat inner join | 5.342 | 6.519 | 5.547 |
| Grouped inner join with ON | 377.296 | 424.915 | 368.060 |
| Grouped inner join with USING | 126.770 | 140.923 | 85.498 |

Runs are sequential in table-column order. The unmodified control was built
from a separate archive of the same HEAD. These observations do not establish
a speedup or isolate validation overhead: the repeated USING median changes
substantially, and individual grouped ON batches span 286–438 ms. They do show
that the slower absolute timings relative to the
[earlier snapshot](750af1d6-relation-names-wip.md) also occur without this patch.
Grouped ON remains roughly 66–71 times the flat query's median in the changed
build; grouped-source preparation remains a profiling target.

## Method

Embedded Debug build, in-memory, Apple M2 Max, Darwin 24.6.0 arm64,
.NET SDK 10.0.401, DOTNET_PROCESSOR_COUNT=4. Background applications active.
Fixture: 500 base rows, 25,000 indexed fan-out rows, one selective row.
Five warmups, seven batches of ten executions per query; each execution checks
all 50 expected rows. No overlapping fsdb builds, tests, or benchmark processes.
Results are median batch averages, not latency percentiles. No MySQL timing
comparison or durable write claim is made.

```sh
DOTNET_PROCESSOR_COUNT=4 dotnet fsi --nologo benchmarks/scripts/grouped-joins.fsx
```

## Raw batch averages (ms)

### Changed build, first run

```text
flat inner rows=50 median-ms=5.342 samples=[5.06832; 6.0241; 13.84475; 4.5407; 5.34196; 5.73932; 4.75742]
grouped inner rows=50 median-ms=377.296 samples=[374.39084; 377.8501; 370.6429; 377.29574; 386.34273; 373.37283; 399.73704]
grouped USING rows=50 median-ms=126.770 samples=[125.35934; 122.12128; 134.99505; 128.69787; 129.59094; 126.76989; 124.43233]
```

### Unmodified HEAD control

```text
flat inner rows=50 median-ms=6.519 samples=[6.00428; 5.81871; 21.21552; 7.59267; 4.45861; 6.51927; 9.2815]
grouped inner rows=50 median-ms=424.915 samples=[410.76674; 402.17432; 425.19113; 424.91459; 443.79048; 400.07622; 440.28585]
grouped USING rows=50 median-ms=140.923 samples=[127.23463; 133.45109; 142.30673; 141.7653; 140.92261; 143.54675; 127.99368]
```

### Changed build, repeat

```text
flat inner rows=50 median-ms=5.547 samples=[6.30513; 4.30993; 15.24456; 5.06977; 5.54725; 5.45522; 5.83762]
grouped inner rows=50 median-ms=368.060 samples=[286.42862; 347.58375; 437.91281; 402.11303; 383.1972; 368.05952; 351.63454]
grouped USING rows=50 median-ms=85.498 samples=[130.72062; 116.59573; 85.49756; 76.48406; 81.80092; 92.27072; 85.1203]
```
