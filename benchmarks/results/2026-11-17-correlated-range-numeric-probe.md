# Numeric DOUBLE probe allocation reduction

Finite numeric probes into unformatted signed DOUBLE columns now avoid a
diagnostic-suppression scope whose conversion cannot warn. Existing exactness
checks still run after conversion. Unsigned columns, display-rounded columns,
strings, nonfinite doubles, and other types retain the existing coercion path.

## Allocation evidence

A twelve-second `gc-verbose` trace of the range benchmark at `e66852e5`
contained 238,368 allocation ticks representing approximately 25.4 GB of
sampled allocation. It included setup, warmup, and the timed workload; it
is attribution evidence, not an uninstrumented throughput measurement.
The [profile summary](2026-11-17-correlated-range-allocation-profile.json)
records type and stack aggregates. Inclusive stack shares overlap.

- Approximately 63% of sampled bytes had correlated source access on the stack.
- The estimated-ref/range helper appeared under approximately 37%.
- `Storage.exactProbeValue` was the nearest recorded engine frame for 12.7%.
- AsyncLocal map arrays under that probe accounted for approximately 9.3%.

The first experiment skipped coercion only for an already typed DOUBLE.
It did not reduce this workload's allocations because ordinary literal bounds
arrive as DECIMAL or integer values. Its
[samples](2026-11-17-correlated-range-double-probe.json) and
[patch](2026-11-17-double-only-probe.patch) are retained as an intermediate
experiment. The retained implementation also handles signed integer,
unsigned integer, and DECIMAL inputs for a signed DOUBLE column without
numeric display formatting, using the same numeric conversions as coercion.

## Matched comparison

The [benchmark protocol](2026-11-17-correlated-range-ref-estimate.md), host,
data, Release configuration, warmup, and alternating three eight-second
samples per revision/workload were retained. All result checks passed.
The before binary was copied from `e66852e5` before rebuilding the candidate.
[Raw samples](2026-11-17-correlated-range-numeric-probe.json) include order,
query counts, elapsed time, allocations, and derived rates.

| Workload | Before median queries/sec | After median queries/sec | Before bytes/query | After bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 3,644 | 3,648 | 658,377 | 660,704 |
| Range | 743 | 795 | 3,233,232 | 2,826,681 |

Range allocation fell 12.6%; all candidate samples were near 2.82–2.83 MB
per query, compared with 3.23–3.24 MB before. Median range throughput rose
7.0%, but one unchanged equality-control sample also suffered a substantial
throughput drop. Treat allocation savings as the stronger result and timing
as a local observation under uncontrolled background load.

This recovers part of the preceding planner regression. It does not establish
full recovery relative to `bf57ee65`, nor MySQL performance or durable-write
parity. Remaining allocation and planning costs still need investigation.

## Reproducing attribution

The [trace reader](../AllocationTraceReader/Program.cs) uses pinned TraceEvent
3.1.23, matching dotnet-trace 10.0.731102 used here. It aggregates allocation
ticks by type, inclusive engine frame, nearest engine frame, and AsyncLocal
array stack. Its output was compared exactly with the temporary reader's
output on the same trace.

```sh
dotnet-trace collect --profile gc-verbose --output /tmp/range.nettrace -- \
  dotnet benchmarks/CorrelatedRangeProfile/bin/Release/net10.0/CorrelatedRangeProfile.dll range 12
dotnet run --project benchmarks/AllocationTraceReader -- /tmp/range.nettrace /tmp/range-allocations.json
```

This analysis is sampled: nearest-frame attribution can be affected by
inlining and missing frames, and individual object-type proportions are
estimates. The before/after benchmark uses the separate process-wide allocated
byte counter, not allocation ticks. Temporary trace files and copied binaries
were removed after analysis.

Validation: the full `just check` gate passed all 3,954 tests, including the
correlated diagnostics matrices and broad-candidate intersection regression.
The trace-reader project built successfully and reproduced the saved summary.

A follow-up [empty view-catalog check](2026-11-17-correlated-range-empty-view-catalog.md)
reduced range allocations by a further 7.5% in its matched comparison.
