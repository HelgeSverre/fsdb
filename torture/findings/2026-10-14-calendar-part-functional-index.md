# Calendar-part functional indexes on date columns

Native MySQL 8.4.11 accepts `KEY ix_year ((YEAR(d)))` for a `DATE` column.
For a selective `WHERE YEAR(d)=2024` lookup it reports `ref` access with a
two-byte key, and returns both matching leap-day and year-end dates. The
function preserves NULL and the key follows an updated date.

Fsdb previously accepted the non-unique expression key but scanned for that
predicate. `YEAR`, `MONTH`, `DAY`/`DAYOFMONTH`, `QUARTER`, `DAYOFYEAR`,
`DAYOFWEEK`, and `WEEKDAY` now use a shared functional-index projection for
`DATE` and `DATETIME` columns, including
zero-date values. Their numeric probes and key-length metadata follow MySQL:
two bytes for `YEAR`, five for the other parts. The focused regressions
first failed with `ALL` access and now check physical `ref` access, NULL
results, updated keys, and key lengths. MySQL treats `DAY` and `DAYOFMONTH`
as equivalent index expressions; Fsdb does too.

The original `year-functional-index` differential contract compared source
values, equality and NULL lookups, and key maintenance after UPDATE with the
digest-pinned MySQL 8.4.11 image. All six steps matched in
`20261014T022558380-22746/contracts`; the full 151-case, 19,138-step run
retained the same ten unrelated differences as the previous pinned run.
The expanded `calendar-part-functional-index` contract also covers month/day
keys and the day alias. All fourteen steps matched in the pinned
`20261014T025122066-53352/contracts` run; the full 151-case, 19,146-step
run retained the same ten unrelated differences. A separate native 8.4.11
probe confirms `DATETIME` uses the same two-byte `YEAR` key and five-byte
month/day keys.

Native MySQL 8.4.11 also uses five-byte `ref` keys for `QUARTER`,
`DAYOFYEAR`, `DAYOFWEEK`, and `WEEKDAY`. On `2024-02-29` these return
1, 60, 5, and 3 respectively. `QUARTER` returns 0 for a zero month;
day-position functions return NULL for zero month/day and normalize an
invalid nonzero day where possible. Fsdb now projects those cases through
the same storage and probe path. The expanded contract covers their
results, NULLs, and UPDATE maintenance.

The first invalid-date contract run exposed Fsdb's old `QUARTER` behavior:
it returned NULL for zero month or zero day where MySQL returns 0 or the
nonzero month's quarter. Scalar evaluation and index projection now share
`quarterOfMonth`. All 31 steps of the final calendar-part contract matched
the digest-pinned MySQL 8.4.11 run at
`20261014T030734326-59866/contracts`; the 151-case, 19,163-step campaign
retained the same ten unrelated differences. The full local gate passed
3,514 tests, including focused scalar and indexed zero-date regressions.

Calendar-part functions over `TIMESTAMP` are intentionally outside this
physical-key path for now. In a native MySQL 8.4.11 probe, a row inserted as
`2024-12-31 23:30:00` at UTC projected `YEAR(ts)=2025` after setting the
session time zone to `+02:00`, yet an indexed `WHERE YEAR(ts)=2025` returned
no row. A separate January 31 UTC fixture projected February 1 after the
same time-zone change, while indexed `MONTH(ts)=2` and `DAY(ts)=1` both
returned no row. Fsdb should not claim a compatible physical lookup until the
time-zone-dependent behavior is characterized more fully.
