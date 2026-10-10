# Point-bound in-process probe

<!--
code sha: 64e8d3ab
date: 2026-10-10T12:39:00Z
host: Darwin 24.6.0 arm64, Apple M2 Max
dotnet: 10.0.401
-->

Release-built fsdb runs in one F# Interactive process through
`QueryHandler.handle`, without the wire protocol. A table has 10,000 integer
primary-key rows. `EXPLAIN` reports `const` for `id=7500` and `id=7500+0`,
and `ALL` for the `id+0=7500` scan control. Each case gets 40 untimed
warmups. Eight interleaved trials start after a full GC; the table excludes
the first two trials to reduce JIT effects.

| Predicate | Median time/query | Median allocation/query |
|---|---:|---:|
| `id=7500` | 85.94 µs | 251.8 KiB |
| `id=7500+0` | 86.20 µs | 254.9 KiB |
| `id+0=7500` | 3.08 ms | 6.50 MiB |

The literal and constant-expression indexed probes are within 0.3 µs in
this process. This rules out a large constant-folding cost in this query
shape, but does not locate the remaining wire-level latency. The scan
control additionally performs arithmetic on each row, so its ratio is not
a pure index speedup.

A [paired native wire quick run](64e8d3ab-quick.md) on the same revision
measured fsdb at 465.11 µs for a literal point lookup and 470.59 µs for a
constant-expression lookup; MySQL measured 44.35 and 44.97 µs. The two
fsdb shapes are close there as well. The in-process probe has a simpler
one-column table, so subtracting its time from the wire run would not
isolate protocol cost.

Raw samples cover all iterations in each trial:

```csv
case,trial,iterations,elapsed_ms,allocated_bytes
literal,1,1500,353.033,394833824
constant_expression,1,1500,247.944,399516480
forced_scan,1,15,110.109,119063136
literal,2,1500,184.983,391383512
constant_expression,2,1500,169.564,395615944
forced_scan,2,15,52.907,102228760
literal,3,1500,140.477,388172568
constant_expression,3,1500,132.814,391636728
forced_scan,3,15,46.881,102206880
literal,4,1500,131.060,386784480
constant_expression,4,1500,127.547,391596480
forced_scan,4,15,44.844,102206520
literal,5,1500,126.212,386784480
constant_expression,5,1500,130.008,391596480
forced_scan,5,15,46.190,102206520
literal,6,1500,126.989,386784480
constant_expression,6,1500,128.206,391596480
forced_scan,6,15,46.188,102206520
literal,7,1500,128.337,386784480
constant_expression,7,1500,128.600,391596480
forced_scan,7,15,45.793,102206520
literal,8,1500,129.488,386784480
constant_expression,8,1500,136.547,391596728
forced_scan,8,15,46.807,102206520
```
