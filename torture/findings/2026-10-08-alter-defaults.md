# ALTER expression defaults and binlog conditions

Status: audited cases match native MySQL 8.4.11.

## Evidence

The [default fixture](2026-10-08-alter-default-native.json) records 54 scripts;
the [settings fixture](2026-10-08-binlog-settings-native.json) records 22.
Both use disposable native MySQL 8.4.11 with a 64 MiB buffer pool and redo
capacity. Reproduce each with `python3 torture/scripts/condition-oracle.py`
followed by its fixture path. The corresponding
[default replay](2026-10-08-alter-default-current.json) and
[settings replay](2026-10-08-binlog-settings-current.json) match rendered rows,
numeric error codes, and SQLSTATEs for every script.

## Observable behavior

- ADD COLUMN with RAND, RANDOM_BYTES, UUID, UUID_SHORT, SYSDATE, or the audited
  user-identity functions rejects with 1674/HY000 when logging is enabled and
  the format is ROW or MIXED. Empty tables and unchosen expression branches
  retain the rejection.
- STATEMENT format permits those defaults with one statement-wide warning;
  disabling session logging permits them without that warning. A later
  missing table, duplicate column, or unsupported algorithm retains the warning.
- Invalid default expressions take precedence over binlog safety, including a
  disallowed function in a later added column. FOUND_ROWS retains its
  deprecation warning before rejection.
- CREATE TABLE defaults, MODIFY defaults, ALTER COLUMN SET DEFAULT, and
  rebuilding an existing expression default are outside the ADD-only safety
  rule. NOW, UNIX_TIMESTAMP, CONNECTION_ID, DATABASE, and ABS are accepted in
  the audited additions.
- SHOW COLUMNS exposes expression text without the outer DDL parentheses;
  SHOW CREATE TABLE retains the required DEFAULT parentheses.
- Binlog settings normalize supported names, ordinal formats, booleans, and
  DEFAULT; invalid values retain the previous setting. Setting binlog_format
  emits its deprecation warning. SET GLOBAL sql_log_bin and changes inside
  an active transaction retain the audited native errors.

These are compatibility settings and conditions, not binary logging or
replication. Restricted-variable privileges, temporary-table restrictions,
and broader global-read behavior are not established by these fixtures.
Physical InnoDB algorithms and lock durations remain outside fsdb's engine.

## Implementation and validation

Default-expression shape and function validation share one short-circuiting
validator. The wire fixtures share one isolated-session runner, and the paired
empty/populated cases use one expression matrix. Duplicate ADD COLUMN fails
before publication, preserving the original schema.

- `just check`: 3,143 tests pass, no build warnings or errors.
- Default/settings replays: 54 and 22 scripts, zero differences.
- ALTER count replay: all 121 scripts match.
- Full native wire suite: 89 contracts, 13,551 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T215101481-79397/contracts`.
