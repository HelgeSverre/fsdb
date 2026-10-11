# Synchronous trigger executor and point-lookup allocation

At commit `bf584266`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). QueryHandler registered a trigger-session callback in an
`AsyncLocal` scope for every handled statement. Trigger statements run
synchronously inside that call. The candidate uses an exception-restoring
`ThreadLocal` scope for this internal callback.

Each action warmed 1,000 calls and recorded five trials of 1,000 calls.
The table uses the median of trials 2–5; the [raw CSV](2026-10-11-thread-scoped-trigger-executor.csv)
retains every trial.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,800 | 34,800 | 0 |
| Warmed text handler | 81,766 | 80,498 | −1,268 |
| Reused prepared handler | 88,112 | 86,384 | −1,728 |

Median latencies were 26.06 → 24.13 µs for the executor, 97.32 → 86.27 µs
for the text handler, and 80.52 → 72.94 µs for the prepared handler. The
short trials drift substantially, so they do not establish a latency gain.
The handler allocation decrease is the supported result. These actions
include different work and are not an additive stage breakdown.

A focused regression runs two sessions with different triggers and session
variables concurrently. Existing trigger tests cover nested statements,
handled errors, and rollback. The full suite passes at the candidate revision.
The broader MySQL-relative fixed-cost gap remains open.
