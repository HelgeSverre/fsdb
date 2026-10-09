# Stored-function writes during UPDATE

Status: fixed for the audited cases.

Native MySQL 8.4.11 runs on a disposable server with a 64 MiB buffer pool and
redo capacity. The [native scripts and results](2026-10-09-routine-update-writes-native.json)
cover assignments, WHERE and JOIN predicates, plain and ignored updates,
statement errors, and transaction completion at all four isolation levels.
The [baseline at 0f32ec35](2026-10-09-routine-update-writes-before.json) differs
in 16 of 20 cases; the [current replay](2026-10-09-routine-update-writes-current.json)
matches all 20. Each fixture retains its complete SQL and expected output.

## Observable behavior

A function changes an unreferenced parent key from 2 to 3 and returns 3.
Outer child updates accept the new reference and retain both sets of writes.
A function evaluated by a predicate can write even when no outer row matches.

A later SIGNAL aborts the statement and discards earlier function and outer
writes. The same rollback boundary covers JOIN predicate evaluation inside an
explicit transaction; committing after the error cannot publish those writes.
An independent reader cannot observe successful writes before COMMIT, and
ROLLBACK discards both the function and outer changes. Committed writes survive
WAL and snapshot recovery.

When the parent table is explicitly read by the invoking join, attempting to
write it from the function still fails with 1442 / HY000. Fatal function errors
remain 1644 / 45000 under UPDATE IGNORE.

## Execution boundary

Custom scalar invocation exposes its actual expression store to stored
functions. Nested function statements acquire write locks while retaining the
invoking statement's private view. Top-level transaction preparation still
refreshes its view normally.

Routine-bearing single-table updates take a private snapshot before evaluating
predicates and assignments. Joined updates take theirs before evaluating joins.
For routine-bearing updates, each assignment runs before storage captures the
catalog used for constraint validation. Publication retains the routine writes;
statement errors discard the snapshot. Ordinary eligible UPDATE IGNORE batching
is unchanged.

## Verification

- Root gate: 3,177 tests pass with no build warnings or errors.
- Native wire: 98 contracts, 14,767 steps, zero differences at
  `torture/artifacts/runs/20261009T065721485-95592/contracts`.
- `routine-update-writes` retains the audited SQL as a permanent wire contract.
- Expecto covers zero-match writes, fatal errors, transaction visibility,
  isolation levels, and WAL/snapshot recovery.
- [Performance controls](../../benchmarks/results/0f32ec35-routine-update.md)
  retain the ordinary UPDATE and UPDATE IGNORE paths.

The audit does not establish parity for every routine call site, concurrent
lock-contention schedule, or multi-target update ordering.
