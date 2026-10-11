# Table-size metadata uses InnoDB page accounting

Status: partial. MySQL 8.4.11 oracle measured on the digest-pinned image in
`torture/compose.yaml`; fsdb's current reporting paths are identified below.

With `information_schema_stats_expiry=0`, MySQL reports the same `DATA_LENGTH`
and `AVG_ROW_LENGTH` through `INFORMATION_SCHEMA.TABLES` and `SHOW TABLE STATUS`.
The values represent allocated InnoDB pages, not the sum of row text lengths.

| Table state | TABLE_ROWS | DATA_LENGTH | AVG_ROW_LENGTH |
|---|---:|---:|---:|
| Empty `(id INT PRIMARY KEY, body VARCHAR(100))` | 0 | 16,384 | 0 |
| One row with `body='x'` | 1 | 16,384 | 16,384 |
| 1,000 narrow rows | 1,000 | 16,384 | 16 |
| 10,000 narrow rows after `ANALYZE TABLE` | 10,000 | 311,296 | 31 |
| Three rows with 10,000-character `TEXT` values | 3 | 49,152 | 16,384 |

The narrow and large tables used `InnoDB` with `ROW_FORMAT=Dynamic`. The
10,000-row result includes page splits and was measured after `ANALYZE TABLE`;
the other readings used session expiry zero without an explicit analyze.
`INDEX_LENGTH` was zero in these primary-key-only fixtures.

Fsdb now uses one page-rounded payload estimate in both reporting paths, with
a 16 KiB minimum and an average derived from the reported row estimate. It
samples at most 256 live row identities per table, so metadata reads do not
walk an entire large row store or a long run of deleted slots.
A diagnostic same-host run of ten repeated metadata queries on 10,000 rows
with 100-character text measured 32.7 ms before sampling and 9.0 ms after.
These are single-run Debug timings, useful for detecting the scan cost rather
than a stable performance claim.
That matches the audited empty and small-table cases, but it still cannot
reproduce InnoDB's page splits and long-`TEXT` layout: the 10,000-row and
three-long-row values above remain divergent. Closing the remaining gap needs
a more faithful allocation model or a deliberately approximate physical
metadata policy.

The same MySQL image retained `DATA_LENGTH=16384` through `ALTER TABLE ts ADD
COLUMN extra VARCHAR(20)` and `DELETE FROM ts` on a three-row narrow table.
With the default statistics cache, deletion still reported `TABLE_ROWS=3` and
`AVG_ROW_LENGTH=5461`; setting `information_schema_stats_expiry=0` changed
those to zero while `DATA_LENGTH` stayed 16 KiB. Fsdb matches that audited
small-table lifecycle; larger deleted tables can retain allocated pages that
its payload estimate forgets.

Oracle setup and measurements:

```sql
CREATE DATABASE size_probe;
USE size_probe;
SET SESSION information_schema_stats_expiry=0;
CREATE TABLE small (id INT PRIMARY KEY, body VARCHAR(100));
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='small';
SHOW TABLE STATUS LIKE 'small';
INSERT INTO small VALUES (1,'x');
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='small';
SHOW TABLE STATUS LIKE 'small';
SET SESSION cte_max_recursion_depth=2000;
INSERT INTO small
WITH RECURSIVE n AS
  (SELECT 2 AS x UNION ALL SELECT x+1 FROM n WHERE x<1000)
SELECT x,'x' FROM n;
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='small';
SHOW TABLE STATUS LIKE 'small';
SET SESSION cte_max_recursion_depth=12000;
INSERT INTO small
WITH RECURSIVE n AS
  (SELECT 1001 AS x UNION ALL SELECT x+1 FROM n WHERE x<10000)
SELECT x,'x' FROM n;
ANALYZE TABLE small;
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='small';
SHOW TABLE STATUS LIKE 'small';
CREATE TABLE large (id INT PRIMARY KEY, body TEXT);
INSERT INTO large VALUES
  (1,REPEAT('a',10000)),(2,REPEAT('b',10000)),(3,REPEAT('c',10000));
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='large';
SHOW TABLE STATUS LIKE 'large';
```
