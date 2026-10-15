// Run after `just check`: dotnet fsi --nologo --exec torture/scripts/correlated-materialized-text-profile.fsx
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
connection.Query "CREATE TABLE outer_bounds(id INT PRIMARY KEY,x VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
connection.Query "CREATE TABLE inner_rows(id INT PRIMARY KEY,k VARCHAR(20) COLLATE utf8mb4_bin)" |> ignore
connection.Query "INSERT INTO outer_bounds VALUES(1,'alpha'),(2,'zeta'),(3,NULL)" |> ignore

[ 0..999 ]
|> List.map (fun id ->
    let text =
        if id = 10 then "ALPHA"
        elif id = 11 then "alpha"
        elif id = 12 then "Beta"
        else sprintf "word%d" id
    sprintf "(%d,'%s')" id text)
|> String.concat ","
|> fun values -> connection.Query("INSERT INTO inner_rows VALUES" + values)
|> ignore

for expression in [ "d.k"; "LOWER(d.k)" ] do
    calls <- 0
    let sql =
        "SELECT o.id,(SELECT COUNT(*) FROM (SELECT DISTINCT id,k FROM inner_rows) d "
        + "WHERE TOUCH(d.id)=d.id AND " + expression + "=o.x) "
        + "FROM outer_bounds o ORDER BY o.id"
    match connection.QueryValues(sql, []) with
    | Ok(_, rows) -> printfn "%s: rows=%A residual_calls=%d" expression rows calls
    | Error error -> failwithf "%s: %A" expression error
