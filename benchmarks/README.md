# Benchmarks

fsdb vs a native MySQL 8.4 on identical schema, seed data, and queries,
via BenchmarkDotNet + MySqlConnector. The suite exists to find and track
hotspots, not to chase parity — fsdb optimizes for readable F# first.

## Running

```sh
just bench               # full latency suite, results -> results/<git-sha>.md
just bench-features      # recent SQL features only, results -> results/<git-sha>-features.md
just bench-quick         # ShortRun validation, no results file
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
(sha, date, OS, .NET, server mode). Milestone snapshots, medians:

| Workload | f1b15ab (pre-M9) | a90dfae (M9) | f4ba12a (M10) | MySQL 8.4 |
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

The current [10k/50k latency](results/ebc3fca.md) and
[100k/500k scale](results/ebc3fca-scale.md) runs separate indexed paths from
work that still scans or replans its input:

| Workload | fsdb at 10k | MySQL at 10k | fsdb at 100k | MySQL at 100k |
|---|---:|---:|---:|---:|
| Single INSERT | 80 µs | 107 µs | 91 µs | 108 µs |
| Single-row UPDATE | 133 µs | 107 µs | 130 µs | 122 µs |
| REPLACE by PK | 96 µs | 132 µs | 109 µs | 146 µs |
| Secondary range | 126 µs | 44 µs | 151 µs | 61 µs |
| Indexed join | 4.49 ms | 177 µs | 124 ms | 194 µs |
| Uncorrelated IN subquery | 103 ms | 149 µs | 1.09 s | 160 µs |
| GROUP BY aggregate | 87.5 ms | 20.2 ms | 1.17 s | 198 ms |
| Window query | 343 ms | 46.7 ms | 5.04 s | 715 ms |
| Natural FULLTEXT | 53.7 ms | 393 µs | 708 ms | 3.72 ms |

The point-write and secondary-range slopes are flat. The join, subquery,
aggregate, window, and FULLTEXT slopes identify planning and persistent-index
work as the highest-leverage performance area.

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

The current [worker-scaling run](results/7be1d91-load-scale.md) records both
completed throughput and retryable lock/deadlock errors:

| Workload | fsdb/MySQL at 1 worker | fsdb/MySQL at 16 workers |
|---|---:|---:|
| Point read | 0.28x | 0.11x |
| Distinct UPDATE | 0.97x | 0.23x |
| Distinct UPSERT | 0.93x | 0.38x |
| INSERT | 1.35x | 0.61x |
| Distinct REPLACE | 1.25x | 0.57x |
| Two-row transaction | 0.64x | 0.18x |
| Mixed read/write | 0.68x | 0.19x |

Single-worker writes are near parity or faster. The falling ratios at higher
worker counts expose publication/locking contention; 16-worker hot-row UPDATE
falls to 31 ops/sec despite negligible reported retries, indicating lock
convoying rather than optimistic abort churn. Multi-worker samples have high
variance, retained in the raw report, so use the table as scaling evidence
rather than a narrow regression threshold.

### Full-text search

The focused [10k-article comparison](results/f4865b1-fulltext.md) covers
natural, boolean, accent-aware, and boolean-prefix queries. fsdb measured
49–54 ms versus MySQL's 0.22–1.09 ms. Accent-aware collation matching adds no
visible penalty relative to fsdb's other modes; the gap is the full-corpus
tokenize/score pass versus MySQL's persistent inverted index.

Add a column here for each representative snapshot; keep intermediate runs in
`results/` without a column.
