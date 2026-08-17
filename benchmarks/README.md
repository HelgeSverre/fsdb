# Benchmarks

fsdb vs a native MySQL 8.4 on identical schema, seed data, and queries,
via BenchmarkDotNet + MySqlConnector. The suite exists to find and track
hotspots, not to chase parity — fsdb optimizes for readable F# first.

## Running

```sh
just bench          # full latency suite (~7 min), results -> results/<git-sha>.md
just bench-quick    # ShortRun job for fast local iteration, no results file
just bench-load     # N-writer throughput (ops/sec), results -> results/<git-sha>-load.md
just bench-durable  # 4-target durability-matched latency, results -> results/<git-sha>-durable.md
just bench-scale    # latency suite at 100k/500k rows, results -> results/<git-sha>-scale.md
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
- fsdb restarts and reseeds per benchmark case so a pathological case can't
  poison later measurements (see the module comment in
  `Fsdb.Benchmarks/ServerBenchmarks.fs`).
- The default run measures fsdb in-memory (no `--data-dir`, so no WAL/fsync).
  `bench-durable` adds the matched configs: fsdb `--data-dir` (binary WAL,
  one plain `fsync` per commit — see `Persistence.attach`; .NET's
  `FileStream.Flush(true)` would instead issue macOS `F_FULLFSYNC` at ~5 ms)
  against durable MySQL, and in-memory fsdb against a no-fsync MySQL. A
  write-number only means something when both engines pay (or both skip) the
  same durability cost.
- `bench-load` measures throughput, not latency: N workers over disjoint id
  slices so MySQL sees no row contention, reporting ops/sec per workload.
  fsdb's whole write path sits behind a per-database `SemaphoreSlim(1,1)`
  (see `Storage.enterTransactionGate`), which a single-connection latency
  suite structurally cannot expose.
- Connection pooling is off (fsdb doesn't implement COM_RESET_CONNECTION).
- The wire+client floor is ~200 µs/op on this machine (`SELECT 1` round
  trip ≈ 0.26 ms via MySqlConnector over loopback) — sub-millisecond rows
  are bounded by the harness as much as the engine.
- Workloads: 10k users + 50k orders, deterministic seed (override with
  `FSDB_BENCH_USERS`/`FSDB_BENCH_ORDERS`). One operation per invocation;
  BenchmarkDotNet handles warmup, outliers, and statistics.

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

\* pre-M9 numbers after the join row were inflated by benchmark poisoning
(the timed-out join kept computing server-side); the harness now isolates
cases. See `results/f1b15ab.md`'s annotation.

Notable one-line context for the M9 jump: an O(n²) list-append in the
UPDATE path, PK/unique hash indexes, a hash equi-join, and `TcpClient.NoDelay`
(Nagle's algorithm had been taxing every round trip since M1).

### Durability-matched (single-connection latency, `cbbdfb4-durable.md`)

fsdb in-memory vs fsdb `--data-dir` (WAL) vs MySQL durable vs MySQL no-fsync:

| Workload | fsdb | fsdb-wal | mysql | mysql-nofsync |
|---|---:|---:|---:|---:|
| Point SELECT by PK | 116 µs | 131 µs | 40 µs | 40 µs |
| Single INSERT | 274 µs | 7.75 ms | 113 µs | 33 µs |
| Batch-100 INSERT | 5.07 ms | 11.96 ms | 1.08 ms | 787 µs |
| Single-row UPDATE | 90 µs* | 5.64 ms | 103 µs | 40 µs |

\* the in-memory UPDATE row was noise-contaminated in this run (373 µs, 215%
CI); 81–91 µs is the established value from the clean `f5ff5a4` run.

fsdb's durable write is fsync-bound: one `fsync` per commit on a growing
JSONL WAL (~5–7 ms here), while durable MySQL group-commits its redo log
(~100 µs). The "fsdb beats MySQL on writes" reading is an artifact of
comparing a non-durable engine against a durable one — matched on durability,
fsdb's point write is ~60x slower.

### Concurrency throughput (`4897506-load.md`, 8 workers, ops/sec)

| Workload | fsdb | mysql |
|---|---:|---:|
| update-distinct | 21,777 | 20,399 |
| insert | 5,673 | 22,703 |
| mixed read/write | 20,537 | 47,801 |

fsdb's write throughput grows sublinearly with workers (its per-database
write gate serializes writers: ~20k → ~22k → ~26k ops/sec at 4/8/16
workers) while MySQL scales on insert and mixed read/write. The cheapest
write (point UPDATE) is the one place fsdb's serialized-but-in-memory path
keeps pace with fsync-bound MySQL; anything heavier, or mixed with readers,
and MySQL pulls ahead.

Add a column here per milestone snapshot; keep intermediate runs in
`results/` without a column.
