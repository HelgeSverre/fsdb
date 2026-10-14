# Direct timing of wire write dependency expansion

<!--
sha: badff3ad with temporary counters, reverted after measurement
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [method-resolved wire trace](2026-10-15-wire-method-trace.md) sampled
blocked stacks under `TableLocks.expandDependencies`, but sampling alone could
not show the time it costs each write. Temporary `Stopwatch.GetTimestamp`
counters measured the access-list construction and dependency expansion in
`TableLocks.accessesForStatement`; a second run also measured the whole
`QueryHandler.handle` call. The counters accumulated thread wall time with
atomic additions and wrote a snapshot every 20,000 calls. They were removed
after the experiment.

Both runs used a fresh WAL-backed FSDB server, native durable MySQL as a
host-load control, eight keyless-insert workers, two seconds of warmup, and
fifteen measured seconds. The trace's adjacent uninstrumented runs had
reached 11,984 and 12,198 FSDB inserts/sec with MySQL at 44,651 and 45,272.

```sh
FSDB_DEP_PROFILE_FILE=/tmp/fsdb-dependency-profile.csv \
  FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=1 FSDB_LOAD_WARMUP=2 \
  FSDB_LOAD_SECONDS=15 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Instrumentation | FSDB inserts/sec | MySQL inserts/sec | Access-list µs/call | Expansion µs/call | Handler µs/call |
|---|---:|---:|---:|---:|---:|
| Dependency only | 12,064 | 46,075 | 4.41 | 5.43 | — |
| Dependency and handler | 11,978 | 37,550 | 4.32 | 4.77 | 474.38 |

The first snapshot covers 200,000 dependency calls; the second covers
180,000 dependency and handler calls. These are cumulative means across all
threads and include setup queries, so they are not a distribution of the
measured insert calls alone. The two FSDB throughput results stayed near the
uninstrumented bracket; MySQL was slower in the second run, so its ratio must
not be used to credit the instrumentation with a speed change.

Dependency discovery plus expansion took roughly 9–10 µs per call, or about
2% of the measured 474 µs handler mean in the second run. This directly
limits the explanation suggested by the sampled `Lazy.CreateValue` stacks:
eliminating that entire measured access path could not plausibly close the
wire throughput gap. At 11,978 completed inserts/sec with eight workers, the
mean client operation cycle is approximately 668 µs; the handler timer covers
about 474 µs of that cycle, including SQL execution and durable completion.
The remaining cycle time also includes client and server scheduling, socket
I/O, and response handling; this experiment does not attribute those parts.

The next useful split is inside the handler, especially execution versus
durable completion, paired with an outside-handler server timing. A production
change to dependency expansion is not supported by this timing result.
