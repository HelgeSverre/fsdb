# Trigger warning lifetimes and RESIGNAL

Status: audited warning lifetimes and RESIGNAL condition handling implemented;
conversion-warning and diagnostic-text differences remain.

## Native evidence

`torture/scripts/trigger-warning-oracle.py` verifies 34 scripts on MySQL 8.4.11.
The disposable native server uses a 64 MiB buffer pool and redo capacity.
Inputs, outputs, and expected error codes/SQLSTATEs are preserved in
`2026-10-08-trigger-warnings-native.json`; the client continues after errors.
The fsdb replay matches 29 rendered outputs, with the five differences below
preserved in `2026-10-08-trigger-warnings-current.json`.

## Established behavior

- BEFORE and AFTER triggers keep successful warning conditions local, across
  INSERT, UPDATE, and DELETE. This includes SIGNAL and warnings from a called
  procedure; the CAST warning-production gaps below remain separate.
- Warnings from the invoking statement survive. An indexed DELETE IGNORE
  predicate is not evaluated again when its selected row is unchanged.
- GET DIAGNOSTICS inside the trigger sees the warning code and ROW_COUNT = 0.
- An unhandled failure exports conditions from its failing statement, followed
  by the terminal error. Earlier successful trigger statements and earlier
  trigger invocations do not leak their warnings into that failure.
- Warning handlers and warning RESIGNAL remain nonfatal. Handler bodies use
  the same statement dispatch as the rest of the trigger, avoiding duplicate
  condition dispatch.
- Explicit RESIGNAL SQLSTATE appends a condition, including when the SQLSTATE
  is unchanged. It retains the original message, resets the
  numeric code for the new SQLSTATE class, and then applies explicit SET items.
  Bare and SET-only RESIGNAL replace the active condition instead.

Trigger statements retain their local diagnostics snapshot. Only an unhandled
failure exports the preceding conditions separately from its QueryResult error.
A capture around each trigger invocation prevents successful handler activity
from changing the invoking statement's diagnostics.

## Remaining differences

The maintained replay keeps these counterexamples visible:

- `warning-in-failed-insert` and `warning-in-failed-update`: malformed
  `CAST('x' AS SIGNED)` omits native warning 1292 before a duplicate-key failure.
  Both cases also expose the key-name qualification difference below.
- Those cases and the corresponding `boolean-warning-in-failed-*` controls:
  error 1062 names `PRIMARY` instead of `audit.PRIMARY`.
- `warning-then-missing-table`: error 1146 names `absent` instead of
  `probe.absent`.

The Boolean controls retain the correct conversion-warning counts in the
replay; the INSERT control also has a focused regression. Exact text remains
divergent. These five
cases stay outside the passing wire contract, with exact native and fsdb output
retained in the replay. Nothing is enrolled in the known-gap allowlist.

## Validation

- `just check`: 3,124 tests pass with no build warnings or errors.
- Maintained native oracle: all 34 scripts pass.
- The original DELETE IGNORE replay: all 14 scripts match.
- Full wire suite: 82 contracts / 11,095 steps / zero differences. Trigger
  lifetime cases compare exact diagnostics, SQLSTATEs, affected rows, table
  state, local diagnostics, and warning/fatal RESIGNAL variants.
  Artifact: `torture/artifacts/runs/20261008T195250417-70218/contracts`.
