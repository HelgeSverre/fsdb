# AGENTS.md

fsdb is a MySQL-compatible database server in idiomatic F# (.NET 10). It speaks
the MySQL wire protocol against an in-memory engine: bytes → `Command` → AST →
logical plan → lazy `seq` execution. Readable F# is the explicit primary goal;
raw performance is not — don't optimize by default. MySQL 8.4 is the semantic
oracle for correctness, never sqlite.

## Commands (run via `just`, see justfile)

- `just test` — full Expecto suite
- `just check` — build + test
- `just run [--port … --listen … --data-dir …]` — start server (default 127.0.0.1:3307)
- `just client` / `just smoke` — mysql shell / liveness probe
- `just coverage` — branch coverage (needs `dotnet tool install -g coverlet.console` once)
- `just bench` / `bench-quick` / `bench-durable` / `bench-scale` / `bench-load` — vs MySQL 8.4

Run one test (the `test` recipe passes no args through):

```sh
dotnet run --project tests/Fsdb.Tests -- --filter-test-case <Substring>
# or a full path: --filter "fsdb/<list>/<case>" ; or --run "<full test name>"
```

## F# compile order is manual (critical)

`src/Fsdb/Fsdb.fsproj` — and the test/torture/benchmark fsprojs — list
`Compile Include` entries in dependency order; F# has no top-down symbol
resolution. Add new `.fs` files *before* the files that consume them, or the
build fails with "not defined".

## Layout

- `src/Fsdb/` — library + executable in one project (`OutputType` Exe). Compile
  order is the module dependency order: `Log`/`Binary`/`Collation` → `Value`/
  `Ast` → `Parser` → `Functions` → `Storage`/`Persistence` →
  `InformationSchema` → `Executor` → `Packet`/`Protocol`/`Session`/
  `QueryHandler` → `Server` → `Db` (public embedding facade) → `Program`.
- `tests/Fsdb.Tests/` — Expecto unit + wire-level integration tests.
- `benchmarks/Fsdb.Benchmarks/` — BenchmarkDotNet, fsdb vs MySQL 8.4.
- `torture/` — **separate solution** (`torture/Fsdb.Torture.slnx`), deliberately
  NOT in the root solution or root CI/task gates. Differential harness against a
  MySQL 8.4 oracle. Exit codes: 0 pass/known gaps, 1 infra, 2 new fsdb findings,
  3 replay drift. Promote fixed bugs into the Expecto suite; never auto-enroll
  known gaps (`support/known-gaps.json` is hand-reviewed only).

## Conventions and gotchas

- **MySQL is the oracle, not sqlite.** Where a Laravel-app suite diverges from
  its sqlite baseline, fsdb must match real MySQL 8.4.
- No CI in this repo; verify locally with `just check` (and the torture harness
  for compatibility work).
- Comment style (`docs/comment-style.md`): why-not-what comments only;
  `ponytail:` debt markers; present tense, no "we". NO session narration,
  milestone names ("M9"/"M10"), roadmap or design-doc references in code,
  comments, or test names — git owns history; tests describe behavior.
- MySQL binaries are homebrew paths: client `/opt/homebrew/opt/mysql-client/bin/mysql`,
  server `/opt/homebrew/opt/mysql@8.4/bin/mysqld`. Benchmarks refuse to run if
  anything is listening on 3307, and spin up throwaway mysqld on 3316/3317 (ad
  hoc, no brew services).
- Persistence is opt-in via `--data-dir` (WAL + snapshot); the default is
  in-memory. Both halves are binary, no JSON: the WAL is `wal.bin`,
  `[len][crc32]` records over `CommitEvent` payloads with CRC torn-tail
  detection; the snapshot is `snapshot.fsdb`, a self-delimiting binary tree.
  Writes fsync via libc `fsync` (not `FileStream.Flush(true)`, which issues
  `F_FULLFSYNC` on macOS) — matching MySQL's default macOS durability
  semantics at ~16 us instead of ~5 ms per call.
- Known perf floor: a write statement snapshots `Table.RowsArray`
  (`ImmutableArray<Value[]>`; the plain-list `Table.Rows` member copies out of
  it) copy-on-write, so insert/update rebuild it O(table) per statement. This
  is a documented, deferred "Large" change — don't casually swap it out; the
  engine's snapshot/transaction model leans on that immutability.
- F# source files are ordered in the fsproj; keep `.slnx` (new XML solution
  format), not `.sln`.
