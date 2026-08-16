# fsdb

A MySQL-compatible database server written in idiomatic F#.

fsdb speaks the MySQL wire protocol — point `mysql`, PDO, or any MySQL driver at it
and run queries against an in-memory engine built as a pipeline of discriminated
unions: bytes → `Command` → AST → logical plan → lazy `seq` execution.

Beautiful F# is the primary goal; raw performance is not.

## Why

To see how far a parser-combinator SQL grammar, DU-based relational algebra, and
a registry-driven function system can go — the benchmark is running a real Laravel
application's migrations and test suite against it unmodified.

## Running

```sh
dotnet run --project src/Fsdb        # listens on 127.0.0.1:3307
mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'
```

```
USAGE: fsdb [--help] [--port <port>] [--listen <address>] [--data-dir <path>]

OPTIONS:

    --port, -p <port>     port to listen on (default 3307)
    --listen <address>    IP address to bind, or 'localhost' (default loopback)
    --data-dir <path>     enable durability: WAL + snapshots stored here,
                          replayed on startup
    --help                display this list of options.
```

Without `--data-dir`, fsdb is pure in-memory.

Install globally (publishes a single binary to `~/.local/bin/fsdb`):

```sh
just install      # then: fsdb --help
just uninstall
```

Or via the [justfile](justfile):

```sh
just run      # dotnet run --project src/Fsdb, flags pass through (--port, --listen)
just client   # open a mysql shell against a running server
just smoke    # quick SELECT 1 / SELECT @@version liveness probe
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

`Db.registerAggregate` works the same way for aggregate functions (`MEDIAN`,
custom rollups, ...). A custom function can override a built-in of the same
name — the registry doesn't distinguish "shipped with fsdb" from "registered
by the embedder". See `tests/Fsdb.Tests/IntegrationTests.fs` for a full
round-trip test (`SLUGIFY`/`MEDIAN`) against a real client over the wire.

## Status

All eight roadmap milestones are done: wire protocol, PDO/mysql-CLI
compatibility, the SQL engine core, Laravel migrations, test-suite parity,
the embedding API, opt-in persistence (`--data-dir`), and EXPLAIN +
multi-table DML. See [ROADMAP.md](ROADMAP.md) for the milestone plan,
acceptance gates, and per-milestone evidence.

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
| App D | 13 | 43 | in progress |
| App E | 13 | 487 | pending |

The applications are private codebases, identified here only by framework
version and size.

## Benchmarking

`benchmarks/Fsdb.Benchmarks` runs fsdb head-to-head against a native MySQL
8.4, same schema, same seeded data, same queries, via BenchmarkDotNet.

```sh
just bench        # full suite, ~10 min, results -> benchmarks/results/<git-sha>.md
just bench-quick  # ShortRun job for fast local iteration, no results file
```

Both servers run ad hoc (no brew services) and are torn down afterwards.
fsdb optimizes for readable, idiomatic F# over raw speed, so expect MySQL to
win most of these — the numbers are here to find and track the hotspots,
not to chase parity.

## License

MIT
