# Wire write scheduling control

<!--
sha: 5542fad3
date: 2026-10-14 to 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [embedded-versus-wire control](2026-10-14-embedded-wire-write-control.md)
found that eight WAL-backed embedded writers completed about 31,700 keyless
inserts/sec while the wire path completed about 11,900 on the same schema and
seed. A per-statement disconnect-watcher replacement had no measurable gain.
This follow-up asks whether ordinary server query processing, response work,
or too few concurrent wire requests account for the difference.

Temporary timing counters around the wire server's SQL splitter,
`runCancellable` statement dispatch, and response send recorded 99,028
statements from nine worker connections that closed before the benchmark
server was stopped. The eight-writer, one-trial WAL run measured 12,186
inserts/sec; durable MySQL measured 45,024. The counters were then removed.
At a 1 GHz `Stopwatch` frequency, the captured connections averaged about
19 µs in SQL splitting, 468 µs in statement dispatch, and 15 µs in response
sending per completed statement. These are overlapping per-connection wall
times, not CPU costs; the capture missed other worker connections and cannot
attribute the dispatch interval to parsing, publication, lock waiting, or WAL
completion. Response serialization by itself is too small in this sample to
explain the embedded-versus-wire difference. A sampled-thread trace without
method rundown had no usable symbols; a second trace ended as the disposable
server stopped and was not used for attribution.

The uninstrumented server was then tested at eight, sixteen, and thirty-two
writers, with two fresh-database trials per count, one second of warmup, and
five measured seconds. The paired MySQL server retained durable InnoDB flush
and disabled binary logging for the same single-node control as earlier runs.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=8,16,32 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Writers | FSDB + WAL ops/sec | FSDB RSD | Durable MySQL ops/sec | MySQL RSD |
|---:|---:|---:|---:|---:|
| 8 | 11,923 | 2.1% | 46,193 | 0.2% |
| 16 | 11,560 | 1.1% | 55,721 | 11.1% |
| 32 | 9,615 | 17.8% | 66,230 | 14.4% |

The eight-writer result reproduces the earlier wire baseline. Sixteen writers
do not improve FSDB throughput, and thirty-two are slower with substantial
trial variation; more client requests in flight do not recover the embedded
rate under this load. This rejects a simple per-connection round-trip ceiling
as the sole explanation. The next measurement should compare WAL batch sizes,
flush counts, and acknowledgement waits between embedded and wire clients,
then separate active SQL work from those waits before changing scheduling.

## WAL batching follow-up

Temporary counters around `Persistence.attach`'s WAL append and durable flush
recorded cumulative batch count, commit-event count, and elapsed append-and-
flush wall time every 1,000 batches. The same eight-writer, one-second warmup,
five-second measurement, schema, and seed were used in an embedded/wire/wire/
embedded run sequence. The last partial 1,000-batch interval and any final
server activity after the last recorded interval are omitted. Each keyless
insert contributes one commit event; the counters were removed afterward.

| Path and order | Measured inserts/sec | Captured batches | Events/batch | Append and flush µs/batch |
|---|---:|---:|---:|---:|
| Embedded A | 34,084 | 87,000 | 2.19 | 51.6 |
| Wire A | 12,065 | 39,000 | 1.76 | 109.4 |
| Wire B | 10,835 | 35,000 | 1.79 | 123.3 |
| Embedded B | 31,655 | 70,000 | 2.50 | 68.5 |

The wire path consistently made smaller batches and spent more elapsed time
in each append-and-flush call. The timing includes OS scheduling and file-open
work, so it cannot be read as device fsync latency. Batch size and flush time
are coupled to incoming write rate; these observations do not prove which one
causes the lower throughput.

As a bounded scheduling experiment, the group-commit leader yielded once
before draining its first batch. The queue's concurrent-producer regression
passed. One eight-writer wire trial averaged 1.86 events/batch, 110.7 µs per
append-and-flush batch, and 11,481 inserts/sec, with 45,525 for the MySQL host
control. That is a small batch-size shift without a clear throughput gain
against the adjacent wire runs, so the yield was reverted. A useful next
experiment should separate queue admission, leader wait, actual flush, and
acknowledgement timing under both client paths before changing the durable
completion policy.
