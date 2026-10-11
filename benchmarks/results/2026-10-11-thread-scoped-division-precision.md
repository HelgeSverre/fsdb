# Synchronous division-precision scope and point-lookup allocation

At commit `9476e744`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). Prepared statement metadata and execution entered an
`AsyncLocal` division-precision scope. The candidate uses an
exception-restoring `ThreadLocal` scope on those synchronous QueryHandler
paths. The public scope still flows into delayed tasks.

Each action warmed 1,000 calls and recorded five trials of 1,000 calls.
The table uses the median of trials 2–5; the [raw CSV](2026-10-11-thread-scoped-division-precision.csv)
retains every trial.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,924 | 34,936 | +12 |
| Warmed text handler | 81,132 | 80,911 | −221 |
| Reused prepared handler | 86,851 | 85,887 | −964 |

The pre-parsed executor does not enter the prepared QueryHandler scope;
its 12-byte difference is process variation. Median latencies were
23.65 → 23.93 µs for the executor, 99.93 → 92.06 µs for the text handler,
and 76.69 → 76.72 µs for the prepared handler. Short trials drift, so they
do not establish a latency change. The prepared-handler allocation decrease
is the supported result. These actions include different work and are not an
additive stage breakdown.

Prepared-statement tests cover session precision, prepare-time retention,
and repreparation. A focused regression confirms that the public scope still
reaches a delayed task. The full suite passes at the candidate revision.
The broader MySQL-relative point-lookup cost gap remains open.
