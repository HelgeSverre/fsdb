# Embedded function-registry cache

At parent commit `2efaf3e5` on Darwin arm64 with .NET 10.0.401, an independent
validator in `benchmarks/EmbeddedApiPerf` checked typed results for changing
parameters, SQL NULL, missing rows, and prepared database binding before
timing. Five alternating 500-call Release trials then measured the baseline.
The same validator, build mode, host, and trial count measured the change after
`just check` and `just package-check` completed.

| In-process lookup | Before µs/op | After µs/op | Before B/op | After B/op |
|---|---:|---:|---:|---:|
| Literal text `Query` | 105.1 | 87.8 | 252,831 | 125,833 |
| Per-call `Execute` | 190.3 | 131.5 | 554,230 | 235,907 |
| Per-call `QueryValues` | 193.0 | 128.6 | 553,761 | 236,097 |
| Reused `PreparedCommand.Execute` | 94.0 | 73.7 | 321,572 | 132,120 |
| Reused `PreparedCommand.QueryValues` | 94.9 | 67.5 | 321,621 | 131,993 |

Before the change, a `dotnet-trace` run of 20,000 prepared typed lookups found
`QueryHandler.registryFor` on about 44% of sampled call-stack time, with F#
map rebalancing prominent. The new cache reuses that registry only when its
session inputs are unchanged. Functions capture the small values they need,
so the cached registry does not retain an old transaction snapshot. The
independent validator and a regression covering database, generated-ID, and
time-zone changes passed after the change.

These are directional same-process API measurements. They exclude connection
creation and preparation, and they do not measure server throughput or compare
with MySQL. The remaining roughly 132 KB per prepared lookup warrants another
profile before further optimization.
