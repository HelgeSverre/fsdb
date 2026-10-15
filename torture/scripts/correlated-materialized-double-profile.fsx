// Run after `just check`: dotnet fsi --nologo --exec torture/scripts/correlated-materialized-double-profile.fsx
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
connection.Query "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x DOUBLE,d DECIMAL(12,2),i BIGINT)" |> ignore
connection.Query "CREATE TABLE inner_rows(id INT PRIMARY KEY,k DOUBLE)" |> ignore
connection.Query
    "INSERT INTO outer_bounds VALUES(1,12.5,12.50,12),(2,999.0,999.00,999),(3,NULL,NULL,NULL),(4,-12.5,-12.50,-12)"
|> ignore

[ 0..999 ]
|> List.map (fun id -> sprintf "(%d,%d.5)" id (id - 500))
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO inner_rows VALUES" + values)
|> ignore

for expression, bound in
    [ "d.k", "o.x"; "d.k+0e0", "o.x"; "d.k+0e0", "o.d"; "d.k+0e0", "o.i" ] do
    calls <- 0
    let sql =
        "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
        + "WHERE TOUCH(d.id)=d.id AND " + expression + "=" + bound + ") "
        + "FROM outer_bounds o ORDER BY o.id"
    match connection.QueryValues(sql, []) with
    | Ok(_, rows) -> printfn "%s=%s: rows=%A residual_calls=%d" expression bound rows calls
    | Error error -> failwithf "%s=%s: %A" expression bound error
