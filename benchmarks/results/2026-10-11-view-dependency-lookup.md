# Avoid view-catalog scans for physical-table reads

The warm point-lookup fixture still allocated roughly 90–100 KiB per call.
A `gc-verbose` EventPipe trace of reused prepared lookups identified
`TableLocks.expandDependencies` in the hot statement path. It constructed a
map of all stored views before checking an ordinary physical-table access.
This was unnecessary: a physical table and a view cannot share one name in
the same database.

`expandDependencies` now probes the physical-table snapshot first and loads
the view catalog only when an access is not a physical table. The same rule
applies to explicit `LOCK TABLES` resolution. View references still expand
their base-table dependencies, including nested views. The existing
`view locks include base tables without exposing their names` regression
exercises that path.

The same 10,000-row `PointLookupPipeline` fixture measured cached text and
reused prepared `id=3` lookups in two separate Release processes on the
same Apple M2 Max host. Each had 1,000 warmups, eight paired trials, and
2,000 calls per trial. [Raw trials](2026-10-11-view-dependency-lookup.csv)
include the warmup drift. The table uses the stable final two trials:

| Action | Before B/call | After B/call | Difference |
|---|---:|---:|---:|
| Cached text handler | 91,512 | 90,328 | −1,184 |
| Reused prepared handler | 98,931 | 97,122 | −1,809 |

The final two trial latencies also differ, but separate short process runs
do not establish a latency improvement. The residual allocation and the
broader MySQL-relative point-lookup gap remain open. The EventPipe trace was
a diagnostic lead rather than a per-site allocation measurement.
