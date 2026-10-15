# Durable write publication and queue roles

<!--
sha: 0c8da478
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [server-stage profile](2026-10-15-keyless-server-stage-profile.md)
located much of the eight-writer delay inside execution and WAL completion,
but did not separate publication-lock waiting from queue behavior. This
follow-up instrumented those boundaries in two separate builds. The probes
were removed afterward and the normal Release CLI was rebuilt successfully.

Both runs used fresh WAL-backed FSDB databases and a same-host durable MySQL
8.4.11 control. The workload was keyless autocommit `INSERT`, with one trial,
two seconds of warmup, ten measured seconds, and one or eight wire clients.
The load recipe and seed were identical in the two builds. Each timing is a
mean from the difference between the first and last 20,000-operation counter
snapshots, covering 40,000 operations at one writer and 120,000 at eight.

| Publication stage, mean µs/write | One writer | Eight writers |
|---|---:|---:|
| Wait for database publication slot | 0.02 | 12.33 |
| Hold publication slot | 8.24 | 23.94 |
| Work inside slot | 7.51 | 19.56 |
| WAL acknowledgement action | 55.34 | 260.76 |

The slot wait grows with concurrency, but it is much smaller than WAL
acknowledgement. The publication lock is therefore not a sufficient explanation
for this workload's roughly 260 µs completion action or the previously measured
whole-handler delay. These wall times overlap concurrent calls and are not
process CPU time.

The second build measured what the group-commit caller does after enqueueing.
A leader drains batches until the queue is empty before returning to its own
wire connection. Followers wait for their batch's completion signal.

| Queue role, mean per role or batch | One writer | Eight writers |
|---|---:|---:|
| Leader share of completed calls | 100% | 10.3% |
| Leader action through return | 52.62 µs | 675.56 µs |
| Leader time until own completion signal | 52.53 µs | 173.30 µs |
| Leader time after own completion signal | 0.09 µs | 502.26 µs |
| Batch work drained per leader | 52.34 µs | 630.75 µs |
| Follower action through return | — | 231.16 µs |
| Batches drained per leader | 1.00 | 4.77 |
| Writes per batch | 1.00 | 2.03 |

At eight writers, a leader spends about 502 µs draining other queued batches
after its own write is acknowledged. That is a concrete latency cost for the
leader's connection, and the queue is doing useful work during most of it. The
measurement does not show that handing the drainer role to another thread would
increase throughput; such a change could add scheduling overhead or shrink
batches. It needs a correctness-preserving design and repeated alternating
before/after trials before adoption.

| Workers | FSDB WAL inserts/sec | Durable MySQL inserts/sec |
|---:|---:|---:|
| 1, slot build | 6,805 | 16,150 |
| 8, slot build | 12,492 | 46,964 |
| 1, queue-role build | 6,960 | 15,511 |
| 8, queue-role build | 12,441 | 46,060 |

The two temporary probes had different instrumentation overhead, and each
throughput row is a single trial. They support a directional diagnosis, not a
performance improvement claim. The next scheduling experiment should first
specify who owns a queued batch if a leader returns after its own completion,
including failure and checkpoint behavior. A candidate must preserve ordered
durable acknowledgements and improve repeated same-host WAL-versus-durable
throughput before it replaces the current simple drainer.
