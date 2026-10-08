# Foreign-key validation and diagnostics

Native MySQL 8.4.11, using a disposable server with a 64 MiB InnoDB buffer pool
and redo capacity, is the oracle. The [native scripts and results](2026-10-09-foreign-key-validation-native.json)
and [fsdb replay](2026-10-09-foreign-key-validation-current.json) retain every
case, including the remaining differences.

## Matched behavior

Child-side error 1452 and parent-side error 1451 include the database, table,
constraint name, child and parent columns, and explicit referential actions.
Same-database parent names are unqualified; cross-database references retain
the database. Backticks are escaped, table names use the native normalized
spelling, and explicit NO ACTION is omitted.

After checks are reenabled, a dropped parent table rejects new non-NULL
references. NULL references remain valid. Existing orphans can receive
payload-only updates when the supporting index is unchanged. Changes to the
primary key or a trailing column of the supporting index recheck the reference;
no-op assignments do not.

Self-referencing INSERT, REPLACE, upsert, and UPDATE candidates can satisfy
their own reference. Multi-row INSERT sees earlier accepted rows. Rejected
INSERT IGNORE candidates never become parents for later rows.

## Remaining differences and scope

- CREATE generates the audited native names. [Broader naming behavior](2026-10-09-foreign-key-names.md), including collisions and backing indexes, remains incomplete.
- ALTER error messages can name MySQL's temporary `#sql...` table; fsdb retains
  the logical table name. Temporary identifiers are not fabricated.
- Selection among multiple eligible supporting indexes and functional trailing
  key parts is not covered by this matrix.

The replay matches all 35 scripts. [UPDATE IGNORE constraint skipping](2026-10-09-update-ignore.md) is covered separately. This is bounded evidence, not a claim of
complete foreign-key parity.

## Validation

The root gate passes 3,168 tests without build warnings or errors. The
`foreign-key-row-validation` wire contract covers acceptance, errors,
diagnostics, affected counts, and retained rows across the main write forms.
The complete native wire run passes 95 contracts and 14,247 steps with no
differences at `torture/artifacts/runs/20261008T233047098-86706/contracts`.

The self-reference regressions failed with error 1452 before the candidate-key
check was added. Constraint checks reuse the existing table-address and
collation-aware key encoding logic. Candidate keys enter the mutable lookup
only after the row is accepted.
