# Sustained CLI throughput with Server GC

This follow-up to the [point-read diagnosis](2026-11-18-point-read-diagnosis.md)
compares the same Release Fsdb CLI binary from `2de1b9f9` with
`DOTNET_gcServer=0` and `DOTNET_gcServer=1`. Only the server process
receives the setting. The host is an Apple M2 Max Mac with 12 cores and
32 GiB RAM, macOS 15.6, and .NET 10.0.12 (SDK 10.0.401).

Each trial uses a fresh WAL directory and the shared
[benchmark fixture](../Fsdb.Benchmarks/Schema.fs) with 10,000 users,
50,000 orders, and 10,000 articles. Each client uses one connection, with
pooling disabled, and one outstanding operation at a time. Point reads
fetch one row by primary key. Mixed traffic is approximately half point
reads and half updates to IDs in disjoint worker ranges. The write-only
case uses the same disjoint point updates. The harness checks each query
returns its requested ID and verifies the sum of ages increases by the
exact number of committed updates.

Each process warms for four seconds. The 16-worker point and mixed
workloads measure for 25 seconds. Two trials per mode use opposite
mode order. The table averages the two
trial statistics. Latency percentiles are computed per trial from
client round trips. RSS is the server process's sampled resident
memory at one-second intervals. Raw samples retain the result of
each trial separately.

[Raw trial data](2026-11-25-cli-gc-load.csv)

The planned single-worker and write-only controls did not complete.
The first single-worker control managed only 221 reads/s, versus about
4,400 in the earlier harness. The host was under heavy memory pressure
with about 14 GiB of swap in use and competing processes. The controls
were stopped and excluded. The mixed trials already varied widely
within each mode, so these short windows do not establish idle-host
capacity or a reproducible tail-latency baseline.

| Workload | Workers | Workstation ops/s | Server ops/s | Gain | p95 ms (workstation → server) | p99 ms (workstation → server) | Median RSS MiB (workstation → server) | Peak RSS MiB (workstation → server) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| point | 16 | 5548 | 22586 | 4.07× | 7.91 → 0.97 | 9.82 → 13.08 | 585 → 1083 | 664 → 1101 |
| mixed | 16 | 2025 | 5805 | 2.87× | 24.40 → 10.24 | 42.02 → 45.81 | 523 → 606 | 713 → 1663 |

The point-read gain was 4.03× and 4.11× in the two pairs. The mixed
gain was 2.93× and 2.74×, with materially different absolute throughput
between trials. Server GC improved p95 for both workloads but raised
point-read p99 from 9.82 ms to 13.08 ms. Average sampled peak server
RSS for mixed traffic rose from 713 MiB to 1,663 MiB.

Server GC can be selected at CLI startup on a dedicated process.
The repository default is unchanged because the tuning trades memory
and tail latency for throughput on these workloads.

```bash
DOTNET_gcServer=1 fsdb --data-dir /path/to/data
```

[.NET GC configuration](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector#workstation-vs-server)
