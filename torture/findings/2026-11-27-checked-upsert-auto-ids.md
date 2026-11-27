# Generated IDs in ignored upserts through checked views

MySQL 8.4.11 evaluates CHECK OPTION against a provisional ID of
zero when the candidate needs a generated ID. A rejected candidate
never requests a generated ID. The first eligible candidate reserves
space for the full VALUES list, including explicit IDs and rejected
rows. With `NO_AUTO_VALUE_ON_ZERO`, a supplied zero stays explicit.
[MySQL AUTO_INCREMENT handling](https://dev.mysql.com/doc/refman/8.4/en/innodb-auto-increment-handling.html)

The fixture matches all 33 output rows and the empty error stream
against the digest-pinned MySQL oracle in both Fsdb modes. It covers
full rejection, mixed rejection, explicit IDs, SQL mode changes, and
a prepared transaction that rolls back. A seeded run (86123) matched
another 120 batches and 951 output rows in each mode, varying the
input forms, view checks, and transaction outcomes. The WAL regression
covers autocommit, explicit commit, and rollback without a checkpoint.
[SQL fixture](2026-11-27-checked-upsert-auto-ids.sql)

The [INSERT SELECT follow-up](2026-11-27-checked-upsert-select-auto-ids.md)
covers the separate reservation rules for selected rows.

Candidate ID reuse remains open. When an eligible candidate with a
generated ID finds a secondary-key duplicate, MySQL can reuse the ID
for the next row. In the audited mixed batch, MySQL assigns the fresh
rows IDs 2 and 3, while Fsdb assigns IDs 3 and 4. Both assign ID 6
to the next statement.
