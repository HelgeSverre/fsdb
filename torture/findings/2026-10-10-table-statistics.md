# Cached table statistics and ANALYZE TABLE

MySQL 8.4.11 caches the dynamic columns of `INFORMATION_SCHEMA.TABLES`.
The session variable `information_schema_stats_expiry` defaults to 86400
seconds; setting it to zero bypasses the cache. `ANALYZE TABLE` refreshes the
cached values. These rules are documented in the [MySQL TABLES reference](https://dev.mysql.com/doc/refman/8.4/en/information-schema-tables-table.html)
and [server-variable reference](https://dev.mysql.com/doc/refman/8.4/en/server-system-variables.html).

A disposable native MySQL 8.4.11 probe created an indexed table and observed:

| Action | `TABLE_ROWS` | `DATA_LENGTH` | `INDEX_LENGTH` |
|---|---:|---:|---:|
| Create and first read | 0 | 16384 | 16384 |
| Insert three rows | 0 | 16384 | 16384 |
| `ANALYZE TABLE` | 3 | 16384 | 16384 |
| Delete one row | 3 | 16384 | 16384 |
| `ANALYZE TABLE` again | 2 | 16384 | 16384 |

In one connection, a separate probe read `TABLE_ROWS` as 0 after creation
and after three inserts, then 3 and 2 after setting
`information_schema_stats_expiry=0` and deleting one row, respectively.
These small-table observations establish the cache lifecycle, not a general
InnoDB row-estimation algorithm or physical page accounting.

fsdb currently computes `INFORMATION_SCHEMA.TABLES.TABLE_ROWS` from
`RowsArray.Length` on each read. Its `ANALYZE TABLE` branch only returns a
status row. Closing this gap requires a per-table statistics snapshot whose
lifecycle is explicit across DML, ANALYZE, session expiry policy, and
persistence; changing the display expression alone would give inconsistent
results.
