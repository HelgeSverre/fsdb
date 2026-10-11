# Synchronous variable scopes and point-lookup allocation

At commit `164b0c9a`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). A candidate gave QueryHandler an exception-restoring
`ThreadLocal` path for the executor's variable context and the
interval-arithmetic time zone. Both scopes enclose synchronous expression
execution. The public scope functions retain their `AsyncLocal` behavior for
callers that flow context into tasks.

For each action, the runner warmed 1,000 calls and recorded five trials of
1,000 calls. The table uses the median of trials 2–5. The [combined raw
CSV](2026-10-11-thread-scoped-variable-context.csv) retains all trials.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,796 | 34,684 | −112 |
| Warmed text handler | 90,200 | 83,720 | −6,480 |
| Reused prepared handler | 98,090 | 91,906 | −6,184 |

The median latencies were 22.37 → 23.84 µs for the executor, 95.03 →
100.57 µs for the text handler, and 81.97 → 87.46 µs for the prepared handler.
The short runs drifted substantially, so they do not establish a latency
change. The handler allocation decrease is the supported result; the
pre-parsed executor is effectively unchanged. The text and prepared actions
include different handler work and should not be treated as an additive
stage breakdown.

A focused regression runs two sessions concurrently with different user
variables and opposite session time zones. It repeats time-zone-sensitive
`DATE_ADD` calls and checks that each session retains its own values. Another
regression checks that the public variable scope still flows into a delayed
task. The full suite passes at the candidate revision. Other execution-context
scopes and the broader MySQL-relative fixed-cost gap remain open.
