# Expression-assignment deprecation warnings

Status: audited expression-assignment warning behavior implemented.

The [native fixture](2026-10-09-assignment-deprecation-native.json) records
22 scripts on disposable MySQL 8.4.11 with a 64 MiB buffer pool and redo
capacity. The [fsdb replay](2026-10-09-assignment-deprecation-current.json)
exactly matches 20 scripts. The two remaining differences are described below;
neither is enrolled in the known-gap allowlist.

MySQL emits warning 1287 once per syntactic assignment within an expression,
including unchosen branches and queries producing no rows. Multiple result
rows do not multiply the warning. Plain SET assignments and SELECT INTO do
not warn; assignments nested inside a SET right-hand expression do.

Syntax warnings are emitted on submission or preparation. EXECUTE does not
repeat them. Literal-introducer warnings precede the enclosing assignment's
warning, and assignment warnings survive subsequent binding failures.
The implementation uses the shared executable-expression traversal and does
not establish coverage for every DDL expression or stored-program body.

## Remaining differences

- With no selected database, `SELECT @a:=no_such_function()` produces native
  error 1046/3D000 and two corresponding error conditions. fsdb returns
  1305/42000. Both retain the preceding assignment warning.
- The first `SELECT @a:=FOUND_ROWS()` returns one in the native CLI capture
  and zero in the embedding replay. Both emit the same warnings in the same
  order. The clients have different initialization histories; this fixture
  does not establish the cause of the initial-value difference.

The passing wire contract excludes these two scripts and checks the other
cases in isolated connections. SQL PREPARE warning timing is also covered by
an embedding regression that checks both execution results.

## Validation

- The focused regression fails before the warning implementation.
- `just check`: 3,155 tests pass; no build warnings or errors.
- All 51 HEX expression scripts match, including assignment counts and warnings.
- Full native wire suite: 93 contracts, 13,996 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T224244964-83002/contracts`.
