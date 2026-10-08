# Numeric HEX conversion

Status: exact decimal rounding and audited computed-DOUBLE conversion implemented.

## Native evidence

The [native fixture](2026-10-08-hex-numeric-native.json) records 32 scripts on
MySQL 8.4.11 with a 64 MiB buffer pool and redo capacity. Reproduce with:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-hex-numeric-native.json
```

The [fsdb replay](2026-10-08-hex-numeric-current.json) matches all 32 scripts,
including rendered HEX results, warnings, numeric errors, and SQLSTATEs.
The `hex-numeric-conversion` and `hex-expression-conversion` wire contracts
cover those matching scripts.

## Established behavior

HEX rounds DECIMAL halves away from zero and DOUBLE halves to even. Decimal
conversion stays exact across the signed 64-bit range. Rounded decimal values
outside that range clamp to the corresponding signed endpoint and emit 1292,
retaining the original decimal text. Integer inputs preserve their bits,
including unsigned values above the signed maximum. Strings encode their bytes.

The implementation shares existing approximate rounding and bounded integer
conversion helpers. DECIMAL has an exact branch so a DOUBLE intermediary cannot
lose low bits near the signed endpoints. The earlier comment grouping HEX with
BIN/OCT was incorrect: BIN/OCT's string conversion does not define HEX rounding.

Both previously divergent decimal ALTER scripts now match. The stored decimal
values and scale-loss notes were already correct; HEX's truncation caused their
result differences.

## Computed DOUBLE arguments

The three computed-DOUBLE overflow cases now reject with 1690/22003 and the
native argument expression. Positive literals continue to clamp. The expanded
[expression-kind audit](2026-10-09-hex-expression.md) covers arithmetic,
functions, lazy branches, scalar subqueries, stored columns, and signed-range
boundaries. Its remaining counterexamples concern expression-assignment
deprecation warnings; broader integer-conversion contexts remain unaudited.

## Validation

- The focused regression fails on the original HEX(2.5) result.
- `just check`: 3,152 tests pass, no build warnings or errors.
- Numeric replay: all 32 scripts match.
- ALTER coercion replay: all 23 scripts match.
- Full native wire suite: 92 contracts, 13,931 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T222906031-82197/contracts`.
