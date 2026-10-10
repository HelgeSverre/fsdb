# HEX integer conversion by expression kind

Status: audited DOUBLE conversion behavior and expression-assignment
deprecation warnings implemented.

## Native evidence

The [native fixture](2026-10-09-hex-expression-native.json) records the original
matrix and scalar-subquery follow-ups. It runs on disposable
MySQL 8.4.11 with a 64 MiB buffer pool and redo capacity. Reproduce:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-09-hex-expression-native.json
```

The [fsdb replay](2026-10-09-hex-expression-current.json) matches the original
51 scripts exactly. The `hex-expression-conversion` wire contract covers those scripts,
including user-variable assignment warnings and single evaluation of conditions.
No mismatch is enrolled in the known-gap allowlist.

## Established behavior

- Approximate literals and user variables clamp to the signed integer range
  without warnings. Stored DOUBLE columns clamp with warning 1292 naming an
  incorrect INTEGER value.
- Audited arithmetic, negation, casts, and numeric functions reject an
  out-of-range integer conversion with 1690/22003 and name the argument
  expression. ROUND includes its implicit zero precision; CEIL renders as
  CEILING in the diagnostic.
- The arithmetic and COALESCE probes accept the exact negative signed endpoint, while
  the audited DOUBLE casts reject that endpoint. Positive values at the
  floating-point representation of 2^63 are outside the signed range.
- IF and CASE follow the selected branch's overflow behavior. COALESCE and
  IFNULL apply their own computed-result conversion. Unchosen branches do not
  execute, and side-effecting conditions execute once.
- The audited plain scalar subqueries retain the projection's conversion
  behavior, including selected IF and CASE branches. Grouped, UNION,
  derived, and filtered scalar subqueries also retain the stored DOUBLE
  conversion for positive and negative overflow values without warnings.
- A scalar subquery projecting a mixed DOUBLE/string IF result materializes
  its declared string result before HEX interprets the bytes. The selected
  DOUBLE value `1e20` therefore produces `31653230`, without a warning.

The executor shares IF/CASE branch selection between ordinary evaluation and
HEX argument validation. Numeric HEX formatting remains in the existing scalar
function; byte/string preparation retains its existing charset handling.
No second evaluation is used to reconstruct a selected branch.

This matrix does not establish every integer-conversion context, mixed-result
type, or scalar-subquery shape. Those need further native probes before broader
compatibility claims.

## Assignment warning coverage

The `if-once` and `case-once` scripts assign to `@calls` inside their condition.
Both engines return the same HEX value, a final count of one, and warning 1287.
See the [assignment warning audit](2026-10-09-assignment-deprecation.md) for
preparation timing, warning order, and remaining unrelated diagnostics.

## Validation

- The focused overflow regression fails before the implementation.
- `just check`: 3,155 tests pass, no build warnings or errors.
- Expanded expression replay: all 51 scripts match.
- Full native wire suite: 93 contracts, 13,996 steps, zero differences.
- Materialized scalar-subquery follow-up: the expanded native fixture passes
  on MySQL 8.4.11, and `just check` passes 3,269 tests including its new
  focused regression.
- Mixed-result scalar-subquery follow-up: the native fixture records the
  string conversion and an Expecto regression covers it alongside numeric
  grouped, CTE, DISTINCT, UNION ALL, and window forms. The expanded
  `hex-expression-conversion` differential contract passes; the full
  mode-0 Linux contract run still reports the documented identifier-case
  policy differences outside this contract.

Wire artifact: `torture/artifacts/runs/20261008T224244964-83002/contracts`.
