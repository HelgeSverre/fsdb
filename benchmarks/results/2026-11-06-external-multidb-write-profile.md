# Separate-process multi-database write profile

The earlier [row-wait profile](2026-11-05-multidb-row-wait-profile.md)
ran the torture client and fsdb server in one process. Its thread-pool
pending count could not distinguish client pressure from server pressure.
The new [external workload](../scripts/external-multidb-write.py) launches
the Release fsdb CLI in a child process and sends the same deterministic
two-account transfers from a separate Python process. It uses the same
8 databases × 16 writers × 30 operations, 64 accounts, rollback every
seventh operation, and seed 204 as the earlier high-fan-out shape. Each
database's final balances, versions, and ledger are checked against the
operation plan; every phase had 412 commits and 68 rollbacks per database.

The client uses PyMySQL threads and a per-database iteration barrier. The
server uses its normal durable data directory, unlike the earlier in-memory
`FsdbSubject`. These numbers are therefore a within-script hot/wide
comparison, not a direct speedup calculation against that earlier profile.
Both processes were freshly started for the eight-database A/B/A run.

| Phase | Hot accounts | Wall seconds | Client CPU seconds | Server CPU seconds |
| --- | ---: | ---: | ---: | ---: |
| A1 | 8 | 9.323 | 2.217 | 29.89 |
| B | 64 | 10.855 | 2.057 | 40.11 |
| A2 | 8 | 9.542 | 2.093 | 24.02 |

A separate one-database A/B/A run with the same per-database plan took
0.978, 0.616, and 0.824 seconds respectively. The eight-database wall
times were about 1.2–1.4 times an eightfold serial projection for the
8-account phases, and about 2.2 times for the 64-account phase. The
baseline process and high-fan-out process were separate runs, so these
ratios are indicative rather than a matched scaling proof.

Widening the active account set did not improve wall time in the durable
separate-process run, and server CPU rose while client CPU remained near
two seconds. This rejects a simple explanation in which removing row waits
alone closes the high-fan-out scheduling gap. It does not identify whether
the extra server work is in WAL, row-version publication, query execution,
or another stage. The next useful profile should time those server stages
in this separate-process workload before changing worker reservation or
lock policy.
