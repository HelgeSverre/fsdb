# Slow query table baseline

The digest-pinned MySQL 8.4.11 server was probed with `log_output=TABLE` and
`slow_query_log=ON`. A separate client set `SESSION long_query_time=0` and ran
`SELECT 42 AS fsdb_slow_probe`, `DO 1`, and a failing SELECT with no default
database. After the log was disabled, `mysql.slow_log` contained all four
completed statements, including the threshold-setting statement and the
failed SELECT.

| Statement | rows_sent | rows_examined | Other observed fields |
|---|---:|---:|---|
| `SET SESSION long_query_time=0` | 0 | 0 | query time was nonzero |
| `SELECT 42 AS fsdb_slow_probe` | 1 | 1 | lock time was zero |
| `DO 1` | 0 | 1 | lock time was zero |
| `SELECT * FROM fsdb_missing_slow_probe` | 0 | 0 | error 1046, still logged |

All rows had an empty `db`, zero `last_insert_id`/`insert_id`, and server ID
1 for this connection. `query_time` and `lock_time` have microsecond `TIME`
values. The existing fsdb `Slow_queries` counter measures elapsed time, but
fsdb does not yet track rows examined across executor paths or publish
`mysql.slow_log` rows. Those fields require an execution counter rather than
guessing from result-row counts. The global log destination also needs an
honest table/file policy before enabling slow logging.
