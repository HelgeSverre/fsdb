# YEAR functional index on date columns

Native MySQL 8.4.11 accepts `KEY ix_year ((YEAR(d)))` for a `DATE` column.
For a selective `WHERE YEAR(d)=2024` lookup it reports `ref` access with a
two-byte key, and returns both matching leap-day and year-end dates. The
function preserves NULL and the key follows an updated date.

Fsdb previously accepted the non-unique expression key but scanned for that
predicate. `YEAR` now uses the shared functional-index projection for `DATE`
and `DATETIME` columns, including zero-date values. Its numeric probe and
key-length metadata follow the same two-byte result that MySQL reports.
The focused regression first failed with `ALL` access and now checks physical
`ref` access, NULL results, an updated key, and key length.

The `year-functional-index` differential contract compares source values,
equality and NULL lookups, and key maintenance after UPDATE with the
digest-pinned MySQL 8.4.11 image. All six steps matched in
`20261014T022558380-22746/contracts`; the full 151-case, 19,138-step run
retained the same ten unrelated differences as the previous pinned run.
The full solution passed 3,511 tests. A separate native 8.4.11 probe confirms
`DATETIME` uses the same two-byte `ref` key.

`YEAR(TIMESTAMP)` is intentionally outside this physical-key path for now.
In a native MySQL 8.4.11 probe, a row inserted as
`2024-12-31 23:30:00` at UTC projected `YEAR(ts)=2025` after setting the
session time zone to `+02:00`, yet an indexed `WHERE YEAR(ts)=2025` returned
no row. Fsdb should not claim a compatible physical lookup until the
time-zone-dependent behavior is characterized more fully.
