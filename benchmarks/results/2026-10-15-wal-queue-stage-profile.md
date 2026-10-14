# WAL queue and batch stage profile

<!--
sha: c52e036d
date: 2026-10-15
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
dataset: 1000 users, 1000 orders, 1000 articles
-->

The [wire write scheduling control](2026-10-14-wire-write-scheduling-profile.md)
found much lower eight-writer throughput through the MySQL protocol than
through the embedded API, but could not separate WAL queue time from file work.
This follow-up timed queue admission, the gap before invoking the returned
completion action, completion waiting, group-commit batch work, and phases of
the WAL batch. Counters were temporary and have been removed. All runs used a
fresh WAL-backed FSDB database, one second of warmup, five measured seconds,
one trial per path, and the same keyless-insert workload. The wire runs also
measured durable MySQL as a host-load control.

```sh
FSDB_EMBEDDED_TARGET=wal FSDB_EMBEDDED_WORKERS=8 FSDB_EMBEDDED_TRIALS=1 \
  FSDB_EMBEDDED_WARMUP=1 FSDB_EMBEDDED_SECONDS=5 \
  FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 FSDB_BENCH_ARTICLES=1000 \
  dotnet fsi --nologo benchmarks/scripts/embedded-keyless-load.fsx

FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=1 FSDB_LOAD_WARMUP=1 \
  FSDB_LOAD_SECONDS=5 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

The queue counters were cumulative; the table uses their first report after
about 60,000 completed writes. `completion` sums wall time across the eight
workers, so it is not elapsed process time. `batch work` includes WAL append,
fsync, replica application, and occasional rotation; it excludes completion
signaling. Batch sizes are counts from the same snapshots.

| Path | Inserts/sec | MySQL inserts/sec | Batches / items near 60k | Admission ms | Handoff ms | Completion ms | Batch-work ms |
|---|---:|---:|---:|---:|---:|---:|---:|
| Embedded, original | 34,348 | — | 34,426 / 60,003 | 14 | 98 | 8,412 | 2,314 |
| Wire, original A | 11,856 | 46,290 | 36,060 / 60,003 | 17 | 151 | 14,785 | 4,419 |
| Wire, retained handle A | 12,712 | 46,743 | 42,574 / 60,004 | 23 | 164 | 11,525 | 3,902 |
| Wire, retained handle B | 10,336 | 35,577 | 44,330 / 60,002 | 21 | 162 | 13,507 | 4,016 |
| Wire, original B | 12,048 | 45,245 | 34,684 / 60,000 | 23 | 133 | 15,044 | 4,278 |
| Embedded, retained handle | 38,805 | — | 47,265 / 60,001 | 16 | 117 | 5,956 | 2,052 |

The retained-handle prototype kept one `FileStream` between batches and closed
it before checkpoint/rotation. It was tested between the two original wire
runs; the embedded runs were not interleaved. At 30,000 batches, measured file
open time was 961–1,021 ms in the original wire runs, or 32–34 µs per batch,
and 2–5 ms with the retained handle. Durable flush consumed 1,747–1,795 ms
in the original runs and 1,930–1,947 ms with the retained handle. These totals
cover equal batch counts, not equal write counts: the retained-handle run had
smaller batches. Flush time per batch remained roughly 58–65 µs. Encoding and
write, mirror application, and rotation were measured separately; none
identified a larger isolated cost than open plus flush under this workload.

Queue admission and the handoff gap were small compared with completion wall
time. Removing per-batch opens lowered completion time at comparable write
counts, but the two retained-handle wire trials did not establish a stable
absolute throughput gain; the second trial also had a much slower MySQL host
control. A long-lived file handle would change shutdown, checkpoint, and
temporary-directory lifetime behavior, especially on Windows. The prototype
was reverted along with all timing counters. No production scheduling or WAL
policy changed.

The next useful experiment should compare active server work and scheduler
waiting around the SQL execution path with a method-resolved trace, under a
repeatable host-load control. It should not assume the batch-size difference
is the cause: these runs showed similar original batch counts at 60,000
writes, and the retained handle reduced batch size while removing open cost.
