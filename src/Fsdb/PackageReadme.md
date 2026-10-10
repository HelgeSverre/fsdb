# Fsdb

Fsdb is an embeddable F# database with MySQL-compatible SQL and a MySQL wire-protocol server. This prerelease package targets .NET 10.

```fsharp
open Fsdb

let db = Db.create ()
let connection = Db.connect db

connection.Query "CREATE TABLE items (id INT PRIMARY KEY, name TEXT)" |> ignore
connection.Query "INSERT INTO items VALUES (1, 'first')" |> ignore

match connection.Query "SELECT name FROM items WHERE id = 1" with
| Executor.ResultSet(_, rows) -> printfn "%A" rows
| result -> printfn "%A" result
```

`Db.withDataDir` enables WAL-backed durability before opening connections. `Db.registerScalar`, `Db.registerAggregate`, `Db.registerFunction`, and `Db.registerTable` add host-defined behavior. `Db.serve` starts a stoppable MySQL-protocol listener.

The [repository README](https://github.com/HelgeSverre/fsdb#embedding--extensibility) documents the embedding interface and examples. The server command-line executable is distributed separately from this library package.
