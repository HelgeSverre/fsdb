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
suite currently reaches 24 tests and 529 assertions with one error and seven
failures.

## SQL SECURITY INVOKER views are unavailable

Ghost reaches `CREATE OR REPLACE SQL SECURITY INVOKER VIEW` after completing
its preceding migrations. fsdb only models definer-security views, so accepting
the syntax without changing execution identity would be incorrect.

Reproduce with `just smoke-apps ghost`.

## Functional indexes are unavailable

Rails creates a unique index over `LOWER(external_id)`. `IndexDef` currently
stores column names rather than expressions, so the declaration is rejected
before the mysql2 adapter suite starts.

Reproduce with `just smoke-apps rails`.

## Temporary tables are unavailable

WordPress' database suite now passes its stored-procedure, explicit
auto-increment, REPLACE, and LIKE coverage. Its remaining failure creates a
temporary table, inserts a placeholder-shaped value, and reads it back. A
correct implementation needs a session-local catalog that shadows permanent
tables.

Reproduce with `just smoke-apps wordpress`. The current result is 508 tests,
724 assertions, one failure, and one skipped test.

## Background-job reservation and completion lose the selected row

Nextcloud can insert and enumerate two background jobs, but the second
`getNext()` returns null after reserving the first. Updating a newly started job
to a completed state can also report that no row changed. The DB suite exposes
both paths through `JobListTest::testHasReservedJobs` and `JobRunsTest`.

Reproduce with `just smoke-apps nextcloud`.

## Nextcloud recipient and external-share behavior diverges

The recipient search returns two of three expected users for one limit/offset
case. Creating a user external share raises a duplicate-key error on
`sh_external_mp` where MySQL completes the operation.

Reproduce with `just smoke-apps nextcloud`. The current run reaches 5,704 tests
and 30,540 assertions with 2 errors and 3 failures; the other remaining cases
are the background-job failures above.

## Trigger bodies do not support conditional control flow

Compound trigger bodies support ordered DML and `SET NEW` statements, but not
`IF`, `ELSEIF`, or `ELSE`. Shopware reaches migration 205 before a `BEFORE
UPDATE` trigger containing nested conditional branches is rejected.

Reproduce with `just smoke-apps shopware`. This is the trigger-language gap
tracked in `GAPS.md`, not a failure of the preceding schema or wire protocol.
