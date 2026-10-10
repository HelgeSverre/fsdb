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

fsdb now stores an optional per-table row estimate. The first metadata read
populates it, ordinary row writes retain it, and `ANALYZE TABLE` refreshes it.
The session's `information_schema_stats_expiry=0` reads the live row count;
positive expiry values refresh an expired estimate. The estimate survives WAL
and snapshot recovery. A native differential contract covers first-read
timing, stale counts after writes, ANALYZE refresh, and the zero-expiry path.

The remaining divergence is InnoDB's approximate row sampling, page-derived
`DATA_LENGTH`/`INDEX_LENGTH` and `AVG_ROW_LENGTH`, and per-index cardinality
estimates. fsdb's row estimate is an exact snapshot of its live row count at
refresh time; it does not imitate InnoDB's sampling error or physical pages.
