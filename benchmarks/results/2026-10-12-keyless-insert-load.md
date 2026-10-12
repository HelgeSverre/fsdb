<!--
sha: 0eb67869
date: 2026-10-12T03:21:19Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 1000 users, 1000 orders, 1000 articles
-->

Workers: 1, 8. 3 trial(s), 3.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | insert-keyless | 7324 | 4.3% | 0.0% | 10032 | 2.9% | 0.0% | 0.73x |
| 8 | insert-keyless | 9865 | 1.6% | 0.0% | 25451 | 0.9% | 0.0% | 0.39x |

The measured statement was `INSERT INTO keyless_events (worker_id, event_id)
VALUES (?, ?)`, with literal integers substituted by each worker's connection.
The table has two non-null integer columns and no keys. Each trial used a fresh
seeded database, warmed the workload, and checked that every completed insert
from both phases remained in the table. No trial lost rows or reported a
retryable lock/deadlock error. The benchmark implementation and retention check
were uncommitted additions over the `0eb67869` base revision when measured.

Eight workers improved fsdb's completed rate by **1.35x** over one worker;
MySQL improved by **2.54x**. The fsdb and MySQL write rates are not a
durability-matched comparison: fsdb was in-memory and MySQL used its normal
durable mode. The weaker fsdb scaling is a useful signal for profiling the
per-database publication section and keyless optimistic-merge work; this run
does not isolate which part dominates. The three short trials support a
directional result, not a production throughput guarantee.

## Follow-up on the publication path

A ten-second `dotnet-sampled-thread-time` trace of an eight-writer run sampled
substantial monitor waiting: `Monitor.Enter_Slowpath` accounted for about 20%
of sampled thread time. The samples include other runtime waits and do not
identify a single lock or measure its hold duration, but they support looking
at synchronization before micro-optimizing row coercion.

I tried preparing a single keyless candidate before acquiring the database
publication lock, using the existing prepared-candidate helper. The same
one/eight-writer, three-trial configuration measured **7,623 / 9,836 ops/sec**
after that change, against **7,324 / 9,865 ops/sec** in the recorded baseline.
The eight-writer result did not improve; the single-writer change is within
the short-run variation and unrelated to lock parallelism. I removed the
candidate fast path rather than retain complexity without a demonstrated gain.
The next useful profile should separate time waiting for the database slot
from work done while holding it, and inspect statement/protocol costs outside
that section. A 16-writer regression now checks that keyless autocommit inserts
remain visible exactly once.
