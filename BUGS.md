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
