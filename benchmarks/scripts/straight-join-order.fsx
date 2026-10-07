#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System.Diagnostics
open Fsdb

let connection = Db.create () |> Db.connect

let run sql =
    match connection.Query sql with
    | Executor.Err(code, message) -> failwithf "%d %s" code message
    | result -> result

run "CREATE TABLE bases(id INT PRIMARY KEY)" |> ignore
run "CREATE TABLE many_rows(id INT PRIMARY KEY,base_id INT,KEY(base_id))" |> ignore
run "CREATE TABLE few_rows(id INT PRIMARY KEY,base_id INT,KEY(base_id))" |> ignore

[ 1..500 ]
|> List.map (sprintf "(%d)")
|> String.concat ","
|> (+) "INSERT INTO bases VALUES "
|> run
|> ignore

for batch in [ 1..25000 ] |> List.chunkBySize 250 do
    batch
    |> List.map (fun id -> sprintf "(%d,%d)" id ((id - 1) % 500 + 1))
    |> String.concat ","
    |> (+) "INSERT INTO many_rows VALUES "
    |> run
    |> ignore

run "INSERT INTO few_rows VALUES(1,7)" |> ignore

let query modifier firstJoin =
    sprintf
        "SELECT %sb.id FROM bases b %s many_rows l ON l.base_id=b.id JOIN few_rows s ON s.base_id=b.id ORDER BY b.id"
        modifier
        firstJoin

let cases =
    [ "table constraint", query "" "STRAIGHT_JOIN"
      "global constraint", query "STRAIGHT_JOIN " "JOIN" ]

for label, sql in cases do
    for _ in 1..5 do run sql |> ignore
    let samples =
        [ for _ in 1..7 do
              let timer = Stopwatch.StartNew()
              for _ in 1..10 do run sql |> ignore
              yield timer.Elapsed.TotalMilliseconds / 10.0 ]

    match run sql with
    | Executor.ResultSet(_, rows) when rows = List.replicate 50 [ Some "7" ] -> ()
    | other -> failwithf "%s: expected 50 matching rows, got %A" label other

    let median = samples |> List.sort |> List.item (samples.Length / 2)
    printfn "%s rows=50 median-ms=%.3f samples=%A" label median samples
