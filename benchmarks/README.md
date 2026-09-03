# Benchmarks

fsdb vs a native MySQL 8.4 on identical schema, seed data, and queries,
via BenchmarkDotNet + MySqlConnector. The suite exists to find and track
hotspots, not to chase parity — fsdb optimizes for readable F# first.

## Running

```sh
just bench               # full latency suite, results -> results/<git-sha>.md
just bench-features      # recent SQL features only, results -> results/<git-sha>-features.md
just bench-quick         # ShortRun validation, results -> results/<git-sha>-quick.md
just bench-load          # 8-worker throughput, results -> results/<git-sha>-load.md
just bench-load-scale    # throughput at 1/2/4/8/16 workers
just bench-durable       # four-target durability-matched latency
just bench-scale         # scale-sensitive cases at 100k/500k rows
just bench-comprehensive # latency, durability, data scale, and worker scale
```

Prerequisites and rules:

- MySQL 8.4's `mysql`/`mysqld`/`mysqladmin` on PATH (no brew services — the
  recipe runs `mysqld` ad hoc on port 3316 with a throwaway datadir at
  `benchmarks/mysql-data`, recreated automatically if deleted).
  `bench-durable` also starts a second mysqld on port 3317 with
  `--skip-log-bin --innodb_flush_log_at_trx_commit=0 --sync_binlog=0`
  (datadir `benchmarks/mysql-data-nofsync`).
- Port 3307 must be free — the recipe refuses to run if anything answers
  there, because benchmarking against a shared server corrupts both runs.
- Don't run a bench while a heavy workload (test suites, agents) shares the
  machine; the numbers will be noise. Two consecutive runs should agree on
  `PointSelectByPk` within ~20% — if they don't, the run is not trustworthy.

## Methodology

- fsdb and the benchmark host both build and run Release (`DebugType=none`
  in the bench fsproj — the SDK's default portable PDBs otherwise make
  BenchmarkDotNet report DEBUG; the recipe uses `dotnet exec`, not
  `dotnet run`, which sets hot-reload env vars).
- Both databases are reset and reseeded per benchmark case. fsdb also
  restarts, so a pathological case cannot poison later measurements. This
  keeps mutation benchmarks independent of BenchmarkDotNet's case order.
- The default run measures fsdb in-memory (no `--data-dir`, so no WAL/fsync).
  `bench-durable` adds the matched configs: fsdb `--data-dir` (binary WAL,
  one plain `fsync` per commit — see `Persistence.attach`; .NET's
  `FileStream.Flush(true)` would instead issue macOS `F_FULLFSYNC` at ~5 ms)
  against durable MySQL, and in-memory fsdb against a no-fsync MySQL. A
  write-number only means something when both engines pay (or both skip) the
  same durability cost.
- `bench-load` measures throughput, not latency. Its disjoint and hot-row
  writes separate publication throughput from genuine contention, alongside
  reads, inserts, upserts, REPLACE, explicit transactions, and mixed traffic.
  `FSDB_LOAD_WORKERS` accepts a comma-separated worker-count matrix;
  `FSDB_LOAD_TRIALS` controls repetition. Engine order alternates between
  trials. The report includes relative standard deviation and the share of
  attempts retried after lock-timeout or deadlock errors; throughput counts
  completed operations only.
