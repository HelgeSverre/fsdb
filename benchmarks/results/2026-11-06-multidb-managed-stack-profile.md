# Separate-process multi-database managed-stack profile

The [external write checkpoint](2026-11-06-external-multidb-write-profile.md)
left the high-fan-out CPU increase unattributed after WAL replay sub-stage
timing. This follow-up sampled the Release fsdb server while the same client
ran eight databases and sixteen writers per database. All ledgers and account
balances passed verification.

An 8/64/8-account run with 30 operations per writer measured wall times of
9.47/12.68/10.49 seconds and server CPU of 31.15/49.10/25.93 seconds.
Client CPU stayed at 2.15–2.25 seconds per phase. A separate 64-account run
with 90 operations per writer kept the process alive for a 20-second
`dotnet-trace` capture. It verified 1,235 commits per database, with 30.81
seconds wall time and 97.34 server CPU seconds. The larger run is a profiling
vehicle, not a matched before/after throughput comparison.

The `dotnet-sampled-thread-time` trace was collected from the running server
with a 32 MiB buffer. Its inclusive report placed `QueryHandler.handle` on
31.76% of sampled thread-stack time, `Storage.withWriteLocksFor` on 5.53%,
and `QueryHandler.prepareTransactionWrite` on 6.08%. The exclusive report
placed `Monitor.Enter_Slowpath` on 16.59% and thread-pool semaphore waits on
over 47% of samples. The converted stack view put many monitor samples under
`QueryHandler.executeParsedCoreWith` and `TableLocks.withStatementAccessUnder`.
These are shares of sampled stack time including blocked and idle threads,
not CPU percentages or independent costs. Inlining also prevents the trace
from identifying the exact monitor in every stack.

An attempted `samply` attach captured only native addresses, so it could not
attribute fsdb methods. The managed trace changes the next diagnostic target:
measure the statement-access manager's lock hold and wait time separately
from row-lock waits and catalog publication during the same external workload.
It does not support changing worker count, WAL policy, or lock granularity yet.
