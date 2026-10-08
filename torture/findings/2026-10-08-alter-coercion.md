# ALTER stored-value coercion conditions

Status: audited single-column conditions implemented; multi-column ordering,
COPY affected-row counts, and decimal HEX conversion remain open.

## Native evidence

`python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-alter-coercion-native.json`
replays 23 scripts on disposable native MySQL 8.4.11 with a 64 MiB buffer pool
and redo capacity. The fixture records statements, rendered results and warnings,
and numeric error codes with SQLSTATEs. It does not capture affected-row counts.
The fsdb replay matches 20 scripts exactly; the three remaining results are
retained in `2026-10-08-alter-coercion-current.json`.

## Established behavior

Non-strict MODIFY and CHANGE emit 1265 warnings when VARCHAR, BINARY, or
VARBINARY values exceed the new length. Unicode character lengths count runes.
CHAR removes trailing spaces without warnings. Numeric narrowing clamps values
with 1264 warnings; invalid integer text produces 1366. DECIMAL scale loss emits
1265 notes, including in strict mode.

Strict VARCHAR and VARBINARY narrowing fail with 1265 / 01000; BINARY narrowing
fails with 1406 / 22001. Strict integer overflow fails with 1264 / 22003. Conditions
use the live row ordinal, independent of stable row IDs or deleted rows. CHANGE
uses the new column name. Failed conversions preserve the original schema and
all rows.

A unique-key collision stops conversion immediately. For `aa`, `ab`, and `zz`
narrowed to VARCHAR(1), MySQL emits truncation warnings for rows 1 and 2, then
1062. It emits no warning for row 3. The engine shares stored-value coercion with
ordinary writes and checks unique keys as each converted row is built.

The `alter-coercion-failures` wire contract checks all nine failing scripts,
including error codes, SQLSTATEs, ordered conditions, retained rows, and schema.
Successful-copy scripts remain standalone evidence because their protocol
row counts still differ; they are not passed through a result-ignoring contract.

The schema checks exposed a separate SHOW metadata difference. Native
SHOW COLUMNS, SHOW FULL COLUMNS, and DESCRIBE expose `Key` as a STRING carrying
ENUM flags for persistent tables and views, and VAR_STRING for temporary tables.
The probe metadata preserves this distinction, and the column-definition encoder
retains the ENUM collation even when its flags include BINARY_FLAG. Raw native column metadata is
retained in `2026-10-08-show-column-key-native.txt`; an Expecto regression covers
all three commands and object kinds.

## Remaining counterexamples

- `multi`: converting two columns emits conditions in column order in fsdb.
  MySQL converts both columns of each row before advancing. Correct error and
  warning order requires a row-oriented ALTER conversion plan; sorting messages
  after execution cannot reproduce early failures.
- Both `decimal` scripts store the same values, `1.1` and `2.5`, and produce the
  same scale-loss notes. `HEX(2.5)` returns `3` in MySQL and `2` in fsdb. The direct
  value query retained in each script distinguishes HEX conversion from storage
  rounding.
- Successful COPY ALTER operations still report zero affected rows in fsdb.
  The [foreign-key count finding](2026-10-08-alter-foreign-key-count.md) records
  the existing protocol counterexample. Explicit/default algorithm selection
  needs broader native coverage before the common result policy changes.

No mismatch is enrolled in the known-gap allowlist.

## Validation

- `just check`: 3,136 tests pass, no build warnings or errors.
- The duplicate-key regression fails before the implementation and passes after.
- Native fixture: all 23 scripts reproduced; fsdb replay: 20 exact matches.
- Key-diagnostic replay: 15 of 16 scripts match, including narrowing ALTER.
- Full native wire suite: 86 contracts / 11,724 steps / zero differences.

Wire artifact: `torture/artifacts/runs/20261008T211512610-76070/contracts`.
