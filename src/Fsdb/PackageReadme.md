# Fsdb

Fsdb is an embeddable F# database for .NET 10. Run SQL in process, extend the engine with F# functions and read-only tables, or serve MySQL clients from the same engine.

MySQL 8.4 is the compatibility target. Check the [compatibility guide](https://github.com/HelgeSverre/fsdb/blob/main/docs/compatibility.md) for supported paths and the [release notes](https://github.com/HelgeSverre/fsdb/tree/main/docs/releases) for your package version. The API and storage format may evolve across releases.

## Install

```sh
dotnet add package Fsdb
```

Add `--version` with a specific published version to pin a dependency.
If your SDK reports `NU1605` for `FSharp.Core`, use an SDK that supplies the
required version or update the implicit package reference in your F# project.
Fsdb 0.1.9 requires `FSharp.Core` 10.1.404 or later.

```xml
<ItemGroup>
  <PackageReference Update="FSharp.Core" Version="10.1.404" />
</ItemGroup>
```

## Run SQL in process

The host owns the database and closes its connections on disposal. Parameters use Fsdb's typed `Value` union; `QueryValues` returns typed cells, with SQL `NULL` represented by `None`.

```fsharp
open Fsdb
open Fsdb.Value

let run () =
    use host = Db.create () |> Db.own
    use connection = host.Connect()

    let execute sql values =
        match connection.Execute(sql, values) with
        | Executor.Affected _ -> ()
        | other -> failwithf "Write failed: %A" other

    execute "CREATE TABLE items (id INT PRIMARY KEY, name TEXT)" []
    execute "INSERT INTO items VALUES (?, ?)" [ VInt 1L; VString "first" ]

    match connection.QueryValues("SELECT name FROM items WHERE id = ?", [ VInt 1L ]) with
    | Ok([ "name" ], [ [ Some(VString name) ] ]) -> printfn "%s" name
    | Ok(columns, rows) -> failwithf "Unexpected result: %A %A" columns rows
    | Error error -> failwithf "Query failed: %A" error

run ()
```

This example uses an in-memory database. For WAL-backed storage, configure a directory before owning the host:

```fsharp
use host = Db.create () |> Db.withDataDir "./fsdb-data" |> Db.own
```

Dispose the host cleanly to checkpoint durable storage. A connection is a stateful SQL session. Use one connection per independent transaction, and configure the data directory before opening connections or subscribing to commits.

Use `connection.Prepare` to reuse a statement with different positional values. The disposable command offers typed queries and cancellable asynchronous methods. The [package consumer example](https://github.com/HelgeSverre/fsdb/tree/main/examples/PackageConsumer) demonstrates a prepared query, a scalar extension, and a commit subscription in one F# project.

## Extend or serve the database

Register a scalar function before opening connections, then call it from SQL:

```fsharp
open Fsdb
open Fsdb.Functions
open Fsdb.Value

let run () =
    let db =
        Db.create ()
        |> Db.registerScalar "DOUBLE_IT" (function
            | [ VInt value ] -> VInt(value * 2L)
            | [ VNull ] -> VNull
            | _ -> raise (SqlError(1582, "DOUBLE_IT expects one integer")))

    use host = Db.own db
    use connection = host.Connect()

    match connection.QueryValues("SELECT DOUBLE_IT(?) AS doubled", [ VInt 21L ]) with
    | Ok(_, [ [ Some(VInt 42L) ] ]) -> ()
    | other -> failwithf "Unexpected result: %A" other

run ()
```

| API | Use |
|---|---|
| `Db.registerAggregate`, `Db.registerFunction` | Add grouped computations or scalars with query context. |
| `Db.registerTable` | Expose host data as a read-only SQL table. |
| `Db.subscribeCommits`, `host.SubscribeCommits()` | Consume committed row and schema changes. |
| `Db.serve` | Start a MySQL-protocol listener and get its bound endpoint. |

The command-line server is a separate build. For listener configuration, host lifetime, and additional examples, see the [embedding guide](https://github.com/HelgeSverre/fsdb#embedding--extensibility).
