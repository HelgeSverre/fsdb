# HEX integer conversion by expression kind

Status: audited DOUBLE conversion behavior implemented; expression-assignment
deprecation warnings remain open.

## Native evidence

The [native fixture](2026-10-09-hex-expression-native.json) records 51 scripts
on disposable MySQL 8.4.11 with a 64 MiB buffer pool and redo capacity. Reproduce:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-09-hex-expression-native.json
```

The [fsdb replay](2026-10-09-hex-expression-current.json) matches 49 scripts
exactly. The two mismatches have matching results and assignment counts but
lack native warning 1287 for user-variable assignments within expressions.
The `hex-expression-conversion` wire contract covers all 49 exact matches.
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
  behavior, including selected IF and CASE branches.

The executor shares IF/CASE branch selection between ordinary evaluation and
HEX argument validation. Numeric HEX formatting remains in the existing scalar
function; byte/string preparation retains its existing charset handling.
No second evaluation is used to reconstruct a selected branch.

This matrix does not establish every integer-conversion context, mixed-result
type, or materialized/filtered scalar-subquery shape. Those need further native
probes before broader compatibility claims.

## Remaining counterexample

The `if-once` and `case-once` scripts assign to `@calls` inside their condition.
Both engines return the same HEX value and a final count of one. MySQL also
emits warning 1287: setting user variables within expressions is deprecated.
fsdb omits that deprecation warning. The native/current fixtures preserve the
full message and condition order.

## Validation

- The focused overflow regression fails before the implementation.
- `just check`: 3,152 tests pass, no build warnings or errors.
- Original numeric HEX replay: all 32 scripts match.
- Expanded expression replay: 49 of 51 scripts match; the two warning gaps remain.
- Full native wire suite: 92 contracts, 13,931 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T222906031-82197/contracts`.
