# Single-row WAL replay identity fast-path trial

The separate-process 8-database × 16-writer durable workload still has a
wide-account ceiling on the current head. A baseline 8/64/8-account run
completed in 8.82/10.75/9.42 seconds; each phase verified 412 committed
transactions per database and the final ledgers and balances.

A trial avoided allocating the consumed-row set and lazy fallback-image
dictionary for a one-row WAL update or delete when the preferred row ID's
current image already matched its before-image. All 31 replay-focused
tests passed, and the external workload again verified every ledger.
The candidate's 8/64/8 wall times were 9.01/10.48/9.56 seconds, with
31.75/42.24/27.91 server CPU seconds. A rebuilt unchanged-code replay
measured 8.81/10.12/9.47 seconds and 31.08/37.30/26.94 server CPU
seconds. The candidate lies within run variation and did not establish a
wall-time or CPU gain. It was reverted; the normal Release CLI was rebuilt.

The hot/wide difference persists even when row contention falls, so a
small replay-identity allocation is not the observed high-fan-out limit.
The earlier monitor timing and WAL stage profiles remain the stronger
leads for a structural scheduling change.

Command: `uv run --with pymysql benchmarks/scripts/external-multidb-write.py
--hot-accounts 8 64 8`. The script uses a disposable data directory and
separate client and server processes.
