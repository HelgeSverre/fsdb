# Multi-column ALTER conversion order

Status: audited MODIFY/CHANGE schema planning and row-conversion order implemented.

## Evidence

The [native fixture](2026-10-09-alter-row-order-native.json) contains 24 scripts
on MySQL 8.4.11, using a disposable server with a 64 MiB buffer pool and redo
capacity. It covers normal and strict modes, reversed clauses, reordered and
renamed columns, mixed numeric/text conversion, existing and added unique keys,
interleaved ADD/DROP, missing columns, and repeated modifications.

Reproduce with:

```sh
python3 torture/scripts/condition-oracle.py torture/findings/2026-10-09-alter-row-order-native.json
```

The [current replay](2026-10-09-alter-row-order-current.json) matches all 24
scripts. The `alter-row-order` wire contract includes these cases and four
additional NULL-conversion cases. No mismatches are allowlisted.

## Native ordering

1. Resolve the complete definition before converting rows. An absent later
   column returns 1054 without truncation conditions from an earlier clause.
   Repeated MODIFY of the same column also returns 1054 in the audited script.
2. Visit live rows in order and columns in the final table order. Reversing
   MODIFY clauses does not reverse conditions; moving a column FIRST does.
   CHANGE conditions use the final column name.
3. In strict mode, collect conversion errors from all affected columns of the
   first failing row. Return its first error as the command failure, preserve
   all that row's errors in SHOW WARNINGS, and do not visit later rows.
4. Check uniqueness after converting the entire row. A duplicate in row two
   retains both columns' row-two warnings but none from row three. This also
   applies when the unique index is introduced by the same ALTER.
5. Failed statements preserve the original rows and schema.

For example, two VARCHAR columns containing two-character strings and narrowed
to VARCHAR(1) emit `v row 1`, `w row 1`, `v row 2`, `w row 2` in non-strict mode.
Strict mode retains both row-one errors and returns the first. Sorting messages
after independent column passes cannot reproduce these early-exit rules.

## Storage structure

Statements containing MODIFY or CHANGE validate the candidate definition before
converting stored rows. `alterColumnSources` resolves final column names to their
original row positions and rejects repeated modifications. `convertAlterRows`
prepares one converter per final column, then walks stable row IDs in live-row
order. Unique and added foreign-key checks follow each complete row. Deferred
diagnostics retain errors after the first failure in that row. Failed conversion
never publishes the candidate table.

`coerceAlterValue`, `mapAlterRows`, and `alterUniqueRowValidator` share conversion,
row numbering, and unique-key rules with the existing ALTER paths. AUTO_INCREMENT
requests are reapplied to the converted rows so their floor includes stored IDs.
The audited behavior does not establish every combination of ALTER actions,
generated expressions, or storage-engine operations.

## Constraint edge cases

The [native constraint capture](2026-10-09-alter-plan-constraints-native.json)
and [fsdb replay](2026-10-09-alter-plan-constraints-current.json) cover valid and
invalid foreign keys, AUTO_INCREMENT below the highest stored ID, and NULLs
made non-null through MODIFY or an added primary key in the same statement.
Seven of eight scripts match exactly. Non-strict NULL conversion uses the
implicit zero with 1265 warnings; strict conversion retains all errors from the
first failing row. The four mode/action NULL combinations also run over the wire.

The invalid foreign-key case matches error 1452/23000, preceding truncation,
and retained rows. Its error text differs: native MySQL includes a generated
`#sql-...` table identity and the full constraint definition; fsdb names only
the constraint. That message remains a counterexample. The generated identifier
also varies across native runs, so the constraint file is a raw capture rather
than an exact-message fixture for `condition-oracle.py`.

## Validation

- Focused row-order regression fails before the implementation.
- `just check`: 3,149 tests pass, no build warnings or errors.
- Row-order replay: all 24 scripts match.
- Original ALTER coercion replay: all 23 scripts match.
- ALTER count matrix: all 121 scripts match and remain in the wire suite.
- Constraint replay: seven exact matches; one retained message difference.
- Full native wire suite: 91 contracts, 13,830 steps, zero differences.

Wire artifact: `torture/artifacts/runs/20261008T221439008-81151/contracts`.
