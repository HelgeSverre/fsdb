<!--
sha: 0eb67869
date: 2026-10-12T03:21:19Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: mysql  Ver 8.4.11 for macos15.7 on arm64 (Homebrew)
targets: in-memory fsdb; durable MySQL
dataset: 1000 users, 1000 orders, 1000 articles
-->

Workers: 1, 8. 3 trial(s), 3.0s measured after 1.0s warmup.

| Workers | Workload | fsdb ops/sec | fsdb RSD | fsdb retry | MySQL ops/sec | MySQL RSD | MySQL retry | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|---:|---:|
| 1 | insert-keyless | 7324 | 4.3% | 0.0% | 10032 | 2.9% | 0.0% | 0.73x |
| 8 | insert-keyless | 9865 | 1.6% | 0.0% | 25451 | 0.9% | 0.0% | 0.39x |

The measured statement was `INSERT INTO keyless_events (worker_id, event_id)
VALUES (?, ?)`, with literal integers substituted by each worker's connection.
The table has two non-null integer columns and no keys. Each trial used a fresh
seeded database, warmed the workload, and checked that every completed insert
from both phases remained in the table. No trial lost rows or reported a
retryable lock/deadlock error. The benchmark implementation and retention check
were uncommitted additions over the `0eb67869` base revision when measured.

Eight workers improved fsdb's completed rate by **1.35x** over one worker;
MySQL improved by **2.54x**. The fsdb and MySQL write rates are not a
durability-matched comparison: fsdb was in-memory and MySQL used its normal
durable mode. The weaker fsdb scaling is a useful signal for profiling the
per-database publication section and keyless optimistic-merge work; this run
does not isolate which part dominates. The three short trials support a
directional result, not a production throughput guarantee.

## Follow-up on the publication path

A ten-second `dotnet-sampled-thread-time` trace of an eight-writer run sampled
substantial monitor waiting: `Monitor.Enter_Slowpath` accounted for about 20%
of sampled thread time. The samples include other runtime waits and do not
identify a single lock or measure its hold duration, but they support looking
at synchronization before micro-optimizing row coercion.

I tried preparing a single keyless candidate before acquiring the database
publication lock, using the existing prepared-candidate helper. The same
one/eight-writer, three-trial configuration measured **7,623 / 9,836 ops/sec**
after that change, against **7,324 / 9,865 ops/sec** in the recorded baseline.
The eight-writer result did not improve; the single-writer change is within
the short-run variation and unrelated to lock parallelism. I removed the
candidate fast path rather than retain complexity without a demonstrated gain.
A 16-writer regression now checks that keyless autocommit inserts remain visible
exactly once.

## Indexless publication shortcut

Temporary timing counters around `withDatabasePublishing` separated slot wait,
slot hold, row operation, and root swap for 10-second eight-writer runs. One
run averaged **104 µs waiting** and **63 µs holding** the slot over 90,000
recorded writes; a one-writer run averaged effectively no wait and **32 µs
holding** over 70,000 writes. A second eight-writer run split the **59 µs
hold** into about **57 µs in the insert operation**, **0.2 µs in the catalog
root swap**, and roughly **2 µs elsewhere**. These are instrumented runs, so
their absolute rates are not the uninstrumented benchmark rates. The
diagnostic counters were removed after measurement.

For a table with unchanged columns and indexes and no full-text or spatial
indexes before or after the write, `publishRowsWithDocumentRules` no longer
enumerates row changes solely to update absent document indexes. On a direct
Release `Storage.insertRows` fixture with 1,000 warmup inserts followed by
20,000 measured single-row inserts, managed allocation fell from **24,789.5
to 16,057.5 bytes per insert**. The first after version repeated at **16,088.4
bytes**; the final version, which explicitly carries forward the preexisting
empty document-index maps, measured **16,325.9 bytes**. That fixture bypasses
SQL parsing and wire handling; its elapsed time was 465 ms before and
271–296 ms across the after runs, which is a local engine result, not server
throughput.

The uninstrumented wire load run with the same one/eight-writer, three-trial
settings measured **8,107 / 10,208 ops/sec** with the shortcut. The earlier
baseline was **7,324 / 9,865**; a later baseline rerun measured **6,717 /
9,950** with 9.2% relative standard deviation at one writer. The eight-writer
difference is small relative to trial variation, so this does not establish
a wire-level throughput gain. All runs checked row retention and reported no
retryable lock/deadlock errors. The indexless shortcut is retained for its
clear allocation reduction; the database-slot serialization gap remains.
