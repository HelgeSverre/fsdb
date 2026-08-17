# fsdb

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4.svg)](global.json)
[![MySQL 8.4](https://img.shields.io/badge/MySQL-8.4%20compatible-4479A1.svg)](https://dev.mysql.com/)

A MySQL-compatible database server in idiomatic F#. It speaks the MySQL wire
protocol — point `mysql`, PDO, or any MySQL driver at it and run queries against
an in-memory engine built as a pipeline of discriminated unions: bytes →
`Command` → AST → logical plan → lazy `seq` execution.

Readable F# is the primary goal; raw performance is not.

## Why

To see how far a parser-combinator SQL grammar, DU-based relational algebra, and
a registry-driven function system can go — the benchmark is running a real Laravel
application's migrations and test suite against it unmodified.

## Running

Requires the .NET 10 SDK (pinned by `global.json`).

```sh
dotnet run --project src/Fsdb        # listens on 127.0.0.1:3307
mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'
```

```
USAGE: fsdb [--help] [--port <port>] [--listen <address>] [--data-dir <path>]
            [--version]

OPTIONS:

    --port, -p <port>     listen port (default 3307)
    --listen <address>    bind address (default 127.0.0.1)
    --data-dir <path>     persist data here (WAL + snapshots); omit for
                          in-memory
    --version             print the fsdb version and exit
    --help                display this list of options.
```

Install globally (publishes a single binary to `~/.local/bin/fsdb`):

```sh
just install      # then: fsdb --help
just uninstall
```

Or via the [justfile](justfile):

```sh
just run      # dotnet run --project src/Fsdb, flags pass through (--port, --listen)
just client   # open a mysql shell against a running server (optional port=)
just smoke    # quick SELECT 1 / SELECT @@version liveness probe (optional port=)
just test     # run the Expecto suite
just check    # build + test
```

## Extensibility

Every function call — including built-ins like `CONCAT` and `JSON_EXTRACT` —
resolves through one registry, SQLite-style. Embed fsdb in your own program and
register a function before you start listening:

```fsharp
open Fsdb
open Fsdb.Value

let slug (s: string) =
    System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim '-'

[<EntryPoint>]
let main _ =
    Db.create ()
    |> Db.registerScalar "slugify" (function
        | [ VString s ] -> VString(slug s)
        | _ -> VNull)
    |> Db.listen System.Net.IPAddress.Loopback 3307
    |> Async.RunSynchronously

    0
```

`Db.registerAggregate` works the same way for aggregate functions — the fold
receives every non-NULL row's value and returns one:

```fsharp
let median (values: Value list) =
    let sorted = values |> List.choose (function VInt i -> Some(float i) | _ -> None) |> List.sort

    match sorted with
    | [] -> VNull
    | _ ->
        let mid = sorted.Length / 2

        if sorted.Length % 2 = 0 then
            VDouble((sorted.[mid - 1] + sorted.[mid]) / 2.0)
        else
            VDouble sorted.[mid]

let db =
    Db.create ()
    |> Db.registerAggregate "MEDIAN" median
```

A custom function can override a built-in of the same name — the registry
doesn't distinguish "shipped with fsdb" from "registered by the embedder":

```fsharp
// Deterministic timestamps for reproducible tests.
Db.create ()
|> Db.registerScalar "NOW" (fun _ -> VDateTime(System.DateTime(2026, 1, 1)))
```

Add `--data-dir` durability to the embedded server the same way — a binary WAL
and snapshot, replayed on restart:

```fsharp
Db.create () |> Db.withDataDir "./fsdb-data" |> Db.listen System.Net.IPAddress.Loopback 3307
```

`Db.withLogger` hooks a log sink in the same style. See
`tests/Fsdb.Tests/IntegrationTests.fs` for the full round-trip test
(`SLUGIFY`/`MEDIAN`) against a real client over the wire.

## Status

All ten roadmap milestones are done: wire protocol, PDO/mysql-CLI
compatibility, the SQL engine core, Laravel migrations, test-suite parity,
the embedding API, opt-in persistence (`--data-dir`), EXPLAIN +
multi-table DML, performance-without-ugliness, and the streaming pipeline.
See [ROADMAP.md](docs/ROADMAP.md) for the milestone plan, acceptance gates,
and per-milestone evidence.

### Compatibility gauntlet

fsdb is validated by migrating and running the test suites of real Laravel
applications against it, unmodified. Where a suite diverges from its sqlite
baseline, the dispute is settled by running the same tests against a real
MySQL 8.4 — fsdb must match MySQL, not sqlite.

| Application | Laravel | Migrations | Result |
|---|---|---|---|
| App A | 11 | 94 | full parity, 0 failures |
| App B | 11 | 205 | parity; 5 residual failures reproduce identically on real MySQL (app-side factory/collation bugs) |
| App C | 10 | 160 | behavioral equivalence with real MySQL (identical failure set from an app-side factory bug) |
| App D | 13 | 43 | parity; 1 residual failure is a sqlite-only PRAGMA introspection test that fails identically on real MySQL |
| App E | 13 | 487 | full 10,972-test suite: 10,913 passed, all 36 failures individually verified as app-side bugs or real-MySQL-identical; one documented order divergence on an unordered query |

The applications are private codebases, identified here only by framework
version and size.

## Benchmarking

`benchmarks/Fsdb.Benchmarks` runs fsdb head-to-head against a native MySQL
8.4, same schema, same seeded data, same queries, via BenchmarkDotNet.

```sh
just bench         # full latency suite, ~30 min, results -> benchmarks/results/<git-sha>.md
just bench-quick   # ShortRun job for fast local iteration, no results file
just bench-durable # durability-matched: fsdb WAL vs MySQL fsync/no-fsync, results -> <git-sha>-durable.md
just bench-scale   # latency suite at 100k users / 500k orders
just bench-load    # N-writer throughput under concurrency (ops/sec)
```

Both servers run ad hoc (no brew services) and are torn down afterwards.
fsdb optimizes for readable, idiomatic F# over raw speed, so expect MySQL to
win most of these — the numbers are here to find and track the hotspots,
not to chase parity.

`torture/` is a separate differential fuzz harness: generated SQL run against
both fsdb and a MySQL 8.4 oracle, with the first divergence classified and
replayed (`torture/scripts/run.sh suite`; exit 0 = pass/known gaps, 2 = new
fsdb findings).

## License

MIT — see [LICENSE](LICENSE).
