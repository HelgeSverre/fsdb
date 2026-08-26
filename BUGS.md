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
suite currently reaches 24 tests and 502 assertions with these remaining
failures.

## Trigger bodies do not support conditional control flow

Compound trigger bodies support ordered DML and `SET NEW` statements, but not
`IF`, `ELSEIF`, or `ELSE`. Shopware reaches migration 205 before a `BEFORE
UPDATE` trigger containing nested conditional branches is rejected.

Reproduce with `just smoke-apps shopware`. This is the trigger-language gap
tracked in `GAPS.md`, not a failure of the preceding schema or wire protocol.