- Connection pooling is off (fsdb doesn't implement COM_RESET_CONNECTION).
- The wire+client floor is ~200 µs/op on this machine (`SELECT 1` round
  trip ≈ 0.26 ms via MySqlConnector over loopback) — sub-millisecond rows
  are bounded by the harness as much as the engine.
- Workloads: 10k users + 50k orders + 10k FULLTEXT articles, deterministic
  seed (override with `FSDB_BENCH_USERS`, `FSDB_BENCH_ORDERS`, and
  `FSDB_BENCH_ARTICLES`). The feature matrix covers views, triggers, CHECK
  constraints, generated columns, CTEs, windows, JSON_TABLE, natural,
  boolean, prefix, and accent-aware FULLTEXT,
  computed projections, transactions, and upserts. One operation runs per
  invocation; BenchmarkDotNet handles warmup, outliers, and statistics.
- BenchmarkDotNet's allocation column covers the benchmark client process,
  including MySqlConnector; it does not measure allocations in either server.

## Results history

Each run lands in `results/<git-sha>.md` with a provenance header
(sha, date, OS, .NET, server mode). Representative snapshots, medians:

| Workload | f1b15ab (initial) | a90dfae (indexed) | f4ba12a (streaming) | MySQL 8.4 |
|---|---:|---:|---:|---:|
| Point SELECT by PK | 1.32 ms | 103 µs | 111 µs | 38 µs |
| Prepared point SELECT | 22.9 ms* | 90 µs | 101 µs | 32 µs |
| Filter + sort + LIMIT scan | 21.6 ms | 15.8 ms | 6.3 ms | 1.9 ms |
| Single INSERT | 1.25 ms | 492 µs | 549 µs | 165 µs |
| Batch-100 INSERT | 142 ms | 8.6 ms | 10.1 ms | 1.2 ms |
| Single-row UPDATE | 2.41 s | 2.33 ms | 662 µs | 110 µs |
| Join users×orders | never finished | 201 ms | 10.7 ms | 268 µs |
| GROUP BY aggregate | 212 ms | 21.8 ms | 26.1 ms | 20.4 ms |
| JSON extract | 131 ms | 9.4 ms | 225 µs | 62 µs |

\* Numbers in the leftmost column after the join row were inflated by
benchmark poisoning (the timed-out join kept computing server-side); the
harness now isolates cases. See `results/f1b15ab.md`'s annotation.

Notable one-line context for the jump between the first two columns: an
O(n²) list-append in the UPDATE path, PK/unique hash indexes, a hash
equi-join, and `TcpClient.NoDelay` (Nagle's algorithm had been taxing every
round trip from the start).

### Current latency and scale

The current [10k/50k latency](results/98bc883-quick.md) and
[100k/500k scale](results/98bc883-scale.md) runs separate indexed paths from
work that still scans or replans its input:

| Workload | fsdb at 10k | MySQL at 10k | fsdb at 100k | MySQL at 100k |
|---|---:|---:|---:|---:|
| Single INSERT | 299 µs | 124 µs | 404 µs | 115 µs |
| Single-row UPDATE | 292 µs | 130 µs | 369 µs | 142 µs |
| REPLACE by PK | 241 µs | 152 µs | 322 µs | 156 µs |
| Secondary range | 143 µs | 44 µs | 195 µs | 46 µs |
| Indexed join | 338 µs | 184 µs | 302 µs | 194 µs |
| Uncorrelated IN subquery | 499 µs | 162 µs | 519 µs | 163 µs |
| GROUP BY aggregate | 27.2 ms | 20.1 ms | 313 ms | 200 ms |
| Window query | 85.2 ms | 98.9 ms | 1.21 s | 1.20 s |
| Natural FULLTEXT | 1.06 ms | 429 µs | 10.3 ms | 3.89 ms |

Point writes, indexed joins, uncorrelated membership, JSON extraction, and
secondary ranges retain shallow slopes. Scans, grouping, non-indexed updates,
decimal membership, and window execution grow with their input. The scale
window samples have high variance, but both engines finish in the same order
of magnitude; the old multi-second fsdb-only cliff is gone. FULLTEXT uses
maintained postings and scales approximately linearly, while the joined query
remains 6.3x slower than MySQL at 100k articles.

The quick matrix also exposes constant-factor work hidden by slope alone:
point reads are 237 µs versus 40 µs, recursive CTE evaluation is 953 µs versus
54 µs, and correlated indexed counts are 1.22 ms versus 207 µs. A sampled CPU
trace of a saturated recursive-CTE workload attributes most active managed
time to per-statement query handling, binding, dynamic scope, and `AsyncLocal`
state transitions rather than the 100-row recursive body itself. That makes
shared statement setup the next profiling seam, not a special-purpose CTE
container.

The [low-cardinality join profile](results/1c2270d-low-cardinality-joins.md)
compares an indexed join with an otherwise identical unindexed hash-join
twin. fsdb now uses observed distinct-key counts to avoid repeated broad index
bucket resolution when the full join result is consumed, while preserving the
index path for early-stopping queries.

### Durability-matched (single-connection latency, `ebc3fca-durable.md`)

fsdb in-memory vs fsdb `--data-dir` (binary WAL) vs MySQL durable vs MySQL no-fsync:

| Workload | fsdb | fsdb-wal | mysql | mysql-nofsync |
|---|---:|---:|---:|---:|
| Single INSERT | 85 µs | 142 µs | 114 µs | 38 µs |
| Batch-100 INSERT | 2.07 ms | 2.23 ms | 1.22 ms | 967 µs |
| REPLACE by PK | 100 µs | 1.13 ms | 122 µs | 52 µs |
| Single-row UPDATE | 128 µs | 1.49 ms | 116 µs | 44 µs |
| Two-row transaction | 339 µs | 3.01 ms | 216 µs | 136 µs |

The WAL adds little to batched INSERT, but UPDATE, REPLACE, UPSERT, and
explicit transactions remain roughly 9–14× slower than durable MySQL. This
is a persistence/publication-path gap rather than the in-memory row-mutation
cost.

### Concurrency throughput

The broad eight-worker baseline in `4897506-load.md` predates optimistic
row-conflict merging:

| Workload | fsdb | mysql |
|---|---:|---:|
| update-distinct | 21,777 | 20,399 |
| insert | 5,673 | 22,703 |
| mixed read/write | 20,537 | 47,801 |

The latest [eight-worker run](results/98bc883-load.md) records completed
throughput with no retryable lock/deadlock errors:

| Workload | fsdb ops/s | MySQL ops/s | fsdb/MySQL |
|---|---:|---:|---:|
| Point read | 12,677 | 105,522 | 0.12x |
| Distinct UPDATE | 4,721 | 11,011 | 0.43x |
| Hot UPDATE | 6,026 | 15,438 | 0.39x |
| Distinct UPSERT | 4,225 | 8,085 | 0.52x |
| INSERT | 5,569 | 15,319 | 0.36x |
| Distinct REPLACE | 5,662 | 14,255 | 0.40x |
| Two-row transaction | 2,038 | 9,225 | 0.22x |
| Mixed read/write | 6,974 | 25,746 | 0.27x |

The default load run is one five-second trial and therefore directional. A
separate 64-worker prepared-transaction campaign completed 12,800 operations
with exact MySQL outcome parity and no failures: fsdb reached 781 tx/s at p99
107 ms, versus MySQL's 266 tx/s at p99 679 ms. That contrast separates the
engine's efficient hot-account transaction path from the wire and generic
statement overhead visible in the broad load matrix.

### Full-text search

The initial post-index [10k-article](results/8e904fd-fulltext-index.md) and
[100k-article](results/8e904fd-fulltext-index-scale.md) comparisons, followed
by the [posting-candidate comparison](results/ef4b4ab-fulltext-postings.md), cover
natural, boolean, accent-aware, and boolean-prefix queries. Against the
pre-index 10k baseline, natural search fell from 53.7 ms to 2.76 ms,
accent-aware search from 51.2 ms to 1.62 ms, boolean search from 50.7 ms to
3.19 ms, and prefix search from 49.5 ms to 1.44 ms. At 100k, posting-driven
boolean evaluation is 56.0 ms versus MySQL's 10.7 ms, while maintained prefix
postings are 33.9 ms versus 2.62 ms. OR predicates, projection-only MATCH,
and the general result pipeline are the remaining scale seams, not document
re-tokenization or vocabulary scans.

Add a column here for each representative snapshot; keep intermediate runs in
`results/` without a column.
