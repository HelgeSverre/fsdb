# Multi-database concurrency campaign — 2026-08-17

## Why this lane exists

The single-database concurrency lane (`Concurrency.fs`, see
[`2026-08-16-concurrency-campaign.md`](2026-08-16-concurrency-campaign.md))
proves FSDB never loses a write among many workers sharing one database. It
never exercises FSDB's actual per-database design claim — that writers to
*different* databases run in parallel via per-database gates and a CAS'd
catalog pointer (`Storage.fs`, "one coarse transaction/write gate *per
database*"). A harness that only ever opens one database cannot tell the
difference between real cross-database parallelism and one big lock that
happens to also work when there's only one database to contend for.

`MultiDbRunner` (`src/Fsdb.Torture/MultiDb.fs`) runs the identical
deterministic prepared-transaction ledger workload (`ConcurrencyWorkload`,
reused as-is) against D independent databases at once, on one shared FSDB
server/store, plus a standalone single-database baseline run at the same
per-database shape. It checks three things the single-db lane can't:

1. each database's ledger still conserves independently (reuses
   `ConcurrencyRunner.runTarget`'s exact expected-balance/version/ledger-hash
   check per database);
2. no database's write ever lands in another database's tables — each
   database gets its own seed-derived transfer plan, so a leaked write would
   break that database's exact-match check instead of coincidentally
   matching;
3. wall-clock for W workers spread across D databases stays under a generous
   fraction (default 0.65x) of D times the wall-clock of running W workers
   against one database alone — the actual parallelism proof. A factor of
   1.0 would mean pure serialization; anything **above** 1.0 means running
   more databases concurrently makes each one slower than serial, which is a
   symptom of contention, not just missing parallelism.

Invoke it via `torture/scripts/run.sh multidb [options]` (same MySQL-oracle
wiring as `concurrency`) or directly with `dotnet run --project
src/Fsdb.Torture -- multidb --databases 8 --workers 16 ...`. New flags:
`--databases` (default 4) and `--scaling-factor` (default 0.65); `--workers`,
`--operations`, `--accounts`, `--hot-accounts`, `--rollback-every`,
`--timeout-seconds` are reused unchanged, meaning *per database*. New
classification: `multidb_scaling_gap` (correctness passed, scaling did not).

`compose.yaml` now sets `--max-connections=2000` on the MySQL oracle
container; the previous default (151) is far below what this lane needs
(D databases x W workers physical connections on the oracle side alone).

## Results

| Databases | Workers/db | Total connections | Correctness | Baseline (1 db) | Multi-db wall-clock | Serial-projected (Dx baseline) | Ratio | Scaling |
|---:|---:|---:|---|---:|---:|---:|---:|---|
| 4 | 4  | 16  | pass | 191 ms   | 452 ms    | 764 ms    | 0.59 | pass |
| 4 | 8  | 32  | pass | 329 ms   | 14,454 ms | 1,316 ms  | 10.98 | **fail** |
| 4 | 16 | 64  | pass | 4,154 ms | 30,988 ms | 16,616 ms | 1.86 | **fail** |
| 8 | 16 | 128 | fail (1205 lock-wait timeouts on 1 of 8 databases) | 4,057 ms | 57,179 ms | 32,456 ms | 1.76 | **fail** |
| 8 | 32 | 256 | fail (1205 lock-wait timeouts / connection resets on all 8 databases) | 19,483 ms | 129,986 ms | 155,864 ms | 0.83 | **fail** |

Evidence bundles:

- 4x4 (clean, scaling passes): `artifacts/runs/20260817T111136760-91350/multidb-seed202-dbs4-workers4-ops50-hot8-rollback7`
- 4x8 (correct, scaling fails 11x): `artifacts/runs/20260817T111156543-91477/multidb-seed203-dbs4-workers8-ops50-hot8-rollback7`
- 4x16 (correct, scaling fails ~2x): `artifacts/runs/20260817T111018478-91223/multidb-seed201-dbs4-workers16-ops50-hot8-rollback7`
- 8x16 (real 1205 failures on one database): `artifacts/runs/20260817T111237268-91935/multidb-seed204-dbs8-workers16-ops30-hot8-rollback7`
- 8x32 (real 1205/connection failures on every database): `artifacts/runs/20260817T111447632-92992/multidb-seed205-dbs8-workers32-ops15-hot8-rollback7`

All runs conserved exactly: every account's expected balance/version, every
ledger row, and the global total balance matched deterministically even when
individual operations failed with an honest 1205 error — no silent data
loss, no cross-database bleed, no lost update anywhere in this campaign. That
part of the per-database design holds.

## What this surfaces (not fixed — reporting only per task scope)

The parallelism claim does not hold at the tested shapes:

- **No shape in this campaign showed multi-database wall-clock meaningfully
  beating D times the single-database baseline.** The one shape that
  "passed" scaling (4 databases x 4 workers, 16 total connections) only
  cleared the generous 0.65x threshold narrowly (0.59x) — consistent with
  ordinary scheduler variance on top of full serialization, not with actual
  cross-database parallel execution.
- **Adding databases makes each individual database's own per-worker
  throughput collapse, not just fail to improve.** At 4 databases x 16
  workers/db, each database alone took 30,988 ms — 7.5x its own 4,154 ms
  solo baseline at the identical shape — for the same fixed amount of work
  per database. If databases were truly independent, each database's own
  elapsed time should stay close to its solo baseline regardless of how many
  other databases are running. Instead it gets dramatically worse, which
  points at contention over something shared across databases (a single
  store-wide lock, listener/accept loop, or thread-pool/connection ceiling),
  not genuine per-database gates.
- **The previously-reported 128-connection lock-wait ceiling
  (2026-08-16 campaign, single database) reappears here as a *total*
  connection ceiling that is reached by spreading the same total connection
  count across multiple databases**, not just by piling workers onto one
  database. 8 databases x 16 workers (128 total FSDB connections) produced
  real 1205 lock-wait timeouts and dropped connections on one of the eight
  databases; 8 x 32 (256 total) produced the same failures on every one of
  the eight databases. Since each database only had 16 (or 32) workers of
  its own — well inside the range that passes cleanly in the single-db
  lane — the ceiling is evidently a store/server-wide resource, not a
  per-database one.

Net: correctness (conservation, no lost updates, no cross-database bleed)
held at every tested shape, including the ones with real 1205 failures — the
failures were honest and didn't corrupt state. But the *parallelism* that
per-database gates are supposed to buy has not been demonstrated at any
tested shape, and the aggregate numbers look like the opposite of
parallelism (super-serial slowdowns at 4x8 and 4x16). This is worth the
concurrent fsdb-side agent's attention before the per-database design is
relied on for throughput, independent of this task's correctness-only scope.

## Coverage still missing

Everything the 2026-08-16 campaign already listed as missing (disconnect
churn, cancellation mid-statement, savepoints, non-REPEATABLE-READ
isolation, persistence/restart during concurrent commits) plus: databases
created/dropped concurrently with active traffic on other databases, mixed
per-database worker counts (skewed load), and a shape between 128 and 256
total connections to bisect exactly where the ceiling sits.
