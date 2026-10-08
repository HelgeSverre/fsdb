#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open System.Diagnostics
open Fsdb

let connection = Db.create () |> Db.connect
let run sql =
    match connection.Query sql with
    | Executor.Err(code, message) -> failwithf "%d %s" code message
    | result -> result

run "CREATE TABLE grouped_inputs(g INT,v INT)" |> ignore
for batch in [ 1 .. 10000 ] |> List.chunkBySize 250 do
    batch |> List.map (fun value -> sprintf "(%d,%d)" (value % 100) value)
    |> String.concat "," |> (+) "INSERT INTO grouped_inputs VALUES " |> run |> ignore

let groups = [ for key in 0 .. 99 -> key, [ for value in 1 .. 10000 do if value % 100 = key then yield value ] ]
let text value = Some(string value)
let cases =
    [ "scalar-sum", "SELECT SUM(v) AS s FROM grouped_inputs", Executor.ResultSet([ "s" ], [ [ text 50005000 ] ])
      "grouped-sum", "SELECT g,SUM(v) AS s FROM grouped_inputs GROUP BY g ORDER BY g",
      Executor.ResultSet([ "g"; "s" ], groups |> List.map (fun (key, values) -> [ text key; text (List.sum values) ]))
      "grouped-distinct", "SELECT g,SUM(v) AS s,COUNT(DISTINCT v) AS n FROM grouped_inputs GROUP BY g ORDER BY g",
      Executor.ResultSet([ "g"; "s"; "n" ], groups |> List.map (fun (key, values) -> [ text key; text (List.sum values); text values.Length ]))
      "grouped-count", "SELECT g,COUNT(*) AS n FROM grouped_inputs GROUP BY g ORDER BY g",
      Executor.ResultSet([ "g"; "n" ], groups |> List.map (fun (key, values) -> [ text key; text values.Length ]))
      "grouped-multiple", "SELECT g,SUM(v) AS s,COUNT(v) AS n,MIN(v) AS lo,MAX(v) AS hi FROM grouped_inputs GROUP BY g ORDER BY g",
      Executor.ResultSet([ "g"; "s"; "n"; "lo"; "hi" ], groups |> List.map (fun (key, values) ->
          [ text key; text (List.sum values); text values.Length; text (List.min values); text (List.max values) ])) ]

let verify sql expected =
    let actual = run sql
    if actual <> expected then failwithf "Unexpected result for %s: %A" sql actual

for _, sql, expected in cases do
    for _ in 1 .. 5 do verify sql expected

let samples =
    [ for sample in 0 .. 8 do
          let ordered = if sample % 2 = 0 then cases else List.rev cases
          for label, sql, expected in ordered do
              let allocated = GC.GetTotalAllocatedBytes true
              let timer = Stopwatch.StartNew()
              for _ in 1 .. 3 do verify sql expected
              timer.Stop()
              yield label, timer.Elapsed.TotalMilliseconds / 3.0, float (GC.GetTotalAllocatedBytes true - allocated) / 3.0 ]

for label, _, _ in cases do
    let values = samples |> List.filter (fun (name, _, _) -> name = label)
    let times = values |> List.map (fun (_, ms, _) -> ms)
    let allocations = values |> List.map (fun (_, _, bytes) -> bytes)
    let median values = values |> List.sort |> List.item (List.length values / 2)
    printfn "%s input=10000 median-ms=%.3f median-bytes=%.0f samples-ms=%A" label (median times) (median allocations) times
