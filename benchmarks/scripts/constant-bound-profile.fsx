#r "../../src/Fsdb/bin/Release/net10.0/Fsdb.dll"

open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor

let mutable session = Session.create 1 (Storage.create ())

let execute sql =
    let next, result = QueryHandler.handle session sql
    session <- next
    match result with
    | Err(code, message) -> failwithf "%d: %s" code message
    | result -> result

execute "CREATE TABLE bound_profile (id INT PRIMARY KEY)" |> ignore

for batch in [ 1 .. 20_000 ] |> List.chunkBySize 500 do
    batch
    |> List.map (sprintf "(%d)")
    |> String.concat ","
    |> sprintf "INSERT INTO bound_profile VALUES %s"
    |> execute
    |> ignore

let cases =
    [ "indexed", "SELECT id FROM bound_profile WHERE id=IF(1,ABS('15000'),3)", "const", 200
      "scan", "SELECT id FROM bound_profile WHERE id+0=IF(1,ABS('15000'),3)", "ALL", 10 ]

let checkQuery sql =
    match execute sql with
    | ResultSet([ "id" ], [ [ Some "15000" ] ]) -> ()
    | result -> failwithf "unexpected result for %s: %A" sql result

for name, sql, access, _ in cases do
    match execute ("EXPLAIN " + sql) with
    | ResultSet(_, [ row ]) when row.[4] = Some access -> ()
    | result -> failwithf "%s did not use %s access: %A" name access result
    for _ in 1 .. 20 do checkQuery sql

printfn "case,trial,iterations,elapsed_ms,allocated_bytes"

for trial in 1 .. 7 do
    for name, sql, _, iterations in cases do
        GC.Collect()
        let before = GC.GetAllocatedBytesForCurrentThread()
        let clock = Stopwatch.StartNew()
        for _ in 1 .. iterations do checkQuery sql
        clock.Stop()
        printfn "%s,%d,%d,%.3f,%d" name trial iterations clock.Elapsed.TotalMilliseconds (GC.GetAllocatedBytesForCurrentThread() - before)
