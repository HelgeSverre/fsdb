# UPDATE IGNORE assignment coercion

MySQL 8.4.11 converts invalid non-NULL SET values during assignment. A
later SET expression or BEFORE trigger sees the converted value. For
example, assigning `'bad'` to an `INT` under `UPDATE IGNORE` stores zero
and emits warning 1366. Fsdb previously returned an error and left the
row unchanged. Ordinary strict `UPDATE` still rejects the assignment.

MySQL handles a NULL assignment to a required column during SET when
there is no BEFORE trigger. It emits warning 1048 and uses the type's
implicit value, even when the column declares another default. If a
BEFORE trigger exists, its `NEW` row can still see and repair NULL. A
NULL left by the original UPDATE takes the implicit value afterward.
If the trigger itself leaves a required column NULL, MySQL emits 1048
and skips the target row. Writes made by the trigger body can persist in
that case. An explicit NULL for a required geometry column in the
audited case remains a statement error (3673).

The [SQL fixture](2026-11-27-ignored-update-coercions.sql) matched all 48
output lines and the error stream in memory and on disk against the
pinned MySQL 8.4.11 image. It covers integer clipping, truncated strings,
invalid floating-point and temporal input, ENUM and JSON, generated
columns with CHECK constraints, and joined updates. A further 120
seeded batches per mode matched MySQL for the audited integer coercions,
row order, warnings, and transaction outcomes.
