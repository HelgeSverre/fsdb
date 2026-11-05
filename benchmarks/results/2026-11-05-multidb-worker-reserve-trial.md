# High-fan-out worker reserve trial (2026-11-05)

The maintained multi-database workload previously found correct writes but a
throughput ceiling at eight databases and 16 writers per database. Each run
used 30 transactions per writer, 64 accounts, eight hot accounts, and a
rollback every seventh transaction, with MySQL 8.4.11 as the correctness
oracle. `MultiDbRunner` times only fsdb's concurrent phase; the MySQL phase
finishes before that stopwatch starts.

The server reserves at least twice the number of active connections in .NET
worker threads because row-lock waits and statement execution are synchronous.
A macOS sample during a current-head 8 × 16 run saw 131 thread-pool workers.
A short managed trace near the end of another run contained many thread-pool,
monitor-wait, and socket-wait stacks, but its short window and mixed client
and server stacks do not isolate a causal hotspot.

I trialled a cap of 64 on that worker reserve and reran the same workload.
The candidate was reverted after the result.

| Run | Single-db baseline | Eight-db wall time | Serial projection | Ratio | Correctness |
|---|---:|---:|---:|---:|---|
| Current policy, first checkpoint | 1,342 ms | 17,644 ms | 10,736 ms | 1.64 | all eight pass |
| Current policy, sampled | 1,337 ms | 19,645 ms | 10,696 ms | 1.84 | all eight pass |
| Current policy, managed trace | 1,397 ms | 16,210 ms | 11,176 ms | 1.45 | all eight pass |
| 64-worker reserve cap | 1,402 ms | 27,944 ms | 11,216 ms | 2.49 | all eight pass |

The cap worsened the matched shape substantially, despite preserving every
commit, rollback, ledger balance, and cross-database isolation check. The
run-to-run variation among current-policy controls limits precise attribution,
but the candidate falls well outside that observed range. Reducing worker
supply without changing synchronous lock waits is not supported by this
experiment. A further scheduling change needs a longer phase-specific trace
or direct measurements of row-wait and runnable-worker time.
