# Reused prepared point lookup on the same fixture

The project-backed `PointLookupPipeline` runner now measures a reused
`SELECT id FROM lookup_profile WHERE id=?` statement through
`QueryHandler.executePrepared`. It binds `3` on every call and checks the exact
one-row result. The comparison action executes the cached text statement
`SELECT id FROM lookup_profile WHERE id=3` through `QueryHandler.handle` on
the same session and 10,000-row primary-key table. Neither action includes
SQL parsing, wire encoding, or the embedded `Db.QueryValues` conversion.

```sh
FSDB_PROFILE_ACTION=fixed-handler,fixed-prepared-handler FSDB_PROFILE_TRIALS=12 \
  dotnet run --project benchmarks/PointLookupPipeline/PointLookupPipeline.fsproj -c Release --no-restore
```

The [raw run](2026-10-11-prepared-point-lookup.csv) has 1,000 warmups per
action and twelve paired trials of 2,000 calls on Apple M2 Max, Darwin
arm64, .NET 10.0.401. Trials 1–4 improved steeply as the process warmed. The
table uses the median of trials 5–12; these are diagnostic figures, not a
controlled speedup claim.

| Action | µs/call | KiB/call |
|---|---:|---:|
| Cached text handler | 49.88 | 109.30 |
| Reused prepared handler | 47.15 | 116.48 |

Prepared execution is slightly faster in this run, but allocates about 7.2
KiB more per call. The difference is small relative to trial timing variation.
Both paths retain roughly 100 KiB of allocation for a one-row lookup. The
next useful investigation is an allocation profile inside statement handling
and execution; a shortcut justified only by parsing time would miss this
warmed workload. The separate `EmbeddedApiPerf` typed-result measurements
remain necessary to assess the public `Db.QueryValues` boundary.
