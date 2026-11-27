# Duplicate-key writes through nested views

An upsert through a single-table view can update a conflicting base
row even when the old row fails the view predicate. MySQL checks the
proposed insert first. If it finds a duplicate and the UPDATE changes
the stored row, it checks the updated row as well. A no-op following
type coercion skips that second check. LOCAL and CASCADED CHECK OPTION
retain their respective predicates.
[MySQL insert implementation](https://github.com/mysql/mysql-server/blob/mysql-8.4.11/sql/sql_insert.cc#L1943-L1961)
[MySQL CHECK OPTION](https://dev.mysql.com/doc/refman/8.4/en/view-check-option.html)

REPLACE needs to delete every conflicting row. A row hidden by the
view causes error 3059 and the statement leaves all prior rows and
conflicts intact. When the proposed insertion itself fails CHECK
OPTION, error 1369 still takes precedence. The rule applies to
VALUES, SET, and SELECT replacement forms. A two-row
`LOAD DATA LOCAL INFILE ... REPLACE` probe also matched error 3059
and rolled back the valid first input row in both Fsdb modes.
[MySQL error 3059](https://dev.mysql.com/doc/mysql-errors/8.4/en/server-error-reference.html#error_er_replace_inaccessible_rows)

The fixture matched 17 output rows and ten errors against MySQL
8.4.11 in both in-memory and durable Fsdb. It includes a prepared
upsert using a qualified `VALUES` reference, rejection of an unknown
view column at preparation, and rollback inside an open transaction.
[SQL fixture](2026-11-27-nested-view-duplicate-writes.sql)

A second run compared 240 generated writes using seed 69345. It
varied three plain or nested view definitions, duplicate primary
and secondary keys, hidden rows, insertion and replacement forms,
and candidate and assignment values. Error messages, affected-row
counts, and the stored table after each case matched in both modes.

The [`INSERT IGNORE` follow-up](2026-11-27-ignored-upsert-view-checks.md)
adds row skipping with warning 1369. The [checked join follow-up](2026-11-27-checked-join-upserts.md)
covers unchanged duplicates, changes that preserve the proposed partners,
and attempts to substitute a different partner.
