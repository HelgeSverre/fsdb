# Point-read throughput under concurrent load

The focused rerun uses Release Fsdb `899d38db`, native MySQL 8.4.11, and
the same Apple M2 Max, macOS 15.6, .NET 10.0.12 and deterministic base
fixture as the [broader comparison](2026-11-18-native-mysql-comparison.md).
The [load harness](../Fsdb.Benchmarks/LoadBenchmarks.fs) runs
`SELECT id, name, age FROM users WHERE id = …` with random hits among
10,000 users. At every worker count the targets alternate over three
trials with 1 s warmup and 3 s measured.

| Workers | Fsdb WAL ops/s (RSD) | Fsdb memory ops/s (RSD) | MySQL in WAL comparison | MySQL in memory comparison |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4,426 (1.1%) | 4,603 (0.7%) | 27,000 | 26,885 |
| 8 | 5,915 (2.9%) | 6,432 (2.1%) | 112,784 | 112,951 |
| 16 | 5,775 (2.9%) | 6,329 (1.1%) | 118,519 | 119,116 |

MySQL ran with normal InnoDB commit flushing and binary logging off in
both passes. There are no timed writes. At 16 workers in-memory Fsdb was
9.6% faster than Fsdb with WAL attached. Both Fsdb configurations peaked
at eight workers, and the 16-worker gap was still 18.8× for memory and
20.5× for WAL. The measurements rule out WAL attachment as the dominant
explanation on this workload.

## Parsing and prepared execution

A separate WAL-only wire probe used the same SQL and fixture with one
connection per worker. It verified the returned ID on every lookup.
Prepared mode used one retained statement per connection. The figures
are medians of three 3 s windows after 1 s warmup, with mode order
reversed for the middle trial.

| Workers | Text ops/s | Retained prepared ops/s | Gain |
| ---: | ---: | ---: | ---: |
| 1 | 4,541 | 5,989 | 31.9% |
| 16 | 5,376 | 5,598 | 4.1% |

The existing [in-process pipeline probe](../PointLookupPipeline/Program.fs)
uses a narrower fixed-ID query and a simpler schema. Across five
10,000-call trials its medians were 24.4 µs for parsing alone,
11.1 µs for execution of a pre-parsed statement, 35.2 µs for the text
handler and 32.7 µs for the prepared handler. The text handler allocated
about 87 KB per call on the calling thread. These separate pipeline
measurements include no TCP transport and cannot be subtracted from the
wire figures to isolate individual stages.

## Loaded server trace

A separate 12 s EventPipe managed thread-time trace sampled the server
while 16 clients performed the same WAL-backed text reads. Most of the
aggregate sampled thread time was idle worker time (77.7%). Contention
samples reached the shared statement-lock manager, range planning, and
transaction lock release. The largest individual monitor-wait stack
ran through `TableLocks.withStatementAccessUnder`. The trace also
confirmed that ordinary autocommit reads create and close an implicit
tracked transaction. The profile samples are overlapping wall times
across threads and do not measure CPU share or end-to-end latency share.

## Transaction and statement-admission controls

A throwaway build altered only the benchmark database's single-table
point-read path during measurement. One variant bypassed the private
autocommit transaction while retaining statement admission. The other
bypassed statement admission while retaining the transaction. The controls
used the same build and ordinary behavior. Each group is a fresh server
with 16 workers, three 2.5 s measurements, and 0.75 s warmup per measurement.
The benchmark was read-only once seeding finished.

| Variant | Trial ops/s (rounded) | Median ops/s |
| --- | --- | ---: |
| Normal, before | 5,091 / 5,930 / 6,095 | 5,930 |
| Direct read with statement admission | 6,694 / 7,351 / 7,436 | 7,351 |
| Private transaction without statement admission | 5,195 / 6,024 / 6,171 | 6,024 |
| Normal, after | 5,692 / 6,486 / 6,617 | 6,486 |

The direct path gained 13–24% against the two control medians. Bypassing
admission alone landed inside the control spread. The transaction lifecycle
therefore contributes to this gap, while the table-admission trace alone
would overstate the benefit of changing the gate. Neither diagnostic
bypass was shipped. A production direct-read path would need to preserve
statement snapshots, locking, and queries that can run stored functions.

## Runtime GC control

The regular CLI leaves GC mode at its runtime default. This control used
three fresh server processes from the same Release binary in sequence.
The middle run used a copied CLI runtime configuration with
`System.GC.Server` set to true. The benchmark client and the WAL storage
settings were identical. Each run used 16 clients and the same three
2.5 s windows described above.

| CLI configuration | Trial ops/s (rounded) | Median ops/s |
| --- | --- | ---: |
| Default, before | 5,462 / 6,313 / 6,400 | 6,313 |
| Server GC | 25,026 / 28,881 / 28,922 | 28,881 |
| Default, after | 5,606 / 6,340 / 6,475 | 6,340 |

GC mode was the largest tested lever at 16 clients. The Server GC median
was about 4.6× the two default control medians. The configuration was
used for diagnosis and was left out of the shipped CLI.

The original load harness also reproduced the effect with WAL attached
and only the CLI runtime configuration changed.

| Workers | Fsdb WAL default ops/s | Fsdb WAL Server GC ops/s (RSD) | MySQL ops/s in Server GC pass | Fsdb gain |
| ---: | ---: | ---: | ---: | ---: |
| 1 | 4,426 | 6,453 (1.7%) | 27,235 | 1.46× |
| 8 | 5,915 | 23,026 (0.8%) | 113,012 | 3.89× |
| 16 | 5,775 | 25,657 (7.0%) | 116,276 | 4.44× |

At 16 workers, the baseline gap of 20.5× dropped to 4.5× in this
configuration. This measures short, warm point-read windows on one host.
Sustained memory use, tail latency, and effects on other workloads remain
unmeasured.
