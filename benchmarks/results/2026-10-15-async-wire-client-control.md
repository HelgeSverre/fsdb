# Async MySqlConnector control for durable keyless inserts

<!--
sha: ee249072 with a temporary async benchmark variant, reverted afterward
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [embedded-versus-wire control](2026-10-14-embedded-wire-write-control.md)
left open whether blocking client calls were driving FSDB's lower wire-write
throughput. A temporary variant of `LoadBenchmarks.fs` ran the same
`insert-keyless` SQL through `MySqlCommand.ExecuteNonQueryAsync` on one
connection per worker. The ordinary variant used `ExecuteNonQuery` on eight
dedicated worker tasks. Both variants opened connections before measurement,
used a fresh WAL-backed FSDB directory, verified the committed row count,
and ran the same variant against durable MySQL as a host control. MySQL kept
commit-time flush enabled and binary logging disabled.

Four alternating trials used one second of warmup followed by five measured
seconds, eight workers, and the same data sizes. The temporary variant was
reverted and the ordinary Release benchmark was rebuilt afterward.

| Order | Client command | FSDB + WAL inserts/sec | MySQL inserts/sec | FSDB/MySQL |
|---:|---|---:|---:|---:|
| 1 | synchronous | 12,177 | 45,168 | 0.27 |
| 2 | asynchronous | 11,645 | 33,444 | 0.35 |
| 3 | asynchronous | 11,967 | 33,477 | 0.36 |
| 4 | synchronous | 11,942 | 46,498 | 0.26 |

FSDB's two-run mean was about 12,060 synchronous and 11,806 asynchronous
inserts/sec. That small decrease is within the limits of these short trials;
there is no evidence of an FSDB throughput gain. MySQL's mean fell from about
45,833 to 33,461 inserts/sec in the async variant, so the improved ratio is
caused by the slower host control. These results do not make async execution
slower in general; they apply to this one-connection-per-worker client harness
and keyless autocommit workload.

Changing the client call path is not the scheduling fix for the observed FSDB
wire gap. The [stage timing](2026-10-15-wire-write-stage-timing.md) instead
shows growth in both WAL completion and the handler's non-WAL work from one
to eight writers; isolate those server stages before changing completion
policy or adding a client-side async abstraction.
