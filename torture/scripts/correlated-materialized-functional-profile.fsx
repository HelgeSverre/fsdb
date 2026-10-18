// Run after `just check`: dotnet fsi --nologo --exec torture/scripts/correlated-materialized-functional-profile.fsx
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/NJsonSchema.dll"
#r "../../src/Fsdb.Cli/bin/Debug/net10.0/Fsdb.dll"

open Fsdb

let mutable calls = 0
let db =
    Db.create ()
    |> Db.registerScalar "TOUCH" (fun values ->
        calls <- calls + 1
        List.head values)

let connection = Db.connect db
connection.Query "CREATE TABLE outer_bounds(id INT PRIMARY KEY, x INT)" |> ignore
connection.Query "CREATE TABLE inner_rows(id INT PRIMARY KEY, k INT)" |> ignore
connection.Query "INSERT INTO outer_bounds VALUES(1,10),(2,999),(3,NULL)" |> ignore

[ 0..999 ]
|> List.map (fun id -> sprintf "(%d,%d)" id id)
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO inner_rows VALUES" + values)
|> ignore

let probe predicate =
    calls <- 0
    let sql =
        "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
        + "WHERE TOUCH(d.id)=d.id AND " + predicate + "=o.x) "
        + "FROM outer_bounds o ORDER BY o.id"
    let result = connection.QueryValues(sql, [])
    let expected =
        [ [ Some(Value.VInt 1L); Some(Value.VInt 1L) ]
          [ Some(Value.VInt 2L); Some(Value.VInt 1L) ]
          [ Some(Value.VInt 3L); Some(Value.VInt 0L) ] ]
    match result with
    | Ok(_, rows) when rows = expected ->
        printfn "%s: expected rows; residual calls=%d" predicate calls
    | _ -> failwithf "Unexpected %s result: %A" predicate result

probe "d.k"
probe "ABS(d.k)"
probe "ABS(d.k)+0"
calls <- 0
let inResult =
    connection.QueryValues(
        "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
        + "WHERE TOUCH(d.id)=d.id AND ABS(d.k)+0 IN (o.x,o.x+1)) "
        + "FROM outer_bounds o ORDER BY o.id", [])
match inResult with
| Ok(_, [ [ Some(Value.VInt 1L); Some(Value.VInt 2L) ]
          [ Some(Value.VInt 2L); Some(Value.VInt 1L) ]
          [ Some(Value.VInt 3L); Some(Value.VInt 0L) ] ]) ->
    printfn "ABS(d.k)+0 IN (o.x,o.x+1): expected rows; residual calls=%d" calls
| _ -> failwithf "Unexpected integer IN result: %A" inResult
probe "COALESCE(d.k,0)"
probe "IFNULL(d.k,0)"
probe "COALESCE(d.k,NULL,0)"
(connection :> System.IDisposable).Dispose()
