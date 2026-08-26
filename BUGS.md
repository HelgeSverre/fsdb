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

## Functional indexes are unavailable

Rails creates a unique index over `LOWER(external_id)`. `IndexDef` currently
stores column names rather than expressions, so the declaration is rejected
before the mysql2 adapter suite starts.

Reproduce with `just smoke-apps rails`.

## Background-job reservation and completion lose the selected row

Nextcloud can insert and enumerate background jobs, but selecting past a
nonexistent class and reserving two consecutive jobs can return null. Updating
a newly started job to a failed state can also report that no row changed. The
DB suite exposes these paths through `JobListTest` and `JobRunsTest`.

Reproduce with `just smoke-apps nextcloud`.

## Nextcloud recipient and external-share behavior diverges

The recipient search returns two of three expected users for one limit/offset
case. Creating a user external share raises a duplicate-key error on
`sh_external_mp` where MySQL completes the operation.

Reproduce with `just smoke-apps nextcloud`. The current run reaches 5,704 tests
and 30,621 assertions with one error and four failures; three are the
background-job cases above, with recipient search and external sharing making
up the other two failures.
