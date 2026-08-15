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

M1 through M5 are done: wire protocol, PDO/mysql-CLI compatibility, the SQL
engine core, Laravel migrations, and a 304-test Laravel (Pest) suite running
against fsdb at exact parity with its sqlite baseline (287 passed, 15
skipped, 2 todos, 787 assertions on both sides). See [ROADMAP.md](ROADMAP.md)
for the milestone-by-milestone plan and acceptance gates.

## License

MIT
