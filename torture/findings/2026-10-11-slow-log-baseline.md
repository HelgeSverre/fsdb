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
| `SELECT id FROM fsdb_slow_scan_probe WHERE id > 1` | 2 | 3 | three-row temporary table without an index |

A follow-up mutation probe found `rows_examined=0` for a three-row literal
`INSERT`, `1` for an indexed single-row UPDATE and DELETE, and `3` for an
UPDATE of a three-row table without an index. A DELETE scanning the two rows
left after a prior deletion reported `2`. MySQL counts candidate rows inspected
by these mutations, not only rows changed.

An `INSERT … SELECT` that read three source rows and inserted two reported
`rows_sent=0`, `rows_examined=3`; fsdb's existing nested SELECT scope gives
the same result in a regression.

All rows had an empty `db`, zero `last_insert_id`/`insert_id`, and server ID
1 for this connection. `query_time` and `lock_time` have microsecond `TIME`
values.

Fsdb now publishes table-backed slow-log rows from completed text and binary
prepared statements. An execution scope counts source candidates before
filtering for scalar reads, single-table scans, `INSERT … SELECT` source reads,
and single-table UPDATE/DELETE, rather than guessing from result-row counts.
`SET SESSION long_query_time=0`
now counts itself, matching the oracle above. Broader join, full-text, and
other mutation accounting remains
incomplete, and `lock_time` currently reports zero. FILE output is refused
while enabled until there is a compatible sink.
