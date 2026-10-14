# Keyless wire-write server stages

<!--
sha: 6d239eec
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [write-stage timing](2026-10-15-wire-write-stage-timing.md) showed that both
WAL completion and other handler work grew with writer count. This follow-up
timed nested phases for the same keyless `INSERT` workload. Temporary counters
were added to `QueryHandler.handle`, its normalized dispatch, parsing,
`Executor.executeAs`, stored-function lookup, and the WAL completion action in
`Storage.preparePublishedEvents`. They were removed after measurement, and the
normal Release CLI was rebuilt successfully.

Both runs used WAL-backed FSDB and the same-host native MySQL 8.4.11 control,
with 1,000 rows each in the seeded users, orders, and articles tables. The load
recipe used one trial, two seconds of warmup, ten measured seconds, and either
one or eight writers. Each counter was cumulative; the table divides the
difference between its first and last 20,000-write snapshots by the number of
completed writes in that interval. The one-writer interval covers 40,000
writes; the eight-writer interval covers 120,000.

| Nested wall-time stage, mean µs/write | One writer | Eight writers |
|---|---:|---:|
| Whole query handler | 94.9 | 457.8 |
| Normalized dispatch | 90.6 | 439.8 |
| SQL parsing | 6.7 | 19.8 |
| `Executor.executeAs` | 66.7 | 344.5 |
| Stored-function lookup | 0.1 | 3.9 |
| WAL completion action | 48.6 | 273.7 |

These are **wall times summed across concurrent requests**, not process CPU
time. The phases are nested and must not be added together. WAL completion is
timed independently and its snapshots are not paired request-for-request with
the handler snapshots, so differences between those rows are approximate.
The temporary timing code also adds overhead; the table is useful for locating
wait growth, not as an uninstrumented latency benchmark.

From one to eight writers, handler time grew by about 363 µs per write,
`executeAs` by 278 µs, and WAL completion by 225 µs. Parsing and stored-function
lookup grew much less. This makes another parser or function-registry rewrite
an unlikely answer to this workload. WAL completion accounts for a large share
of the observed wait growth, but the remaining execution and dispatch time
also warrants investigation. The profile does not distinguish active work from
waiting inside `executeAs`, or prove a particular lock is responsible.

The next experiment should sample or time the execution/publication path
around WAL completion with explicit wait-versus-active-work separation,
alongside the durable MySQL host-load control. A scheduling or WAL policy change
should require repeated before/after throughput evidence; earlier retained-file
and yield experiments did not establish a reliable gain.
