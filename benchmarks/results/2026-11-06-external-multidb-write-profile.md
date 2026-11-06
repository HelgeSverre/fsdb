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

## Server-stage follow-up

A temporary counter trial timed publication including `Store.CommitLock` acquisition, the
returned durable acknowledgement action, and every storage-row
`Monitor.Wait`. It emitted cumulative snapshots every 512 acknowledgements
to the separate server log. The probes were removed and the normal Release
CLI was rebuilt after the run. The same 8/64/8 sequence passed all ledgers:

| Hot accounts | Wall s | Client CPU s | Server CPU s |
| ---: | ---: | ---: | ---: |
| 8 | 9.885 | 2.317 | 33.19 |
| 64 | 11.420 | 2.140 | 42.02 |
| 8 | 9.822 | 2.216 | 25.29 |

The nearest cumulative checkpoints to each phase end were:

| Acknowledgements | Publication ms | Acknowledgement ms | Row waits | Row-wait ms |
| ---: | ---: | ---: | ---: | ---: |
| 3,584, near first 8-account end | 27 | 5,262 | 8,355 | 433,830 |
| 7,680, near 64-account end | 191 | 23,301 | 9,751 | 545,574 |
| 11,264, near second 8-account end | 237 | 28,293 | 18,129 | 971,227 |

These checkpoints cross phase boundaries by up to a few hundred operations;
their differences are directional estimates, not exact per-phase totals.
The wide-account interval adds roughly 1,400 row waits versus more than
8,000 in either hot interval, but about 18 seconds of cumulative durable
acknowledgement time versus about five seconds in the hot intervals. Those
acknowledgement times overlap across workers and cannot be added to wall
time. The counter wraps the group-commit action, so it combines queue wait,
batch work, fsync, notification, and any leader drain after its own signal.
The result directs the next profile to those group-commit sub-stages under
the same external workload. It does not yet justify changing worker or WAL
policy.
