# Safe-update joined-mutation snapshot

<!--
revision: 224915e63a790f9d3493209d00ef5fd4bc832376
date: 2026-10-10T10:41:58Z
host: Darwin 24.6.0 arm64
dotnet: 10.0.401
fsdb: 8.4.0-fsdb, Release, in-memory
mysql: 8.4.11, native, innodb_flush_log_at_trx_commit=0, sync_binlog=0
-->

Both servers ran as native processes on the same host over local TCP, with
one client connection each. The fixture had 500 target rows keyed by `id`
and 10 keyed lookup rows. `sql_safe_updates` was ON. Each workload had 300
warmup calls followed by three blocks of 200 measured calls; target and
workload order was shuffled per block. Values below are median client-observed
milliseconds for each block. Rejected statements returned MySQL error 1175
on both servers, and the final target rows were unchanged.

| Workload | fsdb trials (ms) | MySQL trials (ms) |
|---|---:|---:|
| `SELECT 1` control | 0.293 / 0.216 / 0.196 | 0.052 / 0.051 / 0.048 |
| Single-table unindexed UPDATE, rejected | 0.211 / 0.164 / 0.161 | 0.079 / 0.041 / 0.043 |
| LEFT JOIN UPDATE filtered only on target, rejected | 0.368 / 0.243 / 0.198 | 0.074 / 0.051 / 0.050 |
| LEFT JOIN UPDATE filtered on lookup, accepted | 1.080 / 0.812 / 0.691 | 0.102 / 0.094 / 0.088 |

The joined UPDATE used `ON t.id=l.id` and assigned
`t.payload=t.payload`; the accepted form used `WHERE l.flag=0`, while the
rejected form used `WHERE t.payload=0`. The accepted no-op assignment still
passes through each engine's mutation path, so it does not isolate validation
cost. In-memory fsdb and no-fsync MySQL are non-durable comparison modes.

Fsdb's `SELECT 1` and mutation timings both fell across blocks, indicating
warmup or host variation beyond the initial warmup. These figures show the
current latency scale and a profiling target, not a before/after regression
or a precise speed ratio. The rejected path avoids mutation durability and
is the cleaner starting point for a later profile.
