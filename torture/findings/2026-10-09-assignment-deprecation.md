# Expression-assignment deprecation warnings

Status: audited expression-assignment warning behavior implemented.

The [native fixture](2026-10-09-assignment-deprecation-native.json) records
22 scripts on disposable MySQL 8.4.11 with a 64 MiB buffer pool and redo
capacity. The [fsdb replay](2026-10-09-assignment-deprecation-current.json)
originally matched 20 scripts. The no-database missing-function difference is
now covered by an embedding regression; the other difference came from the
clients' different initialization histories, as established below.

MySQL emits warning 1287 once per syntactic assignment within an expression,
including unchosen branches and queries producing no rows. Multiple result
rows do not multiply the warning. Plain SET assignments and SELECT INTO do
not warn; assignments nested inside a SET right-hand expression do.

Syntax warnings are emitted on submission or preparation. EXECUTE does not
repeat them. Literal-introducer warnings precede the enclosing assignment's
warning, and assignment warnings survive subsequent binding failures.
The implementation uses the shared executable-expression traversal and does
not establish coverage for every DDL expression or stored-program body.

## Client initialization boundary

The first `SELECT @a:=FOUND_ROWS()` returned one in the native CLI capture
and zero in the embedding replay. A controlled PyMySQL connection to the same
MySQL 8.4.11 server returns zero for its first `SELECT FOUND_ROWS()`, zero
after `SET @x=1`, and one after `SELECT 7`. The CLI therefore enters the
script with a prior one-row result; fsdb's fresh-session zero is correct.
Both engines emit the same warnings in the same order.

The passing wire contract excludes the history-dependent first `FOUND_ROWS()`
script and checks the other cases in isolated connections. An embedding
regression covers the no-database missing-function error and both error
conditions, including the preceding assignment warning. SQL PREPARE warning
timing is also covered by an embedding regression that checks both execution
results.

## Validation

- The focused regression fails before the warning implementation.
- `just check`: 3,155 tests pass; no build warnings or errors.
- All 51 HEX expression scripts match, including assignment counts and warnings.
- Full native wire suite: 93 contracts, 13,996 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T224244964-83002/contracts`.
