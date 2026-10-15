# Fsdb

Fsdb is an embeddable F# database with a MySQL-compatible SQL engine and wire-protocol server. This preview targets .NET 10. It can host SQL inside a .NET application, extend SQL with F# functions and tables, or serve MySQL clients from the same engine.

Fsdb is still a prerelease. MySQL 8.4 is the compatibility target, but some behavior differs; see the [compatibility gaps](https://github.com/HelgeSverre/fsdb/blob/main/GAPS.md) before depending on a specific MySQL feature. The public API may change between previews.

## Install

```sh
dotnet add package Fsdb --version 0.1.0-preview.19
```

## Run SQL in process

The host owns the database and closes its connections on disposal. Parameters use Fsdb's typed `Value` union; `QueryValues` returns typed cells, with SQL `NULL` represented by `None`.

```fsharp
open Fsdb
open Fsdb.Value

let run () =
    use host = Db.create () |> Db.own
    use connection = host.Connect()

    connection.Execute("CREATE TABLE items (id INT PRIMARY KEY, name TEXT)", []) |> ignore
    connection.Execute("INSERT INTO items VALUES (?, ?)", [ VInt 1L; VString "first" ]) |> ignore

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

Dispose the host cleanly to checkpoint durable storage. Keep one connection per independent transaction; a connection is a stateful SQL session.

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

`Db.registerAggregate`, `Db.registerFunction`, and `Db.registerTable` provide other extension points. `Db.subscribeCommits` exposes committed changes, and `Db.serve` starts a stoppable MySQL-protocol listener. Connections also offer prepared commands and cancellable asynchronous methods.

The [embedding guide](https://github.com/HelgeSverre/fsdb#embedding--extensibility) covers these APIs in detail. The [package consumer example](https://github.com/HelgeSverre/fsdb/tree/main/examples/PackageConsumer) is a standalone project that exercises a published-style package reference. The command-line server is built separately and is not included in this library package.
