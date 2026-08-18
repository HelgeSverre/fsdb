# AGENTS.md

fsdb is a MySQL-compatible database server in idiomatic F# (.NET 10): the
MySQL wire protocol against an in-memory engine (bytes → `Command` → AST →
logical plan → lazy `seq`). Readable F# is the explicit primary goal; raw
performance is not — don't optimize by default. MySQL 8.4 is the semantic
oracle for correctness, never sqlite: where a Laravel-app suite diverges from
its sqlite baseline, fsdb must match real MySQL 8.4.

## Commands (run via `just`, see justfile)

- `just test` — full Expecto suite
- `just check` — build + test
- `just run [--port … --listen … --data-dir …]` — start server (default 127.0.0.1:3307)
- `just client [port=…]` / `just smoke [port=…]` — mysql shell / liveness probe
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
  `Ast` → `Parser` → `Functions` → `Storage` → `Auth`/`Persistence` →
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

- No CI in this repo; verify locally with `just check` (and the torture harness
  for compatibility work).
- Connections authenticate against `mysql.user`: the bootstrap account is
  `root` with no password (`-uroot`, empty password only); an unknown user is
  a 1045. Text-probed statements (SET/SHOW/KILL/USE) bypass privilege
  checks — a documented divergence, see docs/compatibility.md.
- Comment & doc style: `docs/comment-style.md` is the authority — every
  comment there must survive a KEEP/DELETE/REWRITE grading (why-not-what,
  `ponytail:` debt markers, no session narration, milestone names, roadmap,
  or design-doc references in code, comments, or test names). The same rules
  apply to markdown prose, and docs use words or `[x]` checkboxes for status,
  never emoji markers.
- The justfile resolves `mysql`/`mysqld`/`mysqladmin` from PATH (MySQL 8.4 —
  homebrew's keg-only `/opt/homebrew/opt/mysql@8.4/bin` is the usual source).
  Benchmarks refuse to run if anything is listening on 3307, and spin up
  throwaway mysqld on 3316/3317 (ad hoc, no brew services).
- Persistence is opt-in via `--data-dir` (WAL + snapshot); default in-memory.
  Both halves are binary, no JSON: WAL `wal.bin` = `[len][crc32]` records over
  `CommitEvent` payloads (CRC torn-tail detection); snapshot `snapshot.fsdb` =
  self-delimiting binary tree. fsync via libc — `FileStream.Flush(true)` issues
  `F_FULLFSYNC` on macOS (~5 ms per call) and diverges from MySQL's own macOS
  durability semantics.
- Known perf floor: writes copy `Table.RowsArray` (`ImmutableArray<Value[]>`;
  `Table.Rows` is a plain-list copy of it) O(table) per statement — a
  documented, deferred "Large" change; the snapshot/transaction model leans on
  that immutability.
- Keep `.slnx` (new XML solution format), not `.sln`.
