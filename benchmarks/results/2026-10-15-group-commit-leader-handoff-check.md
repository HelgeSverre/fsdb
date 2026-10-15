# Group-commit leader handoff check

<!--
sha: 3fec3339
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [queue-role profile](2026-10-15-publication-and-queue-role-profile.md)
found that an eight-writer group-commit leader spent about 502 µs draining
other batches after its own commit signal. A temporary prototype kept the
existing ordered queue and batch processor, but let the leader return after
its own completion. If work remained, it queued one thread-pool drainer;
if the queue was empty, it released leadership inline. Scheduling failure
fell back to draining on the caller. No WAL format or acknowledgement rule
changed.

The prototype built successfully and passed all five focused group-commit
tests for batching, checkpoint order, failure propagation, capacity, and
concurrent producers. The throughput comparison used the same native host,
WAL-backed FSDB, durable MySQL 8.4.11, keyless autocommit inserts, and fresh
1,000-row users/orders/articles seeds. Each row is two trials with two seconds
of warmup and ten measured seconds per trial. The original scheduler was
measured, then the prototype, then the restored original scheduler.

| Scheduler | Workers | FSDB writes/sec | FSDB RSD | MySQL writes/sec | MySQL RSD | FSDB/MySQL |
|---|---:|---:|---:|---:|---:|---:|
| Original A | 1 | 7,179 | 2.8% | 15,782 | 0.7% | 0.45× |
| Prototype | 1 | 7,085 | 0.8% | 15,705 | 3.4% | 0.45× |
| Original A | 8 | 12,294 | 0.2% | 45,990 | 1.0% | 0.27× |
| Prototype | 8 | 12,428 | 0.1% | 45,598 | 1.1% | 0.27× |
| Original B | 8 | 12,083 | 0.2% | 45,582 | 0.6% | 0.27× |

The candidate was about 1–3% faster than the two bracketed eight-worker
baselines in absolute FSDB throughput, but the durability-matched ratio still
rounds to 0.27× in every eight-worker run. One-worker throughput was slightly
lower. This is a small, unconfirmed gain for additional thread-pool scheduling
and more complicated ownership and shutdown behavior. The prototype was
reverted; the normal Release benchmark build restored the original queue.

The leader's post-acknowledgement drain is real latency, but handing it to a
thread-pool worker did not materially close the concurrent-write gap in this
workload. Further scheduling work should look at batch formation and wire
request overlap while preserving ordered durable acknowledgements, rather
than assuming that early leader return alone raises throughput.
