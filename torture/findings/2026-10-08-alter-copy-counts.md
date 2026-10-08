# ALTER algorithm selection and copied-row counts

Status: audited affected-row counts and default-expression rejection implemented.

## Native evidence

`python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-alter-copy-counts-native.json`
reproduces 121 scripts on disposable native MySQL 8.4.11 with a 64 MiB buffer
pool and redo capacity. Each script records ROW_COUNT immediately after ALTER,
plus numeric error codes and SQLSTATEs. fsdb matches all 121 scripts; results are retained in
`2026-10-08-alter-copy-counts-current.json`.

The `alter-copy-counts` wire contract compares all 121 scripts using
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

## Expression defaults

`ADD COLUMN added DOUBLE DEFAULT (RAND())` rejects with 1674 / HY000 under
native default binlog settings, before algorithm validation. All four algorithm
variants match and are included in the wire contract. The broader
[default-expression audit](2026-10-08-alter-defaults.md) covers the different
behavior with STATEMENT format and disabled session logging.

## Validation

- The COPY regression fails before the change and passes after it.
- `just check`: 3,143 tests pass, no build warnings or errors.
- Native replay: 121 scripts; 121 exact matches.
- Full native wire suite: 89 contracts / 13,551 steps / zero differences.

Wire artifact: `torture/artifacts/runs/20261008T215101481-79397/contracts`.
