# Keyless-write parse, execution, and acknowledgement stages

<!--
sha: 4537d4ef with temporary stage counters, reverted after measurement
date: 2026-10-17
os: Darwin arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [current-head checkpoint](2026-10-17-keyless-write-scaling-checkpoint.md)
confirmed weak scaling in both WAL-backed and in-memory fsdb. To locate the
wire handler's non-WAL time, temporary `Stopwatch.GetTimestamp` counters
measured `parseStatement`, the rest of `executeStatement` after parsing, and
the returned durable acknowledgement action in `Storage.preparePublishedEvents`.
The parse/execution counter selected only `INSERT INTO keyless_events`.
Acknowledgement counts included setup writes, so the table uses the last
complete 20,000-operation delta, after setup and warmup. The timers measured
wall time, including waits; concurrent call times must not be summed as CPU
time. `Stopwatch.Frequency` was 1,000,000,000 ticks/s.

The same native-host WAL-backed fsdb and durable MySQL 8.4.11 load used fresh
seeded databases. One-writer measurement lasted seven seconds and eight-writer
measurement nine seconds, each after one second of warmup. Each row is one
trial, so the rates are a directional host-load control, not a performance
change claim.

| Writers | fsdb inserts/s | MySQL inserts/s | Parse µs/call | Post-parse execution µs/call | Durable acknowledgement µs/call | Post-parse time outside acknowledgement µs/call |
|---:|---:|---:|---:|---:|---:|---:|
| 1 | 7,255 | 17,239 | 6.97 | 75.91 | 47.24 | 28.67 |
| 8 | 11,886 | 47,877 | 19.18 | 434.24 | 287.25 | 146.99 |

The last column subtracts nested timings from the same run; it is not a
separate stopwatch. Parsing increases with contention but remains a small
part of the eight-writer execution path. Durable acknowledgement accounts for
about two thirds of measured post-parse execution at eight writers. The time
outside acknowledgement also grows by about 118 µs per call from one to
eight writers. That excludes a WAL-only explanation, but this profile does
not separate row preparation, table-lock waits, publication, or work elsewhere
in the handler. Earlier direct measurements put table-lock admission and
release at only about 6–9 µs per eight-writer write, and publication-slot
waiting near 12 µs; those were different runs and should not be subtracted
from this table.

Both temporary probes were removed, the normal Release CLI rebuilt with zero
warnings, and the working tree was clean before this report was written. No
queue, lock, or WAL behavior was changed.
