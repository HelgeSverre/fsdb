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

handle "CREATE TABLE lookup_profile (id INT PRIMARY KEY)" |> ignore

for batch in [ 1 .. 10_000 ] |> List.chunkBySize 500 do
    batch
    |> List.map (sprintf "(%d)")
    |> String.concat ","
    |> sprintf "INSERT INTO lookup_profile VALUES %s"
    |> handle
    |> ignore

let cases =
    [ "fixed", "SELECT id FROM lookup_profile WHERE id=3"
      "round-pi", "SELECT id FROM lookup_profile WHERE id=ROUND(PI())"
      "bit-count", "SELECT id FROM lookup_profile WHERE id=BIT_COUNT(7)" ]

let verify = function
    | ResultSet([ "id" ], [ [ Some "3" ] ]) -> ()
    | result -> failwithf "unexpected lookup result: %A" result

let iterations =
    Environment.GetEnvironmentVariable "FSDB_PROFILE_ITERATIONS"
    |> function
        | null -> 2_000
        | value -> Int32.Parse value

let trials =
    Environment.GetEnvironmentVariable "FSDB_PROFILE_TRIALS"
    |> function
        | null -> 5
        | value -> Int32.Parse value

let measure label action =
    GC.Collect()
    let before = GC.GetAllocatedBytesForCurrentThread()
    let clock = Stopwatch.StartNew()
    for _ in 1 .. iterations do action ()
    clock.Stop()
    printfn "%s,%d,%.3f,%d" label iterations clock.Elapsed.TotalMilliseconds (GC.GetAllocatedBytesForCurrentThread() - before)

printfn "case,iterations,elapsed_ms,allocated_bytes"

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
        let parser () =
            match Parser.parse sql with
            | Ok _ -> ()
            | Error message -> failwith message
        let handler () = handle sql |> verify
        yield name + "-parser", parser
        yield name + "-executor", executor
        yield name + "-handler", handler ]

let measuredActions =
    actions |> List.filter (fun (name, _) ->
        match Environment.GetEnvironmentVariable "FSDB_PROFILE_ACTION" with
        | null -> true
        | selected -> selected = name)

for _, action in measuredActions do
    for _ in 1 .. 1_000 do action ()

for trial in 1 .. trials do
    for name, action in measuredActions do
        measure (sprintf "%s-%d" name trial) action
