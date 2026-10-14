# Statement admission under durable concurrent inserts

<!--
sha: b081bd1b plus the QueryHandler change under test
date: 2026-10-14T18:52:47Z
os: Darwin 24.6.0 arm64
dotnet: 10.0.401
mysql: 8.4.11, native Homebrew server
targets: WAL-backed fsdb; durable MySQL, binary log disabled
dataset: 1000 users, 1000 orders, 1000 articles
-->

The `insert-keyless` load uses a distinct literal SQL statement for each
insert. At eight concurrent writers, the old parsed-statement admission cache
continually added fingerprints and trimmed its bounded dictionary. A
six-second `dotnet-sampled-thread-time` trace of the WAL-backed server placed
`Monitor.Enter_Slowpath` at 17% of sampled thread time. Of its sampled stacks,
9,102 had `BoundedConcurrentCache.trim` as the nearest fsdb caller, mostly
through `ConcurrentDictionary.Count`; 2,290 were under statement execution's
dictionary insertion. These are thread-time samples, not measured lock hold
durations or a CPU breakdown.

The change replaces only the admission fingerprints with fixed slots and
atomic exchange. A collision can make a statement eligible for caching sooner
or evict its admission hint; the parsed-statement cache still resolves by full
SQL text and parser options. No query result or parser contract changes.

The following runs used the same host, data sizes, WAL mode, warmup, and
measurement duration. Each variant was built in Release, and each trial
started a fresh fsdb data directory and seeded MySQL. The source alternated
from original to updated, back to original, then updated again.

```sh
FSDB_LOAD_FSDB_TARGET=fsdb-wal FSDB_LOAD_WORKLOADS=insert-keyless \
  FSDB_LOAD_WORKERS=8 FSDB_LOAD_TRIALS=2 FSDB_LOAD_WARMUP=2 \
  FSDB_LOAD_SECONDS=15 FSDB_BENCH_USERS=1000 FSDB_BENCH_ORDERS=1000 \
  FSDB_BENCH_ARTICLES=1000 just _bench-run --load
```

| Source | fsdb + WAL ops/sec | fsdb RSD | MySQL ops/sec | MySQL RSD |
|---|---:|---:|---:|---:|
| Updated, first two-trial run | 11,896 | 1.4% | 45,119 | 0.1% |
| Original, two-trial run | 8,549 | 1.4% | 45,526 | 0.9% |
| Updated, final two-trial run | 11,719 | 0.5% | 44,796 | 2.6% |

The updated runs were 37–39% faster than the intervening original run for
this workload. Single-trial checks on either side showed the same direction
(7,844 original and 12,046 updated ops/sec). No engine reported retryable
contention. This removes a process-wide statement-admission hot spot but does
not eliminate the durable write scaling gap: MySQL remained several times
faster, and publication and WAL costs still need separate measurement.

The earlier [durability-matched worker campaign](2026-10-14-durable-keyless-scale.md)
used three three-second trials at one, four, and eight workers. Repeating that
configuration with the updated admission path gave:

| Workers | Original fsdb + WAL ops/sec | Updated fsdb + WAL ops/sec | Updated RSD | Updated MySQL ops/sec |
|---:|---:|---:|---:|---:|
| 1 | 5,346 | 6,087 | 4.5% | 15,840 |
| 4 | 9,101 | 10,074 | 1.9% | 31,543 |
| 8 | 9,006 | 11,223 | 2.1% | 45,963 |

Eight-worker fsdb throughput improved about 25% in this shorter campaign,
and one-to-eight-worker scaling rose from 1.68× to 1.84×. The host ran other
work between campaigns, so the alternating 15-second runs above are stronger
evidence for the eight-worker before/after effect. The remaining slope still
trails MySQL's.
