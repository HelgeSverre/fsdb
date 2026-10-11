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
