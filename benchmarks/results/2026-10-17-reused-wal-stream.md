# Reusing the WAL append stream across group-commit batches

The [write-stage profile in PR #143](https://github.com/HelgeSverre/fsdb/pull/143)
found that durable acknowledgement dominated the eight-writer keyless-insert
path. Its WAL follow-up measured only 2.1 events per fsync and 21.8 µs per
event opening, encoding, and writing WAL records. This experiment removes
one file open and close per group-commit batch. The append stream remains
open between batches and is closed before a checkpoint truncates the WAL;
the next batch opens the rotated file. Event order, fsync timing, and
acknowledgement boundaries are unchanged.

The native macOS arm64 host ran WAL-backed fsdb and durable MySQL 8.4.11
against fresh databases with 1,000 seeded users, orders, and articles.
Each result is the mean of two 10-second measured trials after three seconds
of warmup. The candidate ran before the clean rebuilt original at each
worker count. MySQL ran in the same harness as a host-load control. No
retries were observed.

| Writers | Implementation | fsdb inserts/s | fsdb RSD | MySQL inserts/s | MySQL RSD | fsdb/MySQL |
|---:|---|---:|---:|---:|---:|---:|
| 1 | Original | 7,057 | 0.9% | 16,576 | 1.5% | 0.43× |
| 1 | Reused stream | 8,544 | 2.8% | 16,174 | 0.9% | 0.53× |
| 8 | Original | 12,056 | 0.4% | 47,281 | 0.0% | 0.25× |
| 8 | Reused stream | 12,956 | 1.3% | 47,715 | 0.1% | 0.27× |

The candidate gained about 21% at one writer and 7.5% at eight writers
against the paired original runs. Two trials on one host do not establish a
universal gain; the change also leaves most of the eight-writer gap to MySQL.

Two additional eight-writer workloads used fresh databases, two seven-second
trials after two seconds of warmup, and the same candidate-then-original
order. These exercise indexed inserts and distinct-row updates rather than
only keyless inserts. Neither reported retries.

| Workload | Original fsdb inserts or updates/s | Reused stream inserts or updates/s | Original MySQL ops/s | Candidate-run MySQL ops/s |
|---|---:|---:|---:|---:|
| Indexed insert | 5,251 | 5,532 | 42,467 | 42,405 |
| Distinct update | 6,736 | 6,899 | 28,172 | 27,665 |

The candidate was about 5.4% faster on indexed inserts and 2.4% faster on
distinct updates in these short trials. The smaller update difference is
directional; it does not establish a stable effect. Neither workload showed
a throughput regression.

The persistence suite passed 127 tests before the focused post-checkpoint
reopen test was added. That test confirms a write after checkpoint rotation
lands in the new WAL and recovery combines it with the snapshot. The full
`just check` gate then passed 3,707 tests with zero build warnings.
