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
