module Fsdb.EmbeddedApiPerf

open System
open System.Diagnostics
open Fsdb
open Fsdb.Value

let check = function
    | Executor.Err(code, message) -> failwithf "%d: %s" code message
    | _ -> ()

[<EntryPoint>]
let main _ =
    use connection = Db.create () |> Db.connect
    connection.Query "CREATE TABLE perf (id INT PRIMARY KEY, value INT)" |> check
    for batch in [ 1 .. 1000 ] |> List.chunkBySize 100 do
        let values = batch |> List.map (fun n -> sprintf "(%d,%d)" n n) |> String.concat ","
        connection.Query("INSERT INTO perf VALUES " + values) |> check

    use prepared =
        match connection.Prepare "SELECT value FROM perf WHERE id=?" with
        | Ok command -> command
        | Error error -> failwithf "Prepare failed: %A" error

    let cases =
        [ "Query literal", (fun () -> connection.Query "SELECT value FROM perf WHERE id=42" |> check)
          "Execute parameter", (fun () -> connection.Execute("SELECT value FROM perf WHERE id=?", [ VInt 42L ]) |> check)
          "QueryValues parameter", (fun () ->
              match connection.QueryValues("SELECT value FROM perf WHERE id=?", [ VInt 42L ]) with
              | Ok _ -> ()
              | Error error -> failwithf "%A" error)
          "Prepared Execute", (fun () -> prepared.Execute [ VInt 42L ] |> check)
          "Prepared QueryValues", (fun () ->
              match prepared.QueryValues [ VInt 42L ] with
              | Ok _ -> ()
              | Error error -> failwithf "%A" error) ]

    let trials = 5
    let iterations = 500
    for _, action in cases do
        for _ in 1 .. 50 do action ()

    let samples = System.Collections.Generic.Dictionary<string, ResizeArray<float * float>>()
    for name, _ in cases do samples.Add(name, ResizeArray())
    for trial in 1 .. trials do
        let ordered = if trial % 2 = 0 then List.rev cases else cases
        for name, action in ordered do
            GC.Collect()
            let before = GC.GetAllocatedBytesForCurrentThread()
            let timer = Stopwatch.StartNew()
            for _ in 1 .. iterations do action ()
            timer.Stop()
            samples.[name].Add(
                timer.Elapsed.TotalMilliseconds * 1000.0 / float iterations,
                float (GC.GetAllocatedBytesForCurrentThread() - before) / float iterations)

    printfn "case,median_us_per_op,median_bytes_per_op"
    for name, _ in cases do
        let ordered = samples.[name] |> Seq.sortBy fst |> Seq.toArray
        let microseconds, bytes = ordered.[trials / 2]
        printfn "%s,%.1f,%.0f" name microseconds bytes
    0
