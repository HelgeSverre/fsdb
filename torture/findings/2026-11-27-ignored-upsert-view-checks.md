# Ignored upserts through checked views

`INSERT IGNORE ... ON DUPLICATE KEY UPDATE` now skips a row if the
proposed insert or duplicate update fails CHECK OPTION. Each skipped
row emits warning 1369. Successful inserts and updates contribute
to the affected-row count, and a skipped update preserves its
original row. [MySQL IGNORE behavior](https://dev.mysql.com/doc/refman/8.4/en/sql-mode.html#ignore-effect)

The fixture matched all 23 output rows against MySQL 8.4.11 in
memory and on disk. It covers VALUES, INSERT SELECT, and a prepared
batch in an open transaction. Transaction preparation preserves
source ordinals for skipped candidates and excludes them from its
prepared insert targets. Rollback restores the original table.
[SQL fixture](2026-11-27-ignored-upsert-view-checks.sql)

With seed 68115, another 120 explicit-key batches matched affected
rows, warnings, table contents, and transaction rollback. Each
eight-row batch varied the input keys and quantities and used
either LOCAL or CASCADED CHECK OPTION.

After a two-row batch with generated keys was entirely rejected,
the next successful insert received ID 1 in MySQL and ID 3 in both
Fsdb modes. The fixture uses explicit primary keys. Deprecation
warnings for `VALUES(column)` can also be reported twice when
Fsdb rewrites a view insert.
