# Table-size metadata uses InnoDB page accounting

Status: open. MySQL 8.4.11 oracle measured on the digest-pinned image in
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

Fsdb currently disagrees with itself: `InformationSchema.tablesRows` returns
16,384 for every base table, while `SHOW TABLE STATUS` sums in-memory text
payload bytes and derives average length from that sum. The latter is zero
for an empty table and only a few bytes for a one-row table. Replacing the
constant with a simple rounded payload size would still miss the 1,000-row
and 10,000-row cases, as well as the layout of long `TEXT` values. Closing
this gap needs a shared, tested page-allocation estimate or an explicit
policy for approximate physical metadata, with both reporting paths using
the same value.

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
-- Repeat the metadata reads; then insert ids 2 through 1000 and repeat.
-- Insert ids 1001 through 10000, ANALYZE TABLE small, and repeat.
CREATE TABLE large (id INT PRIMARY KEY, body TEXT);
INSERT INTO large VALUES
  (1,REPEAT('a',10000)),(2,REPEAT('b',10000)),(3,REPEAT('c',10000));
SELECT TABLE_ROWS,AVG_ROW_LENGTH,DATA_LENGTH,INDEX_LENGTH
  FROM information_schema.TABLES
  WHERE TABLE_SCHEMA='size_probe' AND TABLE_NAME='large';
SHOW TABLE STATUS LIKE 'large';
```
