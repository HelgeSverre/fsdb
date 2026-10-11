# Synchronous stored-function session scope and point-lookup allocation

At commit `449f695c`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). QueryHandler entered an `AsyncLocal` stored-function session
scope for every parsed statement, including statements that call no stored
function. A candidate uses an exception-restoring `ThreadLocal` scope for
this internal synchronous execution path. Nested stored functions replace
and restore the same slot.

Each action warmed 1,000 calls and recorded five trials of 1,000 calls.
The table uses the median of trials 2–5; the [raw CSV](2026-10-11-thread-scoped-stored-function-session.csv)
retains every trial.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,912 | 34,776 | −136 |
| Warmed text handler | 81,016 | 79,495 | −1,521 |
| Reused prepared handler | 85,848 | 84,595 | −1,253 |

The pre-parsed executor does not enter this QueryHandler scope; its small
difference is process variation. Median latencies were 24.73 → 20.56 µs for
the executor, 94.97 → 81.30 µs for the text handler, and 74.33 → 69.28 µs
for the prepared handler. These short trials drift, so they do not establish
a latency gain. The handler allocation decrease is the supported result.
These actions include different work and are not an additive stage breakdown.

Existing regressions cover nested stored functions, errors followed by a
separate session, and concurrent sessions resolving same-named functions in
different schemas. The full suite passes at the candidate revision. The
broader MySQL-relative fixed-cost gap remains open.
