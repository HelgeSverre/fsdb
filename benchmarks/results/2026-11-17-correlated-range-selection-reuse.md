# Rejected experiment: reuse the range selection

Status: rejected; engine source restored to `863b285c` without this change.
The range throughput regression from the preceding checkpoint remains open.

A sampled thread-time trace of the range workload showed
`rangePreferredToEstimatedRef` at 30.42% inclusive and the projected physical
range selection stack at 29.75%. These are overlapping sampled stack shares,
not additive CPU percentages. The trace included process startup and GC
threads; its exclusive view was dominated by GC polling and memory-pressure
workers. It provided a direction for investigation, not a precise allocation
or CPU-cost attribution.

The [candidate patch](2026-11-17-correlated-range-selection-reuse.patch)
reused the initial immutable range selection during refinement instead of
calling the projected range planner again. It retained both diagnostic
phase records and materialized the same candidate rows.

## Results

Three alternating before/candidate pairs used the same benchmark, host,
Release configuration, data, warmup, and eight-second sample duration as the
[preceding checkpoint](2026-11-17-correlated-range-ref-estimate.md).
All result-row invariants passed. Raw samples and order are in the
[sample JSON](2026-11-17-correlated-range-selection-reuse.json).

| Workload | Before median queries/sec | Candidate median queries/sec | Before median bytes/query | Candidate median bytes/query |
| --- | ---: | ---: | ---: | ---: |
| Equality control | 3,756 | 3,556 | 661,066 | 661,316 |
| Range | 617 | 713 | 3,249,941 | 3,238,608 |

The range throughput span was 554–753 before and 454–764 for the candidate.
The paired changes were approximately −18%, −5%, and +24%. Host throughput
was more variable than in the preceding experiment, so the median increase
does not establish a consistent benefit. Range allocation changed by only
−0.35%, far short of the preceding approximately 60% allocation regression.

The candidate was removed. Its profile, measurements, and exact patch are
retained here so this experiment need not be repeated. No production change
was committed and no claim is made that the performance regression is fixed.
The next investigation should attribute allocated bytes to candidate/context
preparation and repeated binding, rather than assuming the second selection
call explains the regression.

Profiling command (dotnet-trace 10.0.731102):

```sh
dotnet-trace collect --profile dotnet-sampled-thread-time --output /tmp/range.nettrace -- \
  dotnet benchmarks/CorrelatedRangeProfile/bin/Release/net10.0/CorrelatedRangeProfile.dll range 12
dotnet-trace report /tmp/range.nettrace topN -n 400 --inclusive
```

The temporary trace, copied baseline binaries, and experimental Release
outputs were removed after analysis. The regular correctness gate was not
rerun for a rejected candidate; production source exactly matches HEAD.
