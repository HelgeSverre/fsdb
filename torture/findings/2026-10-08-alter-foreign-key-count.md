# ALTER ADD FOREIGN KEY affected-row count

Status: fixed for the audited algorithm and foreign-key-check combinations.

Adding a foreign key with checks enabled to a populated table uses COPY and
reports the live row count in both MySQL 8.4.11 and fsdb. With checks disabled,
default and explicit INPLACE report zero; explicit COPY still reports the row
count. The shared ALTER plan controls validation, rebuilding, and result counts.

The original one-row mismatch is retained in
`2026-10-08-alter-foreign-key-count.json`. The broader
[algorithm and affected-row matrix](2026-10-08-alter-copy-counts.md) covers explicit
and default algorithms, enabled/disabled checks, empty tables, deleted rows,
and other COPY operations. Expecto and native wire regressions cover the fix.

The DELETE IGNORE contract retains CREATE TABLE constraints to keep its setup
focused on row deletion. No mismatch is enrolled in the known-gap allowlist.
