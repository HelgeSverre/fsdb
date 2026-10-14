#I "../../src/Fsdb.Cli/bin/Release/net10.0"
#r "FParsec.dll"
#r "FParsecCS.dll"
#r "Namotion.Reflection.dll"
#r "Newtonsoft.Json.dll"
#r "NJsonSchema.Annotations.dll"
#r "NJsonSchema.dll"
#r "NetTopologySuite.dll"
#r "ZstdSharp.dll"
#r "Fsdb.dll"
#I "../Fsdb.Benchmarks/bin/Release/net10.0"
#r "MySqlConnector.dll"
#load "../Fsdb.Benchmarks/Schema.fs"

open System
open System.Diagnostics
open System.IO
open System.Threading
open System.Threading.Tasks
open Fsdb
open Fsdb.Executor
open Fsdb.Benchmarks

let setting name fallback =
    match Environment.GetEnvironmentVariable name with
    | null -> fallback
    | value -> Int32.Parse value

let workers =
    match Environment.GetEnvironmentVariable "FSDB_EMBEDDED_WORKERS" with
    | null -> [ 1; 4; 8 ]
    | value -> value.Split(',') |> Array.map Int32.Parse |> Array.toList

let trials = setting "FSDB_EMBEDDED_TRIALS" 2
let warmupSeconds = float (setting "FSDB_EMBEDDED_WARMUP" 1)
let measureSeconds = float (setting "FSDB_EMBEDDED_SECONDS" 5)
let durable =
    match Environment.GetEnvironmentVariable "FSDB_EMBEDDED_TARGET" with
    | null | "wal" -> true
    | "memory" -> false
    | value -> failwithf "Unknown embedded benchmark target: %s" value

let query (connection: Db.Connection) sql =
    match connection.Query sql with
    | Err(code, message) -> failwithf "%s failed (%d): %s" sql code message
    | result -> result

let runWorkers (host: Db.DatabaseHost) workerCount seconds (counter: int64 ref) =
    let connections =
        Array.init workerCount (fun _ ->
            let connection = host.Connect()
            query connection "USE fsdb_bench" |> ignore
            connection)
    try
        use start = new ManualResetEventSlim(false)

        let tasks =
            connections
            |> Array.mapi (fun worker connection ->
                Task.Run(fun () ->
                    start.Wait()
                    let clock = Stopwatch.StartNew()
                    let mutable completed = 0L
                    while clock.Elapsed.TotalSeconds < seconds do
                        let eventId = Interlocked.Increment(&counter.contents)
                        query connection $"INSERT INTO keyless_events (worker_id, event_id) VALUES ({worker}, {eventId})"
                        |> ignore
                        completed <- completed + 1L
                    completed))

        let clock = Stopwatch.StartNew()
        start.Set()
        let completed = tasks |> Array.sumBy (fun task -> task.GetAwaiter().GetResult())
        clock.Stop()
        completed, clock.Elapsed.TotalSeconds
    finally
        connections |> Array.iter (fun connection -> (connection :> IDisposable).Dispose())

let measure workerCount =
    let directory = Directory.CreateTempSubdirectory("fsdb-embedded-load-")

    try
        let initial = Db.create ()
        let db = if durable then Db.withDataDir directory.FullName initial else initial

        use host = Db.own db
        use admin = host.Connect()
        query admin "CREATE DATABASE fsdb_bench" |> ignore
        query admin "USE fsdb_bench" |> ignore
        Schema.createSchemaWith (fun sql -> query admin sql |> ignore)
        Schema.seedWith (fun sql -> query admin sql |> ignore)
        query admin "CREATE TABLE keyless_events (worker_id INT NOT NULL, event_id BIGINT NOT NULL)"
        |> ignore

        let counter = ref 0L
        let warmup, _ = runWorkers host workerCount warmupSeconds counter
        let completed, elapsed = runWorkers host workerCount measureSeconds counter

        match query admin "SELECT COUNT(*) FROM keyless_events" with
        | ResultSet(_, [ [ Some stored ] ]) when int64 stored = warmup + completed ->
            float completed / elapsed
        | result -> failwithf "Expected %d completed inserts, got %A" (warmup + completed) result
    finally
        directory.Delete(true)

printfn "target=%s workers=%A trials=%d warmup=%.1fs measure=%.1fs"
    (if durable then "embedded-wal" else "embedded-memory") workers trials warmupSeconds measureSeconds
printfn "workers,trial,ops_per_sec"

for workerCount in workers do
    for trial in 1 .. trials do
        printfn "%d,%d,%.0f" workerCount trial (measure workerCount)
