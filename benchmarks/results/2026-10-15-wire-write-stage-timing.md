# Durable wire-write stage timing at one and eight workers

<!--
sha: cbb2fad9 with temporary counters, reverted after measurement
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [dependency timing](2026-10-15-write-dependency-timing.md) left most of
the wire-write cycle inside `QueryHandler.handle`. This experiment timed four
nested stages: durable completion returned by `Storage.preparePublishedEvents`,
the full query handler, the server's cancellation-watched dispatch wrapper,
and text-result sending. Temporary `Stopwatch.GetTimestamp` counters were
written every 20,000 calls and removed afterward. The normal Release binary
was rebuilt after their removal.

Each run used a fresh WAL-backed FSDB directory, a native durable MySQL host
control with InnoDB commit-time flush enabled and binary logging disabled,
two seconds of warmup, and fifteen measured seconds of keyless inserts.

```sh
FSDB_ACK_PROFILE_FILE=/tmp/fsdb-ack-stage.csv \
FSDB_QUERY_PROFILE_FILE=/tmp/fsdb-handler-stage.csv \
FSDB_RUN_PROFILE_FILE=/tmp/fsdb-wrapper-stage.csv \
FSDB_SEND_PROFILE_FILE=/tmp/fsdb-send-stage.csv \
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=1 FSDB_LOAD_WARMUP=2 \
FSDB_LOAD_SECONDS=15 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

The one-worker run used the same command with `FSDB_LOAD_WORKERS=1` and
distinct counter files. Means come from the last complete counter snapshot;
they include setup queries as well as measured inserts. Nested stage times
must not be summed. The estimated client cycle is workers divided by measured
inserts/sec and includes client, network, and server time.

| Workers | FSDB inserts/sec | MySQL inserts/sec | Estimated cycle µs | WAL completion µs | Handler µs | Dispatch wrapper µs | Response send µs |
|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 6,978 | 16,359 | 143 | 53.36 | 121.75 | 123.60 | 5.04 |
| 8 | 11,943 | 46,424 | 670 | 272.98 | 483.99 | 491.43 | 16.17 |

The eight-worker snapshots cover 180,000 calls in each stage; the one-worker
snapshots cover 100,000. The dispatch wrapper adds roughly 2 µs at one worker
and 7 µs at eight, so the cancellation watcher is not the main throughput
limit. Response sending rises under load but remains much smaller than the
handler. WAL completion accounts for about 44% of the one-worker handler mean
and 56% of the eight-worker handler mean. The handler time outside that
completion rises from roughly 68 to 211 µs as workers increase. Completion
waiting and execution-side work both grow under concurrency; neither alone
explains the full change in client cycle.

The stage timers do not separate WAL queue waiting from append, fsync, and
completion notification. The [queue-stage profile](2026-10-15-wal-queue-stage-profile.md)
measured these components in a different short run, but their values should
not be arithmetically combined with this run. An eight-worker server has more
than one operation in flight, so cumulative thread wall times do not measure
CPU demand or prove lock ownership. The durable-write gap remains open.
The next candidate should isolate the handler's non-WAL time under load and
explain why wire requests form smaller WAL batches than the embedded control;
the data do not justify changing the cancellation wrapper or response writer.
