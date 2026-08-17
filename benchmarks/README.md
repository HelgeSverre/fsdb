# Benchmarks

fsdb vs a native MySQL 8.4 on identical schema, seed data, and queries,
via BenchmarkDotNet + MySqlConnector. The suite exists to find and track
hotspots, not to chase parity — fsdb optimizes for readable F# first.

## Running

```sh
just bench        # full suite (~10 min), results -> results/<git-sha>.md
just bench-quick  # ShortRun job for fast local iteration, no results file
```

Prerequisites and rules:

- MySQL 8.4 keg-only at `/opt/homebrew/opt/mysql@8.4` (no brew services —
  the recipe runs `mysqld` ad hoc on port 3316 with a throwaway datadir at
  `benchmarks/mysql-data`, recreated automatically if deleted).
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
- fsdb runs in-memory (no `--data-dir`), so no WAL/fsync in any number.
- Connection pooling is off (fsdb doesn't implement COM_RESET_CONNECTION).
- The wire+client floor is ~200 µs/op on this machine (`SELECT 1` round
  trip ≈ 0.26 ms via MySqlConnector over loopback) — sub-millisecond rows
  are bounded by the harness as much as the engine.
- Workloads: 10k users + 50k orders, deterministic seed. One operation per
  invocation; BenchmarkDotNet handles warmup, outliers, and statistics.

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

Add a column here per milestone snapshot; keep intermediate runs in
`results/` without a column.
