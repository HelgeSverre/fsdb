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

## Group-commit and WAL batch follow-up

Temporary counters in the group-commit queue and WAL batch writer split the
previous acknowledgement interval into queue admission, active batch work,
leader and follower completion, file open, encoding/write, fsync, replica
application, and rotation. The same external 8/64/8 workload again passed
every ledger. Wall time was 9.32, 10.84, and 9.37 seconds; client CPU was
2.21, 2.04, and 2.08 seconds; server CPU was 29.91, 40.93, and 23.30 seconds.

Periodic group-commit snapshots near the three phase ends were:

| Completed actions | Batches | Admission ms | Batch work ms | Leader ms | Follower ms |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 4,096 | 3,773 | 1.69 | 2,361 | 2,393 | 3,154 |
| 7,680 | 6,454 | 3.35 | 6,673 | 6,760 | 21,066 |
| 11,264 | 9,775 | 4.36 | 8,740 | 8,848 | 23,875 |

The wide interval added about 4.3 seconds of active batch work and
17.9 seconds of overlapping follower wait, compared with about 2.4 and
2.1 seconds of active work in the two hot intervals. Admission remained
below five milliseconds cumulatively. Wide-account writes also formed
larger batches, so the extra work cannot be explained by more batches alone.

WAL snapshots closest in event count to the same boundaries give these
approximate stage increments:

| Interval | Encode/write ms | Fsync ms | Replica apply ms | File open ms |
| --- | ---: | ---: | ---: | ---: |
| First 8-account | 150 | 929 | 1,219 | 0.4 |
| 64-account | 360 | 1,206 | 2,637 | 6.3 |
| Second 8-account | 198 | 1,027 | 778 | 0.2 |

The WAL checkpoints differ from the group-commit checkpoints and each
straddles a phase boundary by some operations. Timings are sums of stage
wall time, and follower waits overlap. The largest measured increase is
replica application of committed row events; fsync and encoding also rise.
This points to replay/index maintenance in the live WAL mirror as the next
code path to profile or optimize. The counters were reverted and the normal
Release binary rebuilt. The measurements do not support changing the
queue-admission or worker-reservation policy.

## Replay sub-stage and unchanged-key control

A temporary replay trace split row-image resolution, mutation application,
index maintenance, and row publication. It also counted identity fallbacks
and process GC activity. The external 8/64/8 workload again passed every
ledger, with 9.47, 11.34, and 9.49 seconds of wall time respectively. The
nearest cumulative replay checkpoints were:

| Replay calls | Resolve ms | Apply ms | Reindex ms | Publish ms | Identity fallbacks | Gen-0 GCs | Allocated GiB |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 6,656, near first hot end | 76 | 668 | 160 | 105 | 0 | 737 | 5.48 |
| 13,312, near wide end | 333 | 2,565 | 597 | 197 | 0 | 1,652 | 12.35 |
| 19,456, near second hot end | 400 | 3,048 | 699 | 232 | 0 | 2,516 | 18.86 |

The approximate wide interval adds 1.90 seconds of mutation-application
wall time, including 0.44 seconds in index maintenance; its resolution
time adds 0.26 seconds. Identity fallback never ran. Allocations and GC
counts also rose in every phase, with only a modestly higher count in the
wide interval, so this trace does not establish GC as the cause. These
stage times include possible thread preemption and are not CPU samples.

A candidate skipped replay reindexing when all indexed source columns were
unchanged. It preserved all ledgers, but a matched baseline/candidate/baseline
64-account run measured wall times of 12.54/11.63/11.49 seconds and server
CPU of 40.38/45.86/40.56 seconds. The candidate's wall time lies within
baseline variation and its CPU cost is higher. It was reverted, as were
all temporary counters, and the normal Release CLI was rebuilt. The
unattributed replay work includes row-store mutation, while the
larger server CPU difference also requires a method-resolved profile beyond
the WAL mirror.
