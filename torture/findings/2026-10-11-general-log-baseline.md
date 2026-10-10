# General query log baseline

MySQL 8.4.11, using the digest-pinned `torture/compose.yaml` server, starts
with `general_log=OFF` and `log_output=FILE`. With `log_output=TABLE` and
`general_log=ON`, a query from another connection becomes a row in
`mysql.general_log` before that connection closes:

```sql
SET GLOBAL log_output='TABLE';
SET GLOBAL general_log=ON;
-- On a separate connection:
SELECT 314159 AS fsdb_general_log_probe;
-- On the first connection:
SELECT command_type, argument
FROM mysql.general_log
WHERE argument='SELECT 314159 AS fsdb_general_log_probe'
ORDER BY event_time DESC LIMIT 1;
```

The probe returned `Query` and the exact SQL text. The server's original
global settings were restored after the probe.

For `PREPARE p FROM 'SELECT ? AS fsdb_log_prepared_probe'` followed by
`EXECUTE p USING @v` with `@v=42`, MySQL records a `Prepare` entry with
`SELECT ? AS fsdb_log_prepared_probe` and an `Execute` entry with
`SELECT 42 AS fsdb_log_prepared_probe`. It also records the outer SQL
`EXECUTE p USING @v` as a `Query` entry.

A raw-protocol client produces `Connect` with an argument such as
`root@192.168.97.1 on mysql using TCP/IP`, `Init DB` with the selected
database name, and an empty `Quit` entry for `COM_QUIT`. A failed `COM_INIT_DB`
request did not create an `Init DB` row. Closing the TCP socket without
`COM_QUIT` also did not create a `Quit` row in the observed session. The
client's `COM_PING` did not create a general-log row.

A follow-up raw-protocol probe sent `COM_FIELD_LIST` with the `%` wildcard.
MySQL recorded `Field List` with argument `user %` for an existing table and
`fsdb_missing_field_list_probe %` for a missing table (which returned 1146).
On a table with `id`, `name`, and `note`, an empty wildcard returned all three
columns, `n%` and `N%` returned `name` and `note`, `no_e` returned only `note`,
and an unmatched pattern returned an EOF without column definitions. Fsdb now
applies its shared iterative SQL-LIKE matcher to these column names.
`COM_RESET_CONNECTION` and `COM_SET_OPTION` produced no general-log entries
in the same probe.
Another raw-protocol probe recorded empty-argument `Statistics` and `Debug`
entries for `COM_STATISTICS` and `COM_DEBUG`; `COM_PROCESS_INFO` returned
1047 on the pinned MySQL server.

`log_output='NONE,FILE'` is accepted while `general_log=ON`; the `NONE`
destination suppresses output despite the `FILE` member. fsdb permits this
combination without requiring a file sink.

A separate `CREATE USER ... IDENTIFIED BY 'fixture_secret_314159'` probe
produced a log argument with `IDENTIFIED BY <secret>` rather than the cleartext
password. fsdb conservatively replaces the whole credential statement with
`[REDACTED CREDENTIAL STATEMENT]`; this preserves the secrecy property but
does not match MySQL's retained statement shape.

fsdb now records text queries and SQL/binary prepared `Prepare` and `Execute`
events to the table destination from its shared store, so a rollback does not
remove the log entry. Bound values are rendered in `Execute` arguments while
credential statements remain redacted. The `NONE` destination suppresses
output as in MySQL. With the default `FILE` destination, enabling general
logging is refused explicitly because there is no compatible file sink yet.
`COM_FIELD_LIST` is also recorded with its wildcard, including a failed lookup;
`COM_STATISTICS` and `COM_DEBUG` record their empty-argument events.
Other wire commands remain outside this path. These boundaries remain open in
`GAPS.md`.
