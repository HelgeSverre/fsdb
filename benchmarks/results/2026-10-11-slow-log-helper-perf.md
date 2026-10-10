# Completed-statement slow-log helper: disabled-path check

The cleanup at `ca4a4513` replaced duplicate text and prepared slow-log
completion code with one helper. This comparison checks whether that extra
call changed ordinary embedded query cost when logging is disabled. The
pre-refactor checkout is `a4a84d85`; the after checkout is `225fde1b`, which
also contains subsequent wire-only changes. Both used Release builds of the
same `benchmarks/EmbeddedApiPerf` harness on Darwin 24.6.0 arm64 with .NET
10.0.401. The harness validates changing prepared values, NULL/missing rows,
and prepared database binding before measuring.

Two alternating before/after pairs used five trials of 500 operations per
case. Each cell lists the two run medians; units are microseconds and allocated
bytes per operation. Both checkouts used the default disabled general and slow
logs. Earlier three-trial, 300-operation pairs varied much more in latency,
so they are excluded from the table.

| Query path | Before µs/op | After µs/op | Before B/op | After B/op |
|---|---:|---:|---:|---:|
| Literal `Query` | 86.6, 85.0 | 81.7, 90.9 | 117260, 116748 | 116812, 117252 |
| Parameter `Execute` | 132.7, 124.2 | 125.8, 133.5 | 228099, 227331 | 227459, 227773 |
| Parameter `QueryValues` | 138.6, 128.0 | 127.2, 132.7 | 228241, 227481 | 227623, 227426 |
| Reused prepared `Execute` | 74.2, 70.9 | 70.1, 72.0 | 124156, 123675 | 123974, 123736 |
| Reused prepared `QueryValues` | 72.1, 63.0 | 70.1, 69.8 | 124156, 123374 | 123822, 123755 |

The before and after timing ranges overlap for every path, and allocations
remain within about 0.5% of the corresponding baseline. This is evidence
against a material disabled-path regression, not evidence of a speedup. The
same-process harness excludes wire transport, server concurrency, and MySQL;
these numbers cannot support a server-throughput claim.
