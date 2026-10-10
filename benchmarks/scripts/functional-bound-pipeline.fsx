#r "../../src/Fsdb/bin/Release/net10.0/Fsdb.dll"

open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor

let store = Storage.create ()
let mutable session = Session.create 1 store

let handle sql =
    let next, result = QueryHandler.handle session sql
    session <- next
    match result with
    | Err(code, message) -> failwithf "%d: %s" code message
    | result -> result

handle "CREATE TABLE functional_profile(id INT PRIMARY KEY,v INT,KEY ix_sqrt ((SQRT(v))))" |> ignore
handle "CREATE TABLE functional_scan_profile(id INT PRIMARY KEY,v INT)" |> ignore

for batch in [ 1 .. 10_000 ] |> List.chunkBySize 500 do
    let values = batch |> List.map (fun id -> sprintf "(%d,%d)" id id) |> String.concat ","
    for table in [ "functional_profile"; "functional_scan_profile" ] do
        handle (sprintf "INSERT INTO %s VALUES %s" table values) |> ignore

let cases =
    [ "literal", "SELECT id FROM functional_profile WHERE SQRT(v)=3e0"
      "constant", "SELECT id FROM functional_profile WHERE SQRT(v)=SQRT(9)"
      "scan", "SELECT id FROM functional_scan_profile WHERE SQRT(v)=SQRT(9)" ]

for name, sql in cases do
    match handle ("EXPLAIN " + sql) with
    | ResultSet(columns, [ row ]) ->
        let cell column = row.[columns |> List.findIndex ((=) column)]
        let expectedType, expectedKey =
            if name = "scan" then Some "ALL", None else Some "ref", Some "ix_sqrt"
        if cell "type" <> expectedType || cell "key" <> expectedKey then
            failwithf "%s used the wrong access path: type=%A key=%A" name (cell "type") (cell "key")
    | result -> failwithf "unexpected EXPLAIN result for %s: %A" name result

let verify = function
    | ResultSet([ "id" ], [ [ Some "9" ] ]) -> ()
    | result -> failwithf "unexpected lookup result: %A" result

let setting name fallback =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | value -> Int32.Parse value

let iterations = setting "FSDB_PROFILE_ITERATIONS" 500
let trials = setting "FSDB_PROFILE_TRIALS" 5

let measure label action =
    GC.Collect()
    let before = GC.GetAllocatedBytesForCurrentThread()
    let clock = Stopwatch.StartNew()
    for _ in 1 .. iterations do action ()
    clock.Stop()
    printfn "%s,%d,%.3f,%d" label iterations clock.Elapsed.TotalMilliseconds (GC.GetAllocatedBytesForCurrentThread() - before)

let actions =
    [ for name, sql in cases do
        let statement =
            match Parser.parse sql with
            | Ok statement -> statement
            | Error message -> failwith message
        let executor () =
            Executor.execute store Functions.builtins Storage.defaultDatabase (0L, 0L) false statement
            |> snd
            |> verify
        let handler () = handle sql |> verify
        yield name + "-executor", executor
        yield name + "-handler", handler ]

printfn "case,iterations,elapsed_ms,allocated_bytes"

for _, action in actions do
    for _ in 1 .. 500 do action ()

for trial in 1 .. trials do
    for name, action in actions do
        measure (sprintf "%s-%d" name trial) action
