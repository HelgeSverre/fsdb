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

## Extensibility

Every function call — including built-ins like `CONCAT` and `JSON_EXTRACT` —
resolves through one registry, SQLite-style:

```fsharp
db |> Db.registerScalar "slugify" (function
    | [VString s] -> VString (slug s)
    | _ -> VNull)
```

## Status

Early days. See [ROADMAP.md](ROADMAP.md) for the milestone plan and what
currently works.

## Running

```sh
dotnet run --project src/Fsdb        # listens on 127.0.0.1:3307
mysql --protocol=tcp -h127.0.0.1 -P3307 -e 'SELECT 1'
```

## License

MIT
