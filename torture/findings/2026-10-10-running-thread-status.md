# Running-thread status

On disposable native MySQL 8.4.11, both `SHOW SESSION STATUS LIKE
'Threads_running'` and the global form reported 2 with one querying
connection, one sleeping connection, and the enabled event-scheduler daemon.
They reported 3 while the second connection ran `SELECT SLEEP(1)`. A direct
client query after `SET GLOBAL event_scheduler=OFF` reported 1 with only its
own query active. `SHOW FULL PROCESSLIST` confirmed the daemon, querying
connection, and sleeping connection before the setting changed.

Fsdb now derives `Threads_running` from non-sleeping registered connections
and the enabled event scheduler while a listener has registered connections.
The same value is exposed through session and global status. The scheduler's
enabled check is shared with its actual dispatch loop, so changing the global
setting cannot leave the status calculation using a separate rule.
