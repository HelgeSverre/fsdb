# Duplicate-key diagnostic qualification

Status: base-table qualification and single-column ALTER truncation warnings
implemented; dotted quoted identifiers remain a counterexample.

## Native evidence

`python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-key-diagnostics-native.json`
verifies 16 scripts on disposable native MySQL 8.4.11 with a 64 MiB buffer pool
and redo capacity. The fixtures preserve statements, rendered diagnostics,
error codes, and SQLSTATEs. The fsdb replay matches 15 scripts; the remaining
difference are retained in `2026-10-08-key-diagnostics-current.json`.

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

## Remaining counterexamples

- `odd-name`: MySQL preserves a dot inside the quoted table name `Odd.Table`,
  reporting `odd.table.Odd.Key`. fsdb interprets that dot as qualification and
  rejects the absent database `Odd` with error 1049. This requires preserving identifier structure beyond
  diagnostic formatting; the discrepancy is not corrected by adding a string
  prefix at the error boundary.
The `narrow-column` case now matches, including both truncation warnings before
error 1062. A three-row variant in the [ALTER coercion contract](2026-10-08-alter-coercion.md)
also verifies that conversion stops before the third row can emit a warning.

The dotted-identifier script remains outside the passing wire contract.
Nothing is enrolled in the known-gap allowlist.

## Validation

- `just check`: 3,136 tests pass, no build warnings or errors.
- Full native wire suite: 86 contracts / 11,724 steps / zero differences.
- Native key-diagnostic fixture: all 16 scripts reproduced.
- Key replay: 15 of 16 exact matches; trigger replay: all 34 exact matches.

Wire artifact: `torture/artifacts/runs/20261008T211512610-76070/contracts`.
