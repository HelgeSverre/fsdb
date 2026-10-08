# Integer text casts and conditional evaluation

Status: audited SIGNED/UNSIGNED text conversions and conditional warning behavior implemented.

## Native evidence

Run `python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-integer-casts-native.json`.
The fixture records 107 scripts from disposable native MySQL 8.4.11 with a
64 MiB buffer pool and redo capacity. The fsdb replay in
`2026-10-08-integer-casts-current.json` matches their rendered rows, error codes,
and SQLSTATEs. The `integer-cast-conditions` wire contract additionally compares
result types, affected rows, diagnostics, and retained table contents.

## Established behavior

- Text casts consume ASCII whitespace, an optional sign, and ASCII integer
  digits. Fractions, exponents, trailing non-whitespace, missing digits, and
  overflow produce warning 1292 with the original INTEGER conversion text.
- Positive magnitudes clamp at UINT64_MAX; negative magnitudes clamp at 2^63.
  Valid signedness conversions report warning 1105. A truncation warning can
  precede that complement warning; overflow suppresses the complement warning.
- Numeric inputs retain numeric rounding. Binary literals retain their numeric
  value; binary strings and encoded strings use text conversion semantics.
- Strict INSERT rejects malformed casts with 1292/22007. Non-strict INSERT and
  INSERT IGNORE retain the coerced value and warning. Complement warnings remain
  nonfatal in strict writes.
- IF, IFNULL, and COALESCE evaluate only selected arguments. Skipped casts do
  not emit warnings or fail strict writes. Column references and nested-query
  literal diagnostics remain validated before execution.
- INSERT validates all VALUES rows before evaluating any values. An unknown
  column in a later row prevents a preceding CAST warning, including in triggers.
  A duplicate-key failure during execution retains preceding conversion warnings.
- Prepared numeric parameters keep their existing conversion: a bound '1.9'
  becomes a numeric input and rounds to 2; a bound 'x' produces warning 1292.

Signed and unsigned text casts share one conversion function. Conditional,
window, and INSERT expression validation share one context-aware validator;
value execution retains its separate warning scope. The native condition runner
also verifies the trigger fixture without duplicating its process/error handling.

## Validation and bounds

`just check` passes 3,128 tests with no build warnings or errors. The complete
native wire suite passes 83 contracts / 11,408 steps with zero differences:
`torture/artifacts/runs/20261008T201853608-72130/contracts`.
Both maintained native fixtures pass (107 CAST scripts and 34 trigger scripts).
The trigger replay now retains only the missing-table qualification difference;
INTEGER warnings and qualified duplicate-key messages match in failing
INSERT/UPDATE cases ([evidence](2026-10-08-key-diagnostics.md)).

This establishes the audited integer-cast contexts, not complete conversion
warning coverage. Other cast targets and broader diagnostic producers remain
in GAPS.md. No findings are enrolled in the known-gap allowlist.

The focused readability comparison is recorded in
[the benchmark report](../../benchmarks/results/49d8df53-expression-cleanup.md).
