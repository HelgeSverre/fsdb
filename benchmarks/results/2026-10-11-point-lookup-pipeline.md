# Current point-lookup pipeline profile

At `743fdd63` on an Apple M2 Max (Darwin arm64, .NET 10.0.401), the
project-backed `PointLookupPipeline` runner measured a 10,000-row integer
primary-key table in Release mode. Each action had 1,000 warmups and five
trials of 2,000 calls. The table uses the median of trials 2–5; the
[raw CSV](2026-10-11-point-lookup-pipeline.csv) includes all trials. Each
executor and handler action verifies the exact one-row result.

```sh
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Predicate | Parse µs / KiB | Pre-parsed executor µs / KiB | Text handler µs / KiB |
|---|---:|---:|---:|
| `id=3` | 26.32 / 68.3 | 13.45 / 37.1 | 49.62 / 109.2 |
| `id=ROUND(PI())` | 38.51 / 101.7 | 18.10 / 46.1 | 57.68 / 125.8 |
| `id=BIT_COUNT(7)` | 34.40 / 95.3 | 16.62 / 43.7 | 55.12 / 119.9 |

These are separate action timings, so the stage figures are not an additive
breakdown of a single request. First trials were much slower, and the same
runner's preceding uncaptured run varied noticeably; this is a diagnostic
profile, not a code-change speedup claim. Compared with the
[2026-10-10 profile](2026-10-10-point-lookup-pipeline.md), the fixed handler
is lower in this run, but the runtime, host activity, and intervening commits
prevent attribution.

The independent `EmbeddedApiPerf` harness also passed its typed-result,
changing-parameter, NULL, missing-row, and preparation-database checks. Two
short runs put reused prepared `QueryValues` at about 76–80 µs and 126 KiB
per call on its separate 1,000-row, three-column fixture. It cannot be
subtracted from the stage timings above. The parser action measures an
uncached standalone parse. The warmed text handler uses QueryHandler's
parsed-statement cache, so parser time is not a component of each repeated
handler call. General execution and statement handling remain the relevant
fixed-cost targets; the prepared typed-result path needs its own narrower
stage profile before another optimization.

## Discarded parser shortcut

A same-runner follow-up skipped the version-comment rewrite when SQL lacked
`/*!`. The [raw candidate run](2026-10-11-point-lookup-comment-fastpath.csv)
showed 616–728 fewer allocated bytes per isolated parse than the baseline
above. Text-handler allocation was unchanged for all three predicates; its
timing varied upward in this short run. The shortcut was removed because it
did not address the measured request cost. This comparison is diagnostic,
not a controlled latency A/B under isolated host load.
