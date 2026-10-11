# Connection and status-uptime counters

A disposable MySQL 8.4.11 server reported `Connections`,
`Max_used_connections`, and `Uptime_since_flush_status` in both GLOBAL and
SESSION status. In one probe, the values were 7, 1, and 9 seconds in both
scopes. A successful `FLUSH STATUS` left the first two values unchanged and
reset `Uptime_since_flush_status` to zero. Exact values depend on earlier
client connections and elapsed time; the scopes and reset behavior are the
contract.

Fsdb now counts accepted connection attempts before authentication, raises a
process-wide high-water mark when a connection is registered, and tracks the
last successful status flush. `FLUSH STATUS` resets the uptime baseline along
with active sessions' byte metrics. Starting a new listener also initializes
the uptime baseline. The counters are process-wide in both status scopes.

The wire integration test opens two connections, checks the live attempt and
high-water counters, and checks that the status uptime restarts after a flush.
MySQL also increments `Aborted_connects` for a rejected password, a peer that
closes after receiving the server handshake, and an unknown initial database.
The observed value rose from 0 to 1, 2, and 3 across those cases, and a
subsequent `FLUSH STATUS` left it at 3. Fsdb now counts unsuccessful initial
handshakes, including authentication and database-selection failures, while
normal disconnections after a completed handshake do not change this status.
The wire regression checks the unknown-database case through a real client.
`Threads_created` remains absent because Fsdb does not create one worker
thread per client connection; exposing a fabricated value would obscure that
difference in execution model.
