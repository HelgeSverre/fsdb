# Boolean-context numeric conversion diagnostics

Status: audited conversion, warning-count, strict mutation, and prepared-value
cases implemented. Broader conversion contexts remain subject to native checks.

## Evidence

`torture/scripts/predicate-conversion-oracle.py` passes 109 scripts on MySQL
8.4.11. Each script uses a fresh connection and an ordinary table containing
`(1,'x'), (2,'1x'), (3,'0x')`. Native execution uses a disposable same-host
server with 64 MiB InnoDB buffer pool and redo capacity.

The historical fsdb baseline at `24dba8e9` differs in 35 of the original 49
scripts; `2026-10-08-predicate-conversion-baseline.json` preserves that output.
The current replay matches all 109 scripts and all 62 join-hint lifecycle
scripts, closing the latter's missing `WHERE 'x'` warning. Exact output is in
`2026-10-08-predicate-conversion-current.json`. No failure is enrolled in the
known-gap allowlist.

## Observed contract

- Constant invalid/truncated strings in WHERE and HAVING warn once per
  statement, including true strings that admit multiple rows. Constant WHERE
  warnings remain visible on empty tables and with LIMIT 0.
- IF, NOT, and IS TRUE emit warning 1292 with `Truncated incorrect DOUBLE value`
  wording for the tested invalid literals. Empty string literals do not warn.
- Boolean operands retain evaluation order: `0 AND 'x'` and `1 OR 'x'` do not
  warn; `'x' AND 0` and `'x' OR 1` do.
- Column predicates emit one conversion warning per evaluated row. An earlier
  selective condition suppresses conversion of rows it rejects.
- Constant JOIN ON conditions emit the same DOUBLE warning once.
- Strict UPDATE and DELETE fail with error 1292 and SQLSTATE 22007. An error
  after an earlier matching row leaves both rows unchanged in the audited cases.
- Fresh prepared Boolean parameters accept valid fractional strings as numbers.
  Malformed strings use an integer prefix and INTEGER warnings: `'0.5x'` is
  false, while valid `'0.5'` is true. Empty parameter strings warn.
- A retained DECIMAL parameter uses decimal-prefix conversion and DECIMAL
  warnings after a valid fractional binding. Repeated SQL and binary executions
  preserve this transition. Column-assignment conversions remain with storage.

## Implementation

`Value.truthy` reuses `coerceLeadingDouble` and reports truncation through the
statement diagnostic policy. WHERE and HAVING preparation evaluate audited
closed predicates once. The join paths share closed-condition preparation.
Speculative analysis suppresses both conversion diagnostics and strict errors.

Parameter inference supplies Boolean numeric context. Invalid numeric strings
reuse the existing integer-prefix and decimal-prefix parsers, preserving the
retained parameter family without pre-converting column assignments.

## Validation

- `just check`: 3,112 tests passed; no build warnings or errors.
- Maintained native oracle: 109 scripts passed; current fsdb replay has no
  differences. Strict mutation preservation has separate regression coverage.
- Full wire suite: 78 cases, 10,265 steps, zero differences. The conversion
  contract includes the original 103 text scripts and repeated binary bindings
  across six Boolean contexts, with exact metadata and diagnostic comparisons.
  Artifact: `torture/artifacts/runs/20261008T184924773-64858/contracts`.

The repeated binary fixture exposed retained DECIMAL conversion that fresh
preparations did not exercise; six corresponding SQL sequences extend the
maintained native fixture and root regressions. The final root gate and native
replay include these sequences. No new performance claim is made.

## Remaining coverage

These checks establish the listed contexts, not complete conversion parity.
Nonstrict and IGNORE mutation warning multiplicity, overflow boundaries,
additional encoded/temporal values, and stored-program combinations need broader
native coverage. General optimizer controls remain separately incomplete.
