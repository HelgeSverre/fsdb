# Multi-column ALTER conversion order

Status: open. Row-conversion helpers are separated from per-action execution;
observable behavior is unchanged.

## Evidence

The [native fixture](2026-10-09-alter-row-order-native.json) contains 24 scripts
on MySQL 8.4.11, using a disposable server with a 64 MiB buffer pool and redo
capacity. It covers normal and strict modes, reversed clauses, reordered and
renamed columns, mixed numeric/text conversion, existing and added unique keys,
interleaved ADD/DROP, missing columns, and repeated modifications.

Reproduce with:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-09-alter-row-order-native.json
```

The [current replay](2026-10-09-alter-row-order-current.json) matches two scripts
and retains 22 mismatches. The matching cases modify one column and drop another.
No mismatches are enrolled in the known-gap allowlist or presented as passing
wire contracts.

## Native ordering

1. Resolve the complete definition before converting rows. An absent later
   column returns 1054 without truncation conditions from an earlier clause.
   Repeated MODIFY of the same column also returns 1054 in the audited script.
2. Visit live rows in order and columns in the final table order. Reversing
   MODIFY clauses does not reverse conditions; moving a column FIRST does.
   CHANGE conditions use the final column name.
3. In strict mode, collect conversion errors from all affected columns of the
   first failing row. Return its first error as the command failure, preserve
   all that row's errors in SHOW WARNINGS, and do not visit later rows.
4. Check uniqueness after converting the entire row. A duplicate in row two
   retains both columns' row-two warnings but none from row three. This also
   applies when the unique index is introduced by the same ALTER.
5. Failed statements preserve the original rows and schema.

For example, two VARCHAR columns containing two-character strings and narrowed
to VARCHAR(1) emit `v row 1`, `w row 1`, `v row 2`, `w row 2` in non-strict mode.
Strict mode retains both row-one errors and returns the first. Sorting messages
after independent column passes cannot reproduce these early-exit rules.

## Storage structure

`applyAlterAction` currently transforms the complete row store for each action,
so conversion and schema preparation are interleaved. `coerceAlterValue` now
owns ALTER-specific conversion error mapping, while `mapAlterRows` owns live
row numbering and stable row-ID replacement. Both remain private to Storage
and serve the existing MODIFY/CHANGE and charset conversion paths.

The remaining implementation needs a complete schema plan with source-column
bindings, followed by one row walk in final-column order. New defaults, column
moves and renames, and final unique keys must participate in that plan. The
existing deferred diagnostics mechanism can retain conditions after the first
error without re-evaluating a row. Publication must remain atomic.

## Refactor validation

- `just check`: 3,144 tests pass, no build warnings or errors.
- All 24 replay results are byte-for-byte identical before and after extraction.
  Both files have SHA-256 `d67eb8ececc2fd7da26551ea49b582bdb4195c919e16a4737019055f84265e41`.
- Existing full native wire suite: 90 contracts, 13,627 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T220152285-80377/contracts`.
