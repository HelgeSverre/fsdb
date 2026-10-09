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

MySQL compares ENUM and SET foreign keys by their encoded storage values,
even when the parent and child labels differ. An ENUM parent containing
`'a'` accepts an ENUM or SET child containing `'x'` when both encode as 1;
an update to encoded value 2 cascades to the child's own `'y'` label. This
holds in all four ENUM/SET parent-child combinations, and mismatched encoded
values return 1452. The `enum-set-foreign-key-bytes` contract checks lookup,
rejection, update cascade, and delete cascade. fsdb now uses the encoded
values for these FK operations. MySQL can also cascade an encoded value
outside a child ENUM's declared member count and retain its ordinal while
displaying an empty label. fsdb now preserves that internal ordinal through
referential lookup, display, numeric expressions, WAL replay, and snapshots. The
contract checks an out-of-domain cascade, scan-based comparisons, and
subsequent delete; the recovery regression also checks that the restored
foreign key can cascade-delete it. MySQL's FK support index misses the cascaded row for
equality predicates on either its old or new ordinal, even though an
`IGNORE INDEX` scan sees it. fsdb keeps its index consistent with the stored
row; indexed lookups of this invalid ordinal remain a deliberate divergence.

Further MySQL 8.4.11 probes accepted `BIT` paired with `BINARY` or `VARBINARY`
in either direction, regardless of declared lengths, but rejected `BIT` paired
with character `CHAR` or integer types. `ENUM` and `SET` member lists may differ
only when their encoded widths agree: crossing the eight-member `SET` boundary
or the 255-member `ENUM` boundary returned 3780, including cross-family pairs.
`DATETIME` and `TIMESTAMP` also referenced one another in either direction,
while `DATE` remained incompatible with both. Creation now applies these
same type-family and width rules.

The `bit-binary-foreign-key-bytes` contract checks the runtime key rule:
`BIT(9)` value 1 matches binary bytes `X'0001'`, but not the shorter
`X'01'`. Updates cascade the exact bytes from BIT to VARBINARY and from
BINARY to BIT, and the latter delete cascades too. fsdb now compares the
stored byte sequence for these cross-family keys and converts cascaded values
to the child column's representation.

MySQL also compares the stored bytes when two `BIT` foreign-key columns have
different declared widths. `BIT(1)` and `BIT(8)` value 1 match in either
parent/child direction because both occupy one byte; updates and deletes
cascade. `BIT(8)` value 1 and `BIT(9)` value 1 do not match because their
stored keys occupy one and two bytes. The same physical-key comparison now
covers fsdb's referential lookups and actions without changing same-width
indexed keys.

The expanded `bit-binary-foreign-key-bytes` contract passed all 38 steps
against MySQL 8.4.11 at
`torture/artifacts/runs/20261009T231344896-24141/contracts`. The full run
retained only the nine identifier-case differences from the pinned server's
different case mode. `just check` passed all 3,249 tests.

A further MySQL 8.4.11 probe updated a `BIT(8)` parent from 1 to 2 with a
referencing `BIT(1)` child. The cascade retained the out-of-range child value
2, and a later parent delete cascaded to that child. MySQL's indexed equality
lookup missed the invalid child value while an index-ignored scan found it;
fsdb keeps its index consistent with the stored row. `HEX()` renders stored
BIT values as unpadded numbers (`2` here), while a bit literal retains its
declared bytes (`HEX(b'00000001')` is `01`).
The expanded `bit-binary-foreign-key-bytes` contract passed all 47 steps at
`torture/artifacts/runs/20261009T232455021-53958/contracts`, and `just check`
passed all 3,249 tests. The full contract run retained the same nine
identifier-case differences elsewhere.

An existing foreign-key column has a stricter ALTER boundary than creation.
Changing `BIT`, `BINARY`, `CHAR`, `TIME`, `DATETIME`, `TIMESTAMP`, or `DECIMAL`
representation returned 1832 for a child column and 1833 for a referenced
parent column, even when the final child/parent types would be compatible.
An incompatible final type still returned 3780 first. Compatible `VARCHAR` and
`VARBINARY` widening within their length-prefix width succeeded; crossing
that width returned 1832. Appending `ENUM` or `SET` members within the encoded
width succeeded, while replacing or removing members returned 1832. fsdb now
checks the final type compatibility and constrained-column storage layout
before publishing the ALTER catalog. A key added in the same `ALTER` does not
impose the old column's layout restriction: changing `BIT(8)` to `BIT(9)` and
adding its compatible reference succeeded in either action order. The
`foreign-key-column-storage` wire
contract covers representative cases on both sides of a foreign key.

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

Stored generated columns can participate in foreign keys, but a generated
child column cannot use `ON UPDATE CASCADE`, `ON DELETE SET NULL`, or
`ON UPDATE SET NULL`: MySQL returns 3104 (`HY000`) before checking whether the
parent exists or whether the column is virtual. `ON DELETE CASCADE` works and
deletes dependent rows; `RESTRICT` and `NO ACTION` declarations work. A stored
generated parent column can use the audited actions. The root regression and
`foreign-key-generated-actions` wire contract cover these boundaries.

Changing a nullable child column to `NOT NULL` under an existing `ON DELETE
SET NULL` or `ON UPDATE SET NULL` constraint returns 1830 (`HY000`) and leaves
the column nullable. Dropping that constraint in the same ALTER permits the
change; trying to add the SET NULL constraint afterward returns 1830. MySQL
8.4.11 and fsdb now agree on the final column and constraint state, including
the error when a type change and the nullability change occur together.

The expanded `foreign-key-alter-definitions` wire contract passes these
additional steps against MySQL 8.4.11 in
`torture/artifacts/runs/20261009T163829499-91044/contracts`. Earlier coverage
also passed all 242 steps under the mode-matched
[`lower_case_table_names=2` run](2026-10-09-identifier-case-policy.md).
