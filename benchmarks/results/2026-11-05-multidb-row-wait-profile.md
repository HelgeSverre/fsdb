# Multi-database row waits and worker pressure

The maintained 8-database × 16-writer `multidb` workload remained correct but
failed its scaling threshold. This diagnostic temporarily timed every
`Monitor.Wait` in the storage row-lock path and sampled .NET thread-pool
workers and pending items every 10 ms during the fsdb phase. The probes were
removed after the runs; no scheduling policy was retained. The subject uses
an in-memory store and shares a process with the torture client, so pending
items include both client and server work. These numbers do not isolate durable
WAL throughput or a server-only worker queue.

Each run used eight databases, 16 writers per database, 30 transactions per
writer, 64 accounts, rollback every seventh transaction, and seed 204. All
eight ledgers passed against the pinned MySQL 8.4.11 oracle in every run:
412 commits, 68 rollbacks, and zero failed transactions per database.

| Hot accounts | Worker reserve | Row waits | Sum of row-wait time | Peak workers | Peak pending | Wall time | Scaling ratio |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 | current 2× connections | 8,900 | 722 s | 133 | 218 | 15.45 s | 1.26 |
| 64 | current 2× connections | 770 | 90 s | 133 | 287 | 14.18 s | 0.90 |
| 64 | current 2× connections, repeat | 772 | 108 s | 172 | 235 | 15.75 s | 1.76 |
| 64 | trial 4× connections | 772 | 104 s | 155 | 238 | 15.02 s | 1.79 |

The wide-account case reduced the number of row waits by about 11× and their
cumulative duration by about 7–8×, but the full workload still took roughly
14–16 seconds. Row contention contributes to blocking, yet removing most of
it did not close the high-fan-out wall-time gap in these runs. The two
wide-account controls also show substantial baseline and ratio variation.

The repeat reached a configured minimum of 258 worker threads, while the
actual pool peaked at 172 and pending work at 235. Raising the reservation to
roughly 500 did not increase the observed pool or materially reduce pending
work or wall time. That single candidate run does not support changing the
reserve multiplier. It also does not prove .NET injection is the root cause:
the process includes the async test client, and synchronous row waits and
other request stages can all contribute to pending work.

The next useful measurement should separate client and server processes and
time the runnable queue, row waits, query execution, and completion stages in
the same run. A scheduling change should be judged against matched hot and
wide-account runs after that separation.
