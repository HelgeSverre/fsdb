# Numeric HEX conversion

Status: exact decimal rounding and audited numeric conversion implemented;
computed-DOUBLE overflow remains open.

## Native evidence

The [native fixture](2026-10-08-hex-numeric-native.json) records 32 scripts on
MySQL 8.4.11 with a 64 MiB buffer pool and redo capacity. Reproduce with:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-08-hex-numeric-native.json
```

The [fsdb replay](2026-10-08-hex-numeric-current.json) matches 29 scripts,
including rendered HEX results, warnings, numeric errors, and SQLSTATEs.
The `hex-numeric-conversion` wire contract covers those matching scripts.

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

## Remaining counterexamples

MySQL rejects HEX of `-1e30`, `CAST('-1e30' AS DOUBLE)`, and
`CAST('1e30' AS DOUBLE)` with 1690/22003, naming the argument expression.
fsdb currently clamps these values. Native HEX of the positive literal `1e30`
clamps without a warning, so a blanket range check on the evaluated DOUBLE
would erase an observable distinction. Expression-sensitive integer conversion
needs further native probes before implementation. No mismatch is allowlisted.

## Validation

- The focused regression fails on the original HEX(2.5) result.
- `just check`: 3,144 tests pass, no build warnings or errors.
- Numeric replay: 29 of 32 scripts match; all mismatches retained.
- ALTER coercion replay: 22 of 23 scripts match; multi-column order remains.
- Full native wire suite: 90 contracts, 13,627 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T215647294-79984/contracts`.
