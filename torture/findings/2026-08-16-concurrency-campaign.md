# Prepared-transaction concurrency campaign — 2026-08-16

This campaign used MySqlConnector 2.6.2 with real server-side preparation
(`PrepareAsync`), one unpooled physical connection per worker, explicit
transactions, parameter rebinding, deterministic commits/rollbacks, and an
exact schedule-independent final-state oracle. Every committed transfer must
produce its unique ledger row, two version increments, the expected balance
deltas, and an unchanged global balance total. A successful COMMIT response is
therefore insufficient to hide a lost write.

## Defects exposed and repaired

| Surface | Minimal evidence | Repair | Verification |
|---|---|---|---|
| Connector transaction handshake | One worker failed all 10 operations because `BeginTransactionAsync` sent `SET SESSION TRANSACTION ISOLATION LEVEL REPEATABLE READ`, which FSDB rejected with 1064 | Accept that exact setting and advertise `REPEATABLE-READ`, matching FSDB's existing snapshot implementation; unsupported isolation levels remain rejected | Same one-worker case passed 8 commits, 2 rollbacks, prepared execution, and exact final state |
| Concurrent same-table COMMIT | Two synchronized workers each completed one prepared transaction and received successful COMMIT, but FSDB retained only 1/2 ledger rows and lost one transfer | Establish the transaction snapshot on its first database statement, then hold a coarse transaction/write gate through COMMIT/ROLLBACK; autocommit mutations use the same gate; abandoned server sessions roll back and release their lease | The exact two-worker case passed with both ledger rows and both account/version effects; an integration regression drops a client after a prepared transactional update and proves the write rolls back and the next transaction is not wedged |
| Harness connection deadline | A 64-worker oracle run lost one MySQL worker to MySqlConnector's default 15-second connect timeout while FSDB completed all 6,400 operations | Apply the requested concurrency timeout to connection establishment as well as commands | Identical 64-worker rerun passed both targets |
| Harness phase synchronization | At 128 workers, a synchronous `Barrier` starved command-preparation continuations and produced 23 MySQL communication timeouts while FSDB still completed all operations | Replace it with a reusable `TaskCompletionSource`-based asynchronous phase barrier; failed startup workers remain no-op phase participants so evidence completes without deadlock | Identical 128-worker rerun passed both targets with zero failures |

Failure and repair evidence bundles:

- Handshake failure: `artifacts/runs/20260816T144824124-35449/concurrency-seed73-workers1-ops10-hot4-rollback5`
- Handshake repaired: `artifacts/runs/20260816T144938139-37238/concurrency-seed73-workers1-ops10-hot4-rollback5`
- Minimal lost commit: `artifacts/runs/20260816T144956586-37322/concurrency-seed73-workers2-ops1-hot4-rollback0`
- Minimal lost commit repaired: `artifacts/runs/20260816T145455398-42215/concurrency-seed73-workers2-ops1-hot4-rollback0`
- Synchronous-barrier oracle failure: `artifacts/runs/20260816T150211996-51266/concurrency-seed101-workers128-ops25-hot16-rollback13`
- Asynchronous-barrier rerun: `artifacts/runs/20260816T150456123-52408/concurrency-seed101-workers128-ops25-hot16-rollback13`

No finding was enrolled as a known gap.

## Correctness and pressure results after repair

| Workers | Transactions | Commits / rollbacks | FSDB throughput | FSDB p95 / p99 | Result |
|---:|---:|---:|---:|---:|---|
| 1 | 10 | 8 / 2 | 278 tx/s | 21 / 21 ms | pass |
| 2 | 2 | 2 / 0 | 74 tx/s | 22 / 22 ms | pass |
| 8 | 800 | 640 / 160 | 1,061 tx/s | 9 / 9 ms | pass |
| 16 | 800 | 686 / 114 | 191 tx/s | 35 / 2,726 ms | pass |
| 32 | 8,000 | 6,858 / 1,142 | 229 tx/s | 327 / 775 ms | pass |
| 64 | 6,400 | 5,819 / 581 | 283 tx/s | 396 / 419 ms | pass |
| 128 | 3,200 | 2,954 / 246 | 45 tx/s | 378 / 64,513 ms | pass |

The largest clean result used 128 simultaneous physical connections and 3,200
prepared transactions. FSDB produced every expected commit/rollback outcome,
ledger ID, balance, version, and conservation total with zero execution or
protocol errors. The evidence bundle is
`artifacts/runs/20260816T150456123-52408/concurrency-seed101-workers128-ops25-hot16-rollback13`.

A final post-review smoke against the completed binaries also passed at
`artifacts/runs/20260816T151905291-65838/concurrency-seed107-workers16-ops50-hot8-rollback7`.

## Where it taps out

Correctness now survives the tested pressure, but the implementation is not
MVCC. After its first real database statement, one explicit transaction owns a
global store gate until COMMIT/ROLLBACK; autocommit writes queue behind it.
This is intentionally stronger than MySQL's row-level locking and fixes silent
data loss, but it prevents write parallelism across unrelated rows and tables.

At 128 queued sessions the synchronous engine boundary around that gate causes
a severe latency cliff: throughput fell to 45 tx/s and p99 reached 64.5 seconds
even though p95 remained 378 ms. This is the current demonstrated tap-out. The
next transaction architecture should use asynchronous acquisition at the
server/session boundary plus row/version conflict tracking or maintained
indexes, not weaken the final-state oracle or restore table-level
last-writer-wins merging.

Coverage still missing: repeated disconnect churn mid-transaction under load, cancellation
while queued/inside a statement, deadlock/error retry semantics, savepoints in
concurrent workloads, autocommit mixed with explicit transactions, disjoint
table writers, isolation levels other than REPEATABLE READ, prepared SELECTs
inside transactions, and persistence/restart during concurrent commits.
