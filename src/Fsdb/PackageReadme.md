# Fsdb

Fsdb is an embeddable F# database with MySQL-compatible SQL and a MySQL wire-protocol server. This prerelease package targets .NET 10.

```fsharp
open Fsdb

let demo () =
    use host = Db.create () |> Db.own
    use connection = host.Connect()

    connection.Execute("CREATE TABLE items (id INT PRIMARY KEY, name TEXT)", []) |> ignore
    connection.Execute("INSERT INTO items VALUES (?, ?)", [ Value.VInt 1L; Value.VString "first" ]) |> ignore

    match connection.QueryValues("SELECT name FROM items WHERE id = ?", [ Value.VInt 1L ]) with
    | Ok(_, rows) -> printfn "%A" rows
    | Error error -> printfn "%A" error
```

`Db.withDataDir` enables WAL-backed durability before opening connections. `Db.registerScalar`, `Db.registerAggregate`, `Db.registerFunction`, and `Db.registerTable` add host-defined behavior. `Db.subscribeCommits` provides a disposable queued change feed; `Db.serve` starts a stoppable MySQL-protocol listener. `Connection.QueryAsync`, `ExecuteAsync`, and `QueryValuesAsync` accept cancellation tokens.

The [repository README](https://github.com/HelgeSverre/fsdb#embedding--extensibility) documents the embedding interface and examples. The server command-line executable is distributed separately from this library package.
