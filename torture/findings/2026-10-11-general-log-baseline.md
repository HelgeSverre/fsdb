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

A separate `CREATE USER ... IDENTIFIED BY 'fixture_secret_314159'` probe
produced a log argument with `IDENTIFIED BY <secret>` rather than the cleartext
password. fsdb conservatively replaces the whole credential statement with
`[REDACTED CREDENTIAL STATEMENT]`; this preserves the secrecy property but
does not match MySQL's retained statement shape.

fsdb now records text queries to the table destination from its shared store,
so a rollback does not remove the log entry. The `NONE` destination suppresses
it as in MySQL. With the default `FILE` destination, enabling general logging
is refused explicitly because there is no compatible file sink yet. Prepared
executions and non-query wire commands are also outside this first path.
These boundaries remain open in `GAPS.md`.
