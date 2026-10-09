# Duplicate-key diagnostic qualification

Status: audited base-table qualification, ALTER truncation warnings, and dotted
quoted identifiers implemented.

## Native evidence

`python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-key-diagnostics-native.json`
verifies 16 scripts on disposable native MySQL 8.4.11 with a 64 MiB buffer pool
and redo capacity. The fixtures preserve statements, rendered diagnostics,
error codes, and SQLSTATEs. The fsdb replay matches all 16 scripts; results are retained in
`2026-10-08-key-diagnostics-current.json`.
Those retained results use the case-insensitive `lower_case_table_names=2`
policy fsdb advertises. Digest-pinned Linux MySQL defaults to a different
identifier policy.

## Established behavior

Error 1062 names the base table and index, such as `target.PRIMARY` or
`target.NamedKey`, with SQLSTATE 23000. INSERT, UPDATE, INSERT IGNORE, ODKU,
qualified references, aliases, views, CREATE UNIQUE INDEX, ADD UNIQUE, and
ADD PRIMARY KEY follow this rule. A renamed table uses its new name. Index
name case is retained; table names follow the engine's case folding.

`StorageError.DuplicateKey` carries table, key name, and value separately.
All duplicate-detection paths preserve the table identity; `toMySqlError`
formats the diagnostic once for errors and ignored-row warnings. F# callers
matching this error now receive a three-field case instead of two fields.

Four trigger counterexamples now match, including the INTEGER/DOUBLE warnings
before duplicate-key failures in INSERT and UPDATE. They are included in the
passing trigger wire contract. Its replay now matches all 34 scripts, including
[qualified missing-table errors](2026-10-08-missing-table.md).

## Dotted identifiers and ALTER conversion

The `odd-name` case preserves the literal dot in `Odd.Table` and reports
`odd.table.Odd.Key` under `lower_case_table_names=2`. A pinned Linux server
with `lower_case_table_names=0` instead reports `Odd.Table.Odd.Key`. The
[expanded quoted-name audit](2026-10-09-quoted-table-names.md)
covers resolution, generated view writes, and cross-database renames.

The `narrow-column` case matches both truncation warnings before error 1062.
A three-row variant in the [ALTER coercion contract](2026-10-08-alter-coercion.md)
verifies that conversion stops before the third row can emit a warning.
Nothing is enrolled in the known-gap allowlist.

## Validation

- `just check`: 3,162 tests pass, no build warnings or errors.
- Full native wire suite: 94 contracts / 14,125 steps / zero differences.
- Key replay: all 16 scripts match exactly.
- Expanded quoted-name replay: all 15 scripts match exactly.

Wire artifact: `torture/artifacts/runs/20261008T230516003-85155/contracts`.
