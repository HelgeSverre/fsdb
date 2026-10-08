# ALTER algorithm selection and copied-row counts

Status: audited affected-row counts implemented; RAND default-expression
rejection remains a counterexample.

## Native evidence

`python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-alter-copy-counts-native.json`
reproduces 121 scripts on disposable native MySQL 8.4.11 with a 64 MiB buffer
pool and redo capacity. Each script records ROW_COUNT immediately after ALTER,
plus numeric error codes and SQLSTATEs. fsdb matches 117 scripts; all four
remaining results are retained in `2026-10-08-alter-copy-counts-current.json`.

The `alter-copy-counts` wire contract compares the 117 matching scripts using
actual command affected-row counts, ROW_COUNT, error codes/SQLSTATEs, and stored
rows. It does not assert complete error-message parity. The `alter-coercion`
contract also checks successful COPY counts alongside warnings and resulting
values/schema.

## Established behavior

- COPY reports the live row count, including when explicitly requested for an
  otherwise online operation or without another operation. Empty tables report
  zero; deleted row identities are excluded. INPLACE and INSTANT report zero.
- Type narrowing, stored generated columns, functional defaults, enforced CHECK
  additions, dropping a primary key, and charset changes select COPY in the
  audited definitions. Renames and mixed changes retain the selected count.
- Foreign-key additions with checks enabled select COPY. Disabled checks permit
  INPLACE, while explicit COPY still reports the copied-row count.
- VARCHAR widening can remain online only while its length-prefix width stays
  unchanged: utf8mb4 63 to 64 and latin1 255 to 256 require COPY; 64 to 65 and
  256 to 257 respectively remain online.
- Same-charset conversion, including collation-only changes, remains INPLACE.
  Changing the stored charset through CONVERT or MODIFY requires COPY.
- Unsupported algorithm errors 1845 and 1846 carry SQLSTATE 0A000.

`planAlterExecution` selects one algorithm for validation, rebuilding, and
result policy. The count is read from the candidate table after validation,
just before publication. A shared length-prefix predicate also governs
full-text preservation, avoiding separate physical-width rules.

In-memory publication remains one immutable-root replacement. This does not
implement InnoDB's physical online algorithms or lock durations, and the
matrix does not establish every ALTER combination.

## Remaining counterexample

`ADD COLUMN added DOUBLE DEFAULT (RAND())` fails with 1674 / HY000 in MySQL,
before algorithm validation. fsdb accepts default/COPY and rejects INPLACE or
INSTANT with 1845. These four scripts remain outside the passing wire contract.
They require separate default-expression safety and diagnostic-order work;
no mismatch is enrolled in the known-gap allowlist.

## Validation

- The COPY regression fails before the change and passes after it.
- `just check`: 3,139 tests pass, no build warnings or errors.
- Native replay: 121 scripts; 117 exact matches, four retained counterexamples.
- Full native wire suite: 87 contracts / 13,106 steps / zero differences.

Wire artifact: `torture/artifacts/runs/20261008T213004035-77251/contracts`.
