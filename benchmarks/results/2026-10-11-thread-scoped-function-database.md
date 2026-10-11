# Synchronous function-database scope and point-lookup allocation

At commit `5498cf6d`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). QueryHandler entered an `AsyncLocal` function-database scope
for each handled statement. A candidate uses an exception-restoring
`ThreadLocal` scope on that synchronous path. The public
`withFunctionDatabase` function retains `AsyncLocal` flow into tasks.

Each action warmed 1,000 calls and recorded five trials of 1,000 calls.
The table uses the median of trials 2–5; the [raw CSV](2026-10-11-thread-scoped-function-database.csv)
retains every trial.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,900 | 34,900 | 0 |
| Warmed text handler | 83,886 | 82,462 | −1,424 |
| Reused prepared handler | 91,918 | 88,714 | −3,204 |

The median latencies were 22.03 → 23.65 µs for the executor, 95.33 →
95.09 µs for the text handler, and 84.18 → 81.40 µs for the prepared
handler. The short trials drifted substantially, so they do not establish
a latency change. The allocation decrease is the supported result. These
actions include different work and are not an additive stage breakdown.

Focused regressions check two concurrent sessions resolving the same
unqualified function name in different schemas, and confirm the public
scope still reaches a delayed task. The full suite passes at the candidate
revision. Other statement scopes and the broader MySQL-relative fixed-cost
gap remain open.
