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

The integration regression opens two real wire connections, exchanges an
8 KiB query and result on the second, and checks that the first connection's
global received and sent totals include those bytes while its session total
does not. It also checks the two `FLUSH STATUS` lifetimes. Other active
connections' session-byte reset behavior remains unaudited.
