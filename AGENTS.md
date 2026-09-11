# AGENTS.md

fsdb stands for **F# Database** ("F Sharp Database"). It is a MySQL-compatible
database server written in idiomatic F# on .NET 10. The MySQL wire protocol
feeds `Command`, the SQL AST, a logical plan, and a lazy `seq`-based executor
over an in-memory or WAL-backed engine.

## Priorities

Work in this order:

1. Match observable MySQL 8.4 behavior.
2. Keep the implementation readable, idiomatic, and pleasant F#.
3. Preserve correctness with focused tests and differential evidence.
4. Optimize measured bottlenecks without obscuring the design.

Raw speed is not the default goal. Profile first, retain a readable baseline,
and prefer an obvious data structure or algorithm over local cleverness.

## Working method

Use the mantra: **make the change easy, then make the easy change**.

- Start compatibility work with a failing test whose expected result comes
  from MySQL 8.4.
- When the existing shape makes a change awkward, first make the smallest
  behavior-preserving refactor that exposes the right concept. Verify it, then
  implement the behavior. Keep the refactor and behavior in separate commits
  when that makes review or rollback clearer.
- Prefer domain types, discriminated unions, options, results, pattern matches,
  and small named functions over flags, sentinel values, magic indexes, or
  deeply nested conditionals.
- Use pipelines when they clarify the data flow. Do not force point-free style
  or abstraction where direct code reads better.
- Centralize repeated AST, catalog, row, and protocol traversals when they
  represent the same rule. Similar-looking code with different semantics may
  remain separate.
- Do not add modules, folders, or generic helpers merely for symmetry. A new
  boundary should make ownership or a future change easier to understand.
- Keep commits coherent and leave unrelated working-tree changes alone.

After several feature or performance changes, pause for a cleanup sweep. Review
names, module boundaries, duplicated logic, magic collection checks, mutable
state, oversized functions, comments, and tests. Refactor only where the result
is simpler and more idiomatic; run the normal gate again after the sweep.

## MySQL is the oracle

MySQL 8.4 decides compatibility, never SQLite or another convenience backend.
When an application suite disagrees with its SQLite baseline, reproduce the
case on real MySQL before attributing it to fsdb.

Differential tests should compare the observable contract relevant to the case:

- acceptance or rejection, including numeric error code and SQLSTATE;
- column names and compatible declared result types;
- typed rows and ordering where SQL guarantees an order;
- affected-row counts, warnings, and session state;
- committed schema and data after mutations.

Use the digest-pinned MySQL image in `torture/` for reproducible differential
work. Docker is also the preferred way to probe deliberately selected MySQL
versions: pin each image, record the version, and keep MySQL 8.4 as the primary
semantic baseline. A changed container tag is an oracle change, not routine
maintenance.

Every fixed torture finding should become a small Expecto regression. Do not
auto-enroll failures in `torture/support/known-gaps.json`; that ledger is reviewed
by hand and matches exact signatures.

## Commands

Run commands through `just` (see `justfile`):

- `just test [Expecto args...]` — full Expecto suite or a filtered run
- `just check` — build and test the root solution
- `just run [--port … --listen … --data-dir …]` — start the server
- `just client [port=…]` / `just smoke [port=…]` — connect or probe liveness
- `just coverage` — branch coverage with the pinned Coverlet tool
- `just bench`, `bench-features`, `bench-quick`, `bench-durable`,
  `bench-scale`, `bench-load`, `bench-load-scale`, or `bench-comprehensive` —
  compare fsdb with MySQL 8.4

Run one test with:

```sh
just test --filter-test-case <Substring>
# or: --filter "fsdb/<list>/<case>" / --run "<full test name>"
```

There is no CI in this repository. Run `just check` locally before handing off
a change, plus the relevant torture lane for compatibility work.

## Performance comparisons

