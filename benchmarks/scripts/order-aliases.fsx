#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open System.Diagnostics
open Fsdb

let connection = Db.create () |> Db.connect
let run sql =
    match connection.Query sql with
    | Executor.Err(code, message) -> failwithf "%d %s" code message
    | result -> result

run "CREATE TABLE ordering_values(v INT)" |> ignore
for batch in [ 10000 .. -1 .. 1 ] |> List.chunkBySize 250 do
    batch
    |> List.map (sprintf "(%d)")
    |> String.concat ","
    |> (+) "INSERT INTO ordering_values VALUES "
    |> run
    |> ignore

let cases =
    [ "source", "SELECT v AS a FROM ordering_values ORDER BY ABS(v) LIMIT 100"
      "alias", "SELECT v AS a FROM ordering_values ORDER BY ABS(a) LIMIT 100"
      "correlated-alias", "SELECT v AS a FROM ordering_values ORDER BY ABS((SELECT a)) LIMIT 100" ]
let expected = Executor.ResultSet([ "a" ], [ for value in 1 .. 100 -> [ Some(string value) ] ])
let verify sql =
    let actual = run sql
    if actual <> expected then failwithf "Unexpected result for %s: %A" sql actual

for _, sql in cases do
    for _ in 1 .. 5 do verify sql

let samples =
    [ for sample in 0 .. 8 do
          // Alternate the first query to reduce systematic ordering bias.
          let ordered = if sample % 2 = 0 then cases else List.rev cases
          for label, sql in ordered do
              let allocated = GC.GetTotalAllocatedBytes true
              let timer = Stopwatch.StartNew()
              for _ in 1 .. 5 do verify sql
              timer.Stop()
              yield label, timer.Elapsed.TotalMilliseconds / 5.0, float (GC.GetTotalAllocatedBytes true - allocated) / 5.0 ]

for label, _ in cases do
    let values = samples |> List.filter (fun (name, _, _) -> name = label)
    let times = values |> List.map (fun (_, ms, _) -> ms)
    let allocations = values |> List.map (fun (_, _, bytes) -> bytes)
    let median values = values |> List.sort |> List.item (List.length values / 2)
    printfn "%s input=10000 output=100 median-ms=%.3f median-bytes=%.0f samples-ms=%A" label (median times) (median allocations) times
