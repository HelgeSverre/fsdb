module Fsdb.CorrelatedNestedProfile

open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor

[<EntryPoint>]
let main _ =
    let store = Storage.create ()
    let mutable session = Session.create 1 store
    let handle sql =
        let next, result = QueryHandler.handle session sql
        session <- next
        match result with
        | Err(code, message) -> failwithf "%d: %s" code message
        | result -> result
    handle "CREATE TABLE users(id INT PRIMARY KEY)" |> ignore
    handle "CREATE TABLE orders(id INT PRIMARY KEY,user_id INT,KEY ix_user_id(user_id))" |> ignore
    for batch in [ 1 .. 10_000 ] |> List.chunkBySize 500 do
        batch |> List.map (sprintf "(%d)") |> String.concat "," |> sprintf "INSERT INTO users VALUES %s" |> handle |> ignore
    for batch in [ 1 .. 50_000 ] |> List.chunkBySize 500 do
        batch
        |> List.map (fun id -> sprintf "(%d,%d)" id (((id - 1) % 10_000) + 1))
        |> String.concat ","
        |> sprintf "INSERT INTO orders VALUES %s"
        |> handle
        |> ignore
    let sql =
        "SELECT u.id,(SELECT COUNT(*) FROM orders c WHERE c.user_id=u.id "
        + "AND (SELECT c.id+u.id WHERE c.id % 2=0) IS NOT NULL) FROM users u WHERE u.id<=100"
    let statement =
        match Parser.parse sql with
        | Ok statement -> statement
        | Error message -> failwith message
    let run () =
        match Executor.execute store Functions.builtins Storage.defaultDatabase (0L, 0L) false statement |> snd with
        | ResultSet(_, rows) when rows.Length = 100 ->
            let total =
                rows
                |> List.sumBy (function
                    | [ Some _; Some count ] -> Int32.Parse count
                    | row -> failwithf "Unexpected row: %A" row)
            if total <> 250 then failwithf "Unexpected count total: %d" total
        | result -> failwithf "Unexpected result: %A" result
    for _ in 1 .. 10 do run ()
    printfn "READY pid=%d" Environment.ProcessId
    Console.Out.Flush()
    let clock = Stopwatch.StartNew()
    let mutable queries = 0
    while clock.Elapsed.TotalSeconds < 40.0 do
        run ()
        queries <- queries + 1
    printfn "queries=%d elapsed=%.3f" queries clock.Elapsed.TotalSeconds
    0
