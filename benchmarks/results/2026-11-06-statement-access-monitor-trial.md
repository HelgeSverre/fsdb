# Statement-access monitor timing and writer-registration trial

The [managed-stack profile](2026-11-06-multidb-managed-stack-profile.md)
showed many blocked stack samples in statement execution. Temporary counters
around `TableLocks.withStatementAccessUnder` split cumulative time waiting to
enter its shared manager monitor, time inside that monitor, and time waiting
for statement-level table availability. The same separate-process 8/64/8
workload used eight databases, sixteen writers per database, and thirty
operations per writer. Every run verified all ledgers and account balances.

The candidate skipped `WaitingWriters` registration when the statement's
table access was already available under the manager monitor. It preserved
the existing waiting-writer path for blocked statements. A
baseline/candidate/baseline replay measured:

| Version | 8-account wall | 64-account wall | 8-account wall | 64-account server CPU |
|---|---:|---:|---:|---:|
| Baseline | 8.74 s | 10.55 s | 8.97 s | 44.39 s |
| Candidate | 8.98 s | 10.18 s | 9.37 s | 38.18 s |
| Baseline replay | 9.19 s | 9.72 s | 9.27 s | 35.83 s |

The nearest 4,096-statement counter checkpoints bracket each phase only
approximately. The wide-account interval added about 67.0, 68.9, and 115.1
seconds of cumulative monitor-entry wait in the three runs. It added about
1.2, 1.1, and 1.9 seconds of measured monitor-body time. Statement-level
table-availability waits were zero in all sampled checkpoints. Wait totals
overlap across 128 workers and are not wall time. They vary substantially
between the two unchanged-code runs.

This establishes that the monitor-entry contention is distinct from
statement-level table-lock waiting in this workload. It does not establish
that avoiding uncontended writer registration improves throughput: the
candidate lies within baseline variation. The trial and all counters were
reverted, and the normal Release CLI was rebuilt. A useful next design check
is whether independent databases can coordinate ordinary statement access
without one shared manager monitor while preserving explicit table locks and
global read-lock semantics.
