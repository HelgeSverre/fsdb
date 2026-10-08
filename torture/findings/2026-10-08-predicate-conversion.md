# Boolean-context numeric conversion diagnostics

Status: native contract pinned; implementation incomplete.

## Evidence

`torture/scripts/predicate-conversion-oracle.py` passes 49 scripts on MySQL
8.4.11. Each script uses a fresh connection and an ordinary table containing
`(1,'x'), (2,'1x'), (3,'0x')`. Native execution uses a disposable same-host
server with 64 MiB InnoDB buffer pool and redo capacity.

The fsdb baseline at `24dba8e9` differs in 35 scripts. Exact output is retained
in `2026-10-08-predicate-conversion-baseline.json`. Empty strings, whitespace
around valid numbers, and short-circuited invalid operands already match.
No failure is enrolled in the known-gap allowlist.

## Observed contract

- Constant invalid/truncated strings in WHERE and HAVING warn once per
  statement, including true strings that admit multiple rows. Constant WHERE
  warnings remain visible on empty tables and with LIMIT 0.
- IF, NOT, and IS TRUE emit warning 1292 with `Truncated incorrect DOUBLE value`
  wording for the tested invalid strings. Empty strings do not warn.
- Boolean operands retain evaluation order: `0 AND 'x'` and `1 OR 'x'` do not
  warn; `'x' AND 0` and `'x' OR 1` do.
- Column predicates emit one conversion warning per evaluated row. An earlier
  selective condition suppresses conversion of rows it rejects.
- Constant JOIN ON conditions emit the same DOUBLE warning once.
- Strict UPDATE and DELETE with an invalid constant predicate fail with error
  1292 and SQLSTATE 22007.
- A string bound to a bare WHERE parameter emits `Truncated incorrect INTEGER
  value` wording. Parameter coercion needs separate treatment from literal
  DOUBLE conversion.

## Implementation constraints

`Value.truthy` currently discards truncation through `toDouble`.
`Value.coerceLeadingDouble` already returns both the converted number and a
truncation flag; numeric aggregates and functional-index evaluation use this
information for diagnostics. Boolean conversion should reuse that parsing rule.

Simply making every truth test emit a warning would repeat constant warnings
per row and affect speculative planning and metadata evaluation. The fix needs
to preserve short-circuit evaluation, constant-condition planning, suppression
during speculative analysis, strict mutation errors, and parameter coercion.
The native fixture distinguishes these contracts before implementation changes.
