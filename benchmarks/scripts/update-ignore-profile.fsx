#r "../../src/Fsdb/bin/Debug/net10.0/Fsdb.dll"

open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor

let measure rowCount mode sql =
    let mutable session = Session.create 1 (Storage.create ())
    let execute sql =
        let next, result = QueryHandler.handle session sql
        session <- next
        match errorInfo result with
        | Some error -> failwithf "%s: %A" sql error
        | None -> result
    execute "CREATE TABLE parent(n INT PRIMARY KEY)" |> ignore
    execute "INSERT INTO parent VALUES(1),(2)" |> ignore
    execute "CREATE TABLE child(id INT PRIMARY KEY,n INT,payload INT,CONSTRAINT fk FOREIGN KEY(n) REFERENCES parent(n))" |> ignore
    for batch in [ 1 .. rowCount ] |> List.chunkBySize 250 do
        execute ("INSERT INTO child VALUES " + (batch |> List.map (sprintf "(%d,1,0)") |> String.concat ",")) |> ignore
    let update () =
        let result = execute sql
        if result <> Affected(uint64 rowCount) then failwithf "Unexpected affected rows: %A" result
        if not session.Diagnostics.IsEmpty then failwithf "Unexpected diagnostics: %A" session.Diagnostics
    for _ in 1 .. 3 do update ()
    for sample in 1 .. 5 do
        let allocated = GC.GetTotalAllocatedBytes true
        let timer = Stopwatch.StartNew()
        update ()
        timer.Stop()
        let bytes = GC.GetTotalAllocatedBytes true - allocated
        printfn "%s,%d,%d,%.3f,%d" mode rowCount sample timer.Elapsed.TotalMilliseconds bytes
    let payload, parent = if mode.EndsWith("payload", StringComparison.Ordinal) then "8", "1" else "0", "1"
    let expected = ResultSet([ "MIN(payload)"; "MAX(payload)"; "MIN(n)"; "MAX(n)"; "COUNT(*)" ],
                             [ [ Some payload; Some payload; Some parent; Some parent; Some(string rowCount) ] ])
    let actual = execute "SELECT MIN(payload),MAX(payload),MIN(n),MAX(n),COUNT(*) FROM child"
    if actual <> expected then failwithf "Unexpected final data: %A" actual

printfn "mode,rows,sample,milliseconds,allocated_bytes"
let workloads =
    [ for rowCount in [ 100; 1000; 5000 ] do
          for mode, sql in
              [ "plain_payload", "UPDATE child SET payload=payload+1"
                "ignore_payload", "UPDATE IGNORE child SET payload=payload+1"
                "plain_reference", "UPDATE child SET n=3-n"
                "ignore_reference", "UPDATE IGNORE child SET n=3-n" ] do
              yield rowCount, mode, sql ]
let ordered = if Environment.GetEnvironmentVariable "FSDB_PERF_REVERSE" = "1" then List.rev workloads else workloads
for rowCount, mode, sql in ordered do measure rowCount mode sql
