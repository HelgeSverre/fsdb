# Current-head multi-database write check

The Release CLI at `9239177f` ran the separate-process transfer workload
with eight databases, sixteen writers per database, thirty operations per
writer, and hot-account phases of 8/64/8. Each phase verified all account
balances and ledgers, with 412 committed transfers per database.

| Hot accounts | Wall time | Server CPU | Client CPU |
| ---: | ---: | ---: | ---: |
| 8 | 9.05 s | 30.07 s | 2.22 s |
| 64 | 10.18 s | 34.86 s | 2.08 s |
| 8 replay | 9.65 s | 23.86 s | 2.13 s |

The wide-account phase took 1.13× the first hot phase and 1.05× the replay.
These values are within the variation shown by the earlier unchanged-code
[monitor trial](2026-11-06-statement-access-monitor-trial.md). They confirm
correctness and a current throughput baseline, but do not show a scheduler
improvement or isolate where the server CPU time went. The shared statement
access monitor remains the measured design target from that trial.

Run `uv run --with pymysql benchmarks/scripts/external-multidb-write.py
--hot-accounts 8 64 8` after building the Release CLI. The server and its
databases are disposable.

## Host-contended rerun

The Release CLI at `99d2fb7a` passed the same eight-database 8/64/8
workload, again with 412 committed transfers per database and verified
balances and ledgers in every phase. Wall times were 21.23, 22.51, and
20.65 seconds; server CPU times were 30.50, 31.18, and 25.25 seconds.
At the time, host load average was 57.42 over one minute, unrelated
rendering and TypeScript processes were consuming cores, and the data
volume was 98% full with 20 GiB available. The CPU times remain near the
earlier run while wall times roughly doubled. This run confirms correctness
under host contention but cannot isolate an fsdb scheduling regression or
support a scheduling-policy change.

## Current-head low-load rerun

The Release CLI at `1f96d210` ran the same eight-database 8/64/8 workload
after the host's one-minute load average fell to 3.90. All phases again
verified every balance and ledger, with 412 commits per database.

| Hot accounts | Wall time | Server CPU | Client CPU |
| ---: | ---: | ---: | ---: |
| 8 | 8.52 s | 30.67 s | 2.18 s |
| 64 | 9.59 s | 37.16 s | 2.00 s |
| 8 replay | 8.98 s | 25.90 s | 2.13 s |

These wall times return to the earlier baseline range. This adds evidence
that the 21–23 second host-contended run did not by itself show a scheduler
regression. It remains an unchanged-code check, not a comparison against a
candidate scheduling policy.
