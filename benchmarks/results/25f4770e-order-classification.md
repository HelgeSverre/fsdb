# Aggregate ordering classification cost

Implementation based on `25f4770e`, adding query-scope aggregate ordering
validation and shared preparation/execution diagnostics.

The [ordering-alias probe](../scripts/order-aliases.fsx) uses 10,000 input rows,
LIMIT 100, five warmups, and nine alternating batches of five verified
executions. Embedded in-memory Debug engine, .NET SDK 10.0.401, Darwin 24.6
arm64, eight logical processors, 4 GiB GC heap cap. No concurrent fsdb builds,
tests, or other profiles. Background applications remain active.

| Query | Previous bytes/query | Current bytes/query | Current median ms |
|---|---:|---:|---:|
| Source expression | 60,234,766 | 60,235,286 | 76.694 |
| Projection alias | 60,234,902 | 60,235,422 | 93.679 |
| Correlated alias | 241,045,904 | 242,889,104 | 246.146 |

The [preceding ownership measurement](d2fed802-aggregate-scope-binding.md)
provides the control. Correlated allocation increases by 1,843,200 bytes/query
(0.76%); the ordinary controls add 520 bytes/query. Per-query validation setup
remains a possible optimization target. Timing varies substantially across
batches and ordinary controls, so these samples do not isolate a latency change.
This is not a MySQL speed comparison or a durable-write measurement.

```sh
DOTNET_PROCESSOR_COUNT=8 DOTNET_GCHeapHardLimit=0x100000000 \
  dotnet fsi --nologo benchmarks/scripts/order-aliases.fsx
```

```text
source input=10000 output=100 median-ms=76.694 median-bytes=60235286 samples-ms=[67.57752; 96.46198; 90.55348; 71.68858; 64.55056; 104.82302; 105.77586;
 70.57056; 76.69444]
alias input=10000 output=100 median-ms=93.679 median-bytes=60235422 samples-ms=[64.31072; 101.42818; 96.11474; 113.7393; 70.2913; 93.67878; 107.02336; 72.51036;
 79.77884]
correlated-alias input=10000 output=100 median-ms=246.146 median-bytes=242889104 samples-ms=[224.69612; 302.28404; 303.51202; 234.38598; 231.86798; 292.08728; 334.53098;
 227.30734; 246.14602]
```
