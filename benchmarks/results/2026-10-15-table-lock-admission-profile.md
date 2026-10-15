# Table-lock admission under durable keyless writes

<!--
sha: aafc996b
date: 2026-10-15
os: macOS 15.6 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

A temporary timing probe around `TableLocks.withStatementAccessUnder`
separated physical-access preparation, time through statement-lock admission,
and release. It accumulated stopwatch ticks in the FSDB process and wrote a
snapshot every 20,000 statements. The probe was removed afterward, and the
normal Release CLI rebuilt with zero warnings. The current source has no
profiling branch.

The workload used fresh WAL-backed FSDB directories and durable native MySQL
8.4.11 on the same host, with one or eight wire writers, two trials, two
seconds of warmup, and ten measured seconds. Each snapshot is cumulative from
process start, so it includes seeding and warmup; the last snapshot per FSDB
trial is shown. These timings are wall time under concurrency, not CPU time.

| Writers | Trial snapshot | Prepare µs/statement | Admit µs/statement | Release µs/statement |
|---:|---:|---:|---:|---:|
| 1 | 60,000 | 0.22 | 1.32 | 0.49 |
| 1 | 20,000 | 0.41 | 3.28 | 1.12 |
| 8 | 100,000 | 1.41 | 5.67 | 2.82 |
| 8 | 120,000 | 1.00 | 3.95 | 2.36 |

The instrumented end-to-end means were 4,793 FSDB versus 13,282 MySQL
inserts/sec at one writer and 10,111 versus 28,649 at eight writers. Both
targets varied markedly across two trials (FSDB RSD 24% and 6%, respectively),
and the instrumentation itself adds work. These throughputs cannot establish
a current-head speed trend. The earlier uninstrumented
[durable checkpoint](2026-10-15-current-head-write-follow-up.md) remains the
better throughput reference.

Admission and release together cost roughly 6–9 µs per write in the two
eight-writer snapshots. That is much smaller than the hundreds of microseconds
of concurrent wall time measured inside the handler and WAL completion in the
[server-stage profile](2026-10-15-keyless-server-stage-profile.md). The
measurements came from different runs, so their stages should not be added
or divided into a precise percentage. They do rule out this lock-manager
boundary as the dominant explanation for the durable wire-write gap on this
workload. The next scheduling investigation should stay with executor work
and WAL completion rather than change table-lock semantics on this evidence.
