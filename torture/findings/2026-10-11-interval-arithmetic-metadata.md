# Interval arithmetic result metadata

The scalar differential run `--scenario scalar --seed 113 --max-rows 8
--batch-size 1` exposed a wire-level type mismatch. Fsdb returned the right
date text for `event_date + INTERVAL 1 YEAR`, but declared the result as
`DOUBLE`. The MySqlConnector comparison then failed while trying to parse
`2032-12-14` as a number.

The reduced MySQL 8.4.11 oracle uses one row:

```sql
CREATE TABLE t(d DATE, dt DATETIME);
INSERT INTO t VALUES ('2031-12-14','1983-10-09 21:59:01');
CREATE TABLE projected AS
SELECT d + INTERVAL 1 YEAR AS date_plus_year,
       d + INTERVAL 90 MINUTE AS date_plus_minutes,
       dt - INTERVAL 2 DAY AS datetime_minus_days
FROM t;
SHOW COLUMNS FROM projected;
SELECT * FROM projected;
```

MySQL declares the three columns `date`, `datetime`, and `datetime`. The
row is `2032-12-14`, `2031-12-14 01:30:00`, and
`1983-10-07 21:59:01`. Fsdb had the same values but declared all three as
`DOUBLE` through the generic arithmetic descriptor.

Interval `+` and `-` now use the existing `DATE_ADD`/`DATE_SUB` result-type
rule. A focused regression checks both text-query and prepared-statement
metadata alongside the row values. The original scalar differential seed
passes after the change with no differences.

A follow-up MySQL 8.4.11 probe found that TIME operands retain TIME values
for hour, minute, second, and microsecond intervals, including signed or
greater-than-24-hour results: `12:34:56 + INTERVAL 2 HOUR` gives `14:34:56`,
`23:30:00 + INTERVAL 2 HOUR` gives `25:30:00`, and
`01:00:00 - INTERVAL 2 HOUR` gives `-01:00:00`. DAY and MONTH intervals
instead produce DATETIME values based on the current date. Composite
`HOUR_MINUTE` remains TIME, while `DAY_HOUR` is DATETIME.

For those day-or-larger intervals, TIME is an offset from today's date:
`25:30:00 + INTERVAL 1 DAY` is 49 hours after `CURDATE()`, and
`-01:00:00 + INTERVAL 1 DAY` is 23 hours after it. Both return DATETIME.
MySQL takes that date from the session time zone. With `time_zone='-12:00'`,
`12:00:00 + INTERVAL 1 DAY` is 36 hours after that session's `CURDATE()`;
the same rule holds for `DATE_ADD`. Fsdb now uses the session date for both
forms, rather than the host's local date. Tests cover both `-12:00` and
`+14:00`, so a host-local implementation fails regardless of the host zone.

Fsdb previously converted all of those TIME operands through today's
DATETIME. It now preserves the original TIME for subday units and carries
the source unit through composite-interval parsing. A microsecond interval
reports six fractional digits. An overflowing TIME returns NULL with
MySQL's warning 1441, rather than a clamped value.