Prefer native, same-host processes for performance work. This avoids Docker
networking, filesystem, and resource-scheduling noise. If containers are
necessary, run **both** fsdb and MySQL in equivalently constrained containers
with comparable networking and storage; a containerized MySQL versus native
fsdb result is not apples-to-apples.

Match durability as well as schema and workload:

- Compare fsdb with `--data-dir` against normally durable MySQL for deployment
  and write-performance claims.
- Compare in-memory fsdb only with MySQL configured without commit-time fsync
  when isolating engine work, and label that result as non-durable.
- Do not use the default in-memory fsdb numbers to claim durable write parity.
  They can hide fsdb's persistence cost and materially misstate the gap.

`just bench-durable` runs both matched pairs. The ordinary `bench`, feature,
quick, scale, and load runs use in-memory fsdb against durable MySQL. They are
useful for read paths, scaling slopes, and finding hotspots, but their write
timings have different storage semantics. Record revision, versions, modes,
data sizes, and resource constraints with benchmark evidence. Measure before
and after on the same host.

## F# compile order

F# source order is explicit. `src/Fsdb/Fsdb.fsproj` and the test, torture, and
benchmark projects list `<Compile Include>` entries in dependency order. Add a
new `.fs` file before every file that consumes it or the build fails with
"not defined".

## Layout

- `src/Fsdb/Foundation/` — configuration and low-level utilities
- `src/Fsdb/Sql/` — SQL values, syntax, parsing, collations, and functions
- `src/Fsdb/Engine/` — storage, catalogs, authorization, persistence, planning,
  and execution
- `src/Fsdb/Wire/` — MySQL protocol and connection lifecycle
- `src/Fsdb/Db.fs` — public embedding facade
- `src/Fsdb/Program.fs` — executable entry point
- `tests/Fsdb.Tests/` — Expecto unit and wire-level integration tests
- `benchmarks/Fsdb.Benchmarks/` — BenchmarkDotNet comparisons with MySQL 8.4
- `torture/` — separate differential and failure-injection solution

Keep `torture/Fsdb.Torture.slnx` out of the root solution and ordinary gates.
Its exit codes are 0 for parity/known gaps, 1 for infrastructure, 2 for a new
fsdb finding, and 3 for replay drift.

## Repository conventions and gotchas

- `docs/comment-style.md` governs comments and maintained prose. Comments
  explain intent, invariants, or external behavior rather than restating code.
  `ponytail:` marks bounded debt with both a current ceiling and an upgrade
  trigger; remove it when the limitation disappears. Avoid session narration,
  milestone names, roadmap references, and design-document references in code,
  comments, and test names.
- Documentation uses words or `[x]` checkboxes for status, not emoji markers.
  Avoid exact counts that merely repeat a table or inventory and will go stale.
- Connections authenticate against `mysql.user`. The bootstrap account is
  `root` with an empty password; unknown users receive error 1045. Text-probed
  `SET`, `SHOW`, `KILL`, and `USE` statements perform their own checks because
  they bypass the parsed-statement authorization gate.
- Persistence is opt-in through `--data-dir`. `wal.bin` contains length- and
  CRC-framed binary `CommitEvent` records; `snapshot.fsdb` is a self-delimiting
  binary tree. Unix uses libc `fsync`; Windows uses managed durable flush.
  `FileStream.Flush(true)` maps to `F_FULLFSYNC` on macOS and does not match
  MySQL's normal macOS durability behavior.
- Stored rows have stable, non-reused `RowId`s in fixed-size immutable pages.
  Point writes copy touched pages. Tombstones preserve other identities, and
  ordinary .NET reachability retains pages referenced by snapshots. Indexed
  writes lock row stripes; full scans and structural changes retain the
  per-database publication gate.
- The benchmark recipes resolve `mysql`, `mysqld`, and `mysqladmin` from
  `PATH`, use disposable native servers on ports 3316/3317, and refuse to share
  fsdb's selected port (3307 by default).
- Keep the repository's XML `.slnx` solution format; do not add `.sln` files.
