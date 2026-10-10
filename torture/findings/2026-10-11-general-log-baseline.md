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

fsdb currently exposes the `mysql.general_log` schema but does not append
server events to it. A compatible implementation needs a server-wide logging
setting, a write path independent of the logged statement's transaction, and
a recursion guard for reads of the log table itself. A table-only partial
implementation should keep the unsupported file destination explicit; it
must not advertise logging that it does not perform. This is baseline
evidence, not a completed fix.
