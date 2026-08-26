# Application compatibility bugs

These failures are reproduced by the pinned external smoke targets. Declared
feature boundaries that have not caused an application failure remain in
`GAPS.md`.

## Composite primary-key order is not retained

`ColumnDef.PrimaryKey` records membership but not the order from `PRIMARY KEY
(second, first)`, so `SHOW INDEX` and schema APIs report declaration order
instead of key order. Renaming or replacing a composite primary key can expose
the wrong column sequence.

Reproduce with `just smoke-apps drupal`. The driver-specific schema suite fails
its primary-key discovery and replacement cases.

## Drupal schema limits and unsigned serial checks diverge

The Drupal MySQL schema suite still differs on negative values for unsigned
serial columns, index prefix normalization, oversized index definitions, and
oversized column definitions. These paths need MySQL-compatible errors and
limits rather than framework-specific special cases.

Reproduce with `just smoke-apps drupal`. The connection test passes; the schema
suite currently reaches 24 tests and 498 assertions with these remaining
failures.

## ALTER TABLE validates auto-increment columns before key changes finish

Changing a primary-key serial column can raise error 1075 while an `ALTER
TABLE` statement is still replacing its key. MySQL validates the completed
table definition, while fsdb rejects an intermediate definition in which the
auto-increment column has no key.

Reproduce with `just smoke-apps drupal`. The schema suite fails its primary-key
to serial and serial field-length changes.

## Named primary keys are rejected by ALTER TABLE

MySQL accepts an index name between `PRIMARY KEY` and the column list, as in
`ALTER TABLE migrations_lock ADD PRIMARY KEY migrations_lock_pkey(lock_key)`.
fsdb rejects this form with error 1064.

Reproduce with `just smoke-apps ghost`. Ghost stops while creating its
migration-lock table before the selected model integration test starts.

## InnoDB capability variables are missing

`SHOW VARIABLES LIKE 'innodb_file_per_table'` does not return the MySQL-compatible
`ON` value. Moodle treats the missing value as an unsupported database
configuration and refuses to initialize its PHPUnit schema.

Reproduce with `just smoke-apps moodle`.

## System-variable result types do not match MySQL

`SELECT @@max_allowed_packet` returns a string value where the MySQL protocol
and client stack expose an integer. Rails' mysql2 adapter fails while comparing
that value with the byte size of a schema batch.

Reproduce with `just smoke-apps rails`.

## The MySQL 8.4 foreign-key compatibility variable is missing

Magento reads and preserves `@@RESTRICT_FK_ON_NON_STANDARD_KEY` during schema
setup. fsdb reports an unknown system variable before Magento can create its
tables.

Reproduce with `just smoke-apps magento`.

## LIKE backslash escaping diverges from MySQL

WordPress' database suite observes different matches for patterns that combine
backslash escaping with `%` or `_`. The same queries pass against MySQL.

Reproduce with `just smoke-apps wordpress`.

## Explicit auto-increment values are rejected

WordPress uses explicit values in auto-increment columns during installation
and in its `REPLACE` coverage. fsdb rejects or rewrites those values instead of
preserving the MySQL behavior.

Reproduce with `just smoke-apps wordpress`.

## Stored routines and temporary tables are unavailable

WordPress reaches MySQL-specific coverage for stored procedures, `SHOW CREATE
PROCEDURE`, and temporary tables. Those statements fail before their expected
behavior can be asserted.

Reproduce with `just smoke-apps wordpress`.

## OCTET_LENGTH is missing

Nextcloud's query-builder coverage calls `OCTET_LENGTH` for empty, ASCII, and
multibyte strings. fsdb reports error 1305 instead of returning the encoded
byte length.

Reproduce with `just smoke-apps nextcloud`. Four data cases fail with the same
missing-function error.

## ALTER TABLE rejects ROW_FORMAT

Nextcloud's collation repair issues `ALTER TABLE name ROW_FORMAT = DYNAMIC`,
which MySQL accepts. fsdb rejects the table option with error 1064.

Reproduce with `just smoke-apps nextcloud`.

## Background-job reservation and completion lose the selected row

Nextcloud can insert and enumerate two background jobs, but the second
`getNext()` returns null after reserving the first. Updating a newly started job
to a completed state can also report that no row changed. The DB suite exposes
both paths through `JobListTest::testHasReservedJobs` and `JobRunsTest`.

Reproduce with `just smoke-apps nextcloud`.

## Four-byte filename handling is inconsistent

Nextcloud sees fsdb as lacking four-byte text support and rejects astral-plane
filenames such as emoji through a different validation path than MySQL. Five
path-verification data cases fail.

Reproduce with `just smoke-apps nextcloud`.

## Nextcloud recipient and external-share queries lose rows

The recipient search returns two of three expected users for one limit and
offset case. Creating a user external share also leaves the expected mount
collection empty. These failures remain after the complete application schema,
optional apps, Redis, and PHP database prerequisites initialize successfully.

Reproduce with `just smoke-apps nextcloud`. The canonical run reaches 5,704
tests and 29,889 assertions with 5 errors and 9 failures.

## Trigger bodies do not support conditional control flow

Compound trigger bodies support ordered DML and `SET NEW` statements, but not
`IF`, `ELSEIF`, or `ELSE`. Shopware reaches migration 205 before a `BEFORE
UPDATE` trigger containing nested conditional branches is rejected.

Reproduce with `just smoke-apps shopware`. This is the trigger-language gap
tracked in `GAPS.md`, not a failure of the preceding schema or wire protocol.
