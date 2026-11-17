module Fsdb.CorrelatedRangeProfile

open System
open System.Diagnostics
open Fsdb
open Fsdb.Executor

[<EntryPoint>]
let main args =
    let workload = if args.Length > 0 then args.[0] else "range"
    let seconds = if args.Length > 1 then Double.Parse(args.[1], Globalization.CultureInfo.InvariantCulture) else 8.0
    let predicate =
        match workload with
        | "range" -> " AND i.k BETWEEN 12.5 AND 13"
        | "equality" -> ""
        | other -> invalidArg "workload" ("Unknown workload: " + other)
    let mutable session = Session.create 1 (Storage.create ())
    let handle sql =
        let next, result = QueryHandler.handle session sql
        session <- next
        match result with
        | Err(code, message) -> failwithf "%d: %s" code message
        | result -> result
    for sql in
        [ "CREATE TABLE outer_rows(id INT)"
          "CREATE TABLE inner_rows(owner_id INT,k DOUBLE,KEY ix_owner(owner_id),KEY ix_k(k))"
          "INSERT INTO inner_rows VALUES(1,12.5),(2,12.5)" ] do
        handle sql |> ignore
    [ 1 .. 100 ]
    |> List.map (fun i -> sprintf "(%d)" (1 + i % 2))
    |> String.concat ","
    |> sprintf "INSERT INTO outer_rows VALUES%s"
    |> handle |> ignore
    [ 1 .. 10_000 ] |> List.chunkBySize 500 |> List.iter (fun batch ->
        batch |> List.map (fun i -> sprintf "(9,%d)" (i + 100))
        |> String.concat "," |> sprintf "INSERT INTO inner_rows VALUES%s"
        |> handle |> ignore)
    let sql =
        "SELECT o.id,(SELECT COUNT(*) FROM inner_rows i WHERE i.owner_id=o.id"
        + predicate + ") c FROM outer_rows o"
    let run () =
        match handle sql with
        | ResultSet(_, rows) when rows.Length = 100 ->
            if rows |> List.exists (function [ Some _; Some "1" ] -> false | _ -> true) then
                failwithf "Incorrect counts: %A" rows
        | result -> failwithf "Unexpected result: %A" result
    for _ in 1 .. 10 do run ()
    let allocatedBefore = GC.GetTotalAllocatedBytes true
    let clock = Stopwatch.StartNew ()
    let mutable queries = 0
    while clock.Elapsed.TotalSeconds < seconds do
        run ()
        queries <- queries + 1
    let elapsed = clock.Elapsed.TotalSeconds
    let allocated = GC.GetTotalAllocatedBytes true - allocatedBefore
    printfn "workload=%s queries=%d elapsed=%.6f allocated=%d" workload queries elapsed allocated
    0
