# Thread-scoped execution limits and indexed lookup allocation

At fsdb commit `0f7c94da`, the Release `PointLookupPipeline` runner used its
10,000-row integer primary-key fixture on an Apple M2 Max (Darwin arm64,
.NET 10.0.401). It warmed each action 1,000 times, then ran five trials of
1,000 calls. The table uses the median of trials 2–5; the
[raw CSV](2026-10-11-current-point-lookup-allocation.csv) retains every trial.
Each action verifies the exact one-row result.

```sh
FSDB_PROFILE_ACTION=fixed-executor,fixed-handler,fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=1000 FSDB_PROFILE_TRIALS=5 \
dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release
```

| Action | Median µs/call | Median allocated bytes/call |
|---|---:|---:|
| Pre-parsed executor | 24.69 | 34,895 |
| Warmed text handler | 111.67 | 94,656 |
| Reused prepared handler | 93.88 | 101,757 |

These are separate action timings, not an additive stage breakdown or a
same-process A/B of candidate code. Short latency trials varied noticeably;
the allocation figures were more stable. The prepared action includes its
session and typed-result path and is not directly comparable to the text
handler as if only parsing differed.

A separate 20,000-call prepared-handler run used `dotnet-trace`'s `gc-verbose`
EventPipe profile. The trace is a diagnostic sample, not a byte-exact
allocation ledger. Among its largest sampled object families were
`Entry<IAsyncLocal,Object>[]` (~307 MB), `Session` (~117 MB),
`KeyValuePair<IAsyncLocal,Object>[]` (~103 MB), `ExecutionContext` (~43 MB),
and `ManyElementAsyncLocalValueMap` (~41 MB). The scoped execution context is
therefore a plausible source of the fixed handler allocation. The trace does
not show which individual scope can be safely skipped or establish that
changing scope storage would improve latency.

```sh
FSDB_PROFILE_ACTION=fixed-prepared-handler \
FSDB_PROFILE_ITERATIONS=20000 FSDB_PROFILE_TRIALS=1 \
dotnet-trace collect --profile gc-verbose --output /tmp/fsdb-point-current.nettrace -- \
  dotnet benchmarks/PointLookupPipeline/bin/Release/net10.0/PointLookupPipeline.dll
```

## Scoped execution-limit change

The CTE recursion-depth and `GROUP_CONCAT` length scopes are entered and read
inside synchronous statement execution. They now use the existing
exception-restoring thread-local scope helper, avoiding two `AsyncLocal`
context changes per statement. The same Release runner, with the same warmup,
trial count, and fixture, produced the
[candidate CSV](2026-10-11-thread-scoped-execution-limits.csv).

| Action | Before bytes/call | After bytes/call | Change |
|---|---:|---:|---:|
| Pre-parsed executor | 34,895 | 34,823 | −72 |
| Warmed text handler | 94,656 | 89,922 | −4,734 |
| Reused prepared handler | 101,757 | 97,923 | −3,834 |

The separate-process median latencies were 111.67 → 100.34 µs for text and
93.88 → 86.78 µs for prepared handling. The short trials drifted downward
within each run, so they do not establish a latency improvement. The
allocation decrease is the supported result. The full suite passed 3,394
tests at the candidate revision, and a focused test ran two concurrent
sessions with different aggregate and recursion limits. Existing recursive
CTE and aggregate tests cover the normal and error paths. Other
execution-context scopes and the broader MySQL-relative fixed-cost gap remain
open.
