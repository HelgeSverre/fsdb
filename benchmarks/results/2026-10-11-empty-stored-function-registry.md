# Empty stored-function registry and point-lookup allocation

At commit `02ef8726`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). `withStoredFunctionRegistry` decorated every session with a
temporary registry and restored the original registry afterward, including
when the stored-function catalog was empty. The candidate executes directly
with the original session in that empty case. It scans the catalog once and
retains the existing decoration and restoration when functions exist.

Each action warmed 1,000 calls and recorded five trials of 1,000 calls.
The table uses the median of trials 2–5; the [raw CSV](2026-10-11-empty-stored-function-registry.csv)
retains every trial.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,936 | 34,800 | −136 |
| Warmed text handler | 82,527 | 81,321 | −1,206 |
| Reused prepared handler | 88,829 | 87,827 | −1,002 |

The pre-parsed executor does not enter this QueryHandler wrapper, so its small
change is process variation, not an effect attributable to the candidate.
Median latencies were 23.08 → 24.02 µs for the executor, 93.69 → 83.49 µs
for the text handler, and 80.26 → 72.78 µs for the prepared handler. These
short runs drift enough that they do not establish a latency gain. The
handler allocation decrease is the supported result. Actions include
different work and are not an additive stage breakdown.

Existing stored-function tests exercise populated catalogs, nested function
calls, prepared metadata, and caller-variable propagation. The full suite
passes at the candidate revision. The broader MySQL-relative point-lookup
cost gap remains open.
