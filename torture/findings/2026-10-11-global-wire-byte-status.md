# Session and global wire-byte status

On a disposable MySQL 8.4.11 server, `SHOW SESSION STATUS LIKE
'Bytes_received'` returned 324 for the issuing connection while the next
`SHOW GLOBAL STATUS` returned 371. The corresponding sent-byte observations
were 756 and 971. These exact values depend on the client exchange; their
scope is the contract. After `FLUSH STATUS`, a session read returned 48
received bytes while the global value had continued to 572.

Fsdb previously used the issuing connection's `TransportMetrics` for both
scopes. `CountingStream` now adds each successful read or write to a
process-wide atomic total as well as its connection-local metric. Global
status reads that total; session status still reads its own connection.
`FLUSH STATUS` resets the issuing session's byte metrics without clearing
the global total.

A two-connection MySQL probe established the wider reset rule. Connection A
first reported 324 received bytes; connection B issued `FLUSH STATUS` while
A remained open. A's next session read reported 48, while global received
bytes continued to 789. Fsdb now weakly tracks live transport metrics and
resets every registered session on `FLUSH STATUS`. Atomic add/exchange keeps
that reset safe against concurrent network reads and writes.

The integration regression opens two real wire connections, exchanges an
8 KiB query and result on the second, and checks that the first connection's
global received and sent totals include those bytes while its session total
does not. It checks that a flush on either connection resets the other's
session bytes while global bytes keep accumulating.
