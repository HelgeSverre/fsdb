# Foreign-key column changes

MySQL 8.4.11 was probed in disposable servers using the digest-pinned image
documented in `torture/`. The matching Expecto regressions cover both sides
of a constraint.

With checks enabled, changing a referenced `INT` primary key or its child
column to `BIGINT` returns 3780. Changing the child column from nullable to
`NOT NULL` succeeds. Dropping and re-adding a child column under the same name
in one `ALTER` returns 1828; doing so to a referenced parent column returns
1829. Dropping the foreign key in the same statement permits the child-column
replacement. A parent collation conversion that changes a referenced text
column's collation returns 3780.

For a combined `ALTER` that adds a foreign key and modifies its child column,
MySQL resolves compatibility against the final definition. Changing `BIGINT`
to `INT` and adding the reference to an `INT` parent succeeds in either action
order; changing `INT` to `BIGINT` in the same statement returns 3780 and leaves
the original table unchanged. fsdb matches these three cases.

Dropping the child foreign key and changing its column from `INT` to `BIGINT`
succeeds in either action order. Dropping and re-adding that key alongside the
incompatible change returns 3780 and retains both the original `INT` column
and constraint. fsdb matches these cases in the root regression and the
`foreign-key-alter-definitions` wire contract.

`CHANGE COLUMN` follows the same final-definition rule while renaming.
Changing a referenced child column to a new name with the same type retains
the foreign key on that new name; widening it to `BIGINT` returns 3780 and
leaves the original column and key intact. Dropping the key permits the rename
and widening in either action order. Adding a new key on the final renamed
column succeeds whether the ADD or CHANGE action comes first. Both catalog
metadata and error behavior match in the root and wire regressions.

The native type probes accepted different lengths for `VARCHAR`, `CHAR` versus
`VARCHAR`, and different `DECIMAL` precision and scale; they rejected different
text collations and integer widths or signedness. `ENUM` and `SET` member lists
may differ, and `ENUM` and `SET` can reference one another when their
collations match. Different implicit character sets are incompatible. These
boundaries now inform foreign-key creation and column-change validation. The
checks happen before publishing the altered catalog.

Additional fresh-server creation probes accepted `BINARY` to `VARBINARY`,
different `TIME` precisions, different `BIT` lengths, `FLOAT` with different
precision, signed versus unsigned `DECIMAL`, and `YEAR` to `YEAR`. A
`VARCHAR` child referencing an `ENUM` or `SET` parent returned 3780. The
accepted `SET`↔`ENUM` pair was checked in both directions; the regression
covers both directions and the implicit-character-set rejection.

The `foreign_key_checks=0` drop/re-add boundary was re-probed on a fresh
MySQL 8.4.11 server. MySQL still returns 1828 for a child column and 1829
for a referenced parent column, leaving both definitions unchanged. It also
returns 3780 for changing a constrained `INT` parent or child to `BIGINT` with
checks disabled. The drop/re-add rejection already matched; fsdb's ALTER type
check now runs regardless of the row-check setting. The earlier claim that
MySQL silently removed the constraint was incorrect. Broader type and
ALTER-action combinations remain unverified.

MySQL 8.4.11 also rejects a foreign key that uses a virtual generated column
on either the child or referenced parent side with error 3733 (`HY000`), naming
the virtual column. A parent virtual column with a unique key is still rejected.
The root regression now covers both sides and confirms that a rejected child
table is not published. Changing an existing foreign-key column into a virtual
generated column instead returned 3106 (`HY000`, changing the generated-column
storage status is unsupported); it does not reach foreign-key validation.

The expanded `foreign-key-alter-definitions` wire contract passes these
additional steps against MySQL 8.4.11 in
`torture/artifacts/runs/20261009T163829499-91044/contracts`. Earlier coverage
also passed all 242 steps under the mode-matched
[`lower_case_table_names=2` run](2026-10-09-identifier-case-policy.md).
