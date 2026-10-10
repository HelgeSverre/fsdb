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

The same snapshot now stores index cardinality. In a native MySQL 8.4.11
probe, a three-row table with `(category,id)` values `(1,1)`, `(1,2)`,
`(2,3)` reported 2 distinct first-key values and 3 distinct two-key values
after `ANALYZE TABLE`; deleting the third row left those estimates unchanged.
FULLTEXT and SPATIAL indexes reported the three-row count in a separate
duplicate-value probe. fsdb computes exact B-tree prefix counts with the same
key projection used by its maintained indexes, and uses the row count for
FULLTEXT/SPATIAL entries. The snapshot survives WAL and snapshot recovery;
the native contract checks `INFORMATION_SCHEMA.STATISTICS` and `SHOW INDEX`.
Zero-expiry index reads remain an open edge. In an isolated MySQL probe, a
first read after three inserts returned secondary and primary cardinalities
of 2 and 3; after a delete, `PRIMARY` fell to 2 while the secondary estimate
remained 2. Under the differential harness's preceding metadata reads, the
same three-row first read returned 1 for both indexes, and a composite
secondary's second prefix changed from 3 to 2 after a delete. The existing
fsdb cache cannot derive those state-dependent InnoDB samples. It reports
the cached estimate when present rather than fabricating a sampled value.

The remaining divergence is InnoDB's approximate row sampling, page-derived
`DATA_LENGTH`/`INDEX_LENGTH` and `AVG_ROW_LENGTH`, and its sampled
cardinalities on larger indexes. fsdb's estimates are exact snapshots at
refresh time; they do not imitate InnoDB's sampling error or physical pages.
