module Fsdb.EmbeddedApiPerf

open System
open System.Collections.Generic
open System.Diagnostics
open Fsdb
open Fsdb.Value

type Measurement =
    { MicrosecondsPerOperation: float
      BytesPerOperation: float }

let private positiveEnvironment name fallback =
    match Environment.GetEnvironmentVariable name with
    | null | "" -> fallback
    | value ->
        match Int32.TryParse value with
        | true, parsed when parsed > 0 -> parsed
        | _ -> failwithf "%s must be a positive integer, got %A" name value

let private check label = function
    | Executor.Err(code, message) -> failwithf "%s failed with %d: %s" label code message
    | _ -> ()

let private expectAffected label expected = function
    | Executor.Affected actual when actual = expected -> ()
    | actual -> failwithf "%s: expected %d affected rows, got %A" label expected actual

let private expectRows label expectedColumns expectedRows = function
    | Ok(columns, rows) when columns = expectedColumns && rows = expectedRows -> ()
    | actual ->
        failwithf
            "%s: expected columns %A and rows %A, got %A"
            label
            expectedColumns
            expectedRows
            actual

let private prepare (connection: Db.Connection) sql =
    match connection.Prepare sql with
    | Ok command -> command
    | Error error -> failwithf "Prepare failed for %s: %A" sql error

let private validatePreparedLookup (connection: Db.Connection) =
    check "create lookup table" (connection.Query "CREATE TABLE lookup_items (id INT PRIMARY KEY, value INT NULL, note VARCHAR(20) NULL)")

    use insert = prepare connection "INSERT INTO lookup_items VALUES (?, ?, ?)"

    [ [ VInt 1L; VInt 10L; VString "one" ]
      [ VInt 2L; VNull; VString "two" ]
      [ VInt 3L; VInt 30L; VNull ] ]
    |> List.iteri (fun index values -> insert.Execute(values) |> expectAffected (sprintf "insert row %d" (index + 1)) 1UL)

    use lookup = prepare connection "SELECT id, value, note FROM lookup_items WHERE id=?"

    lookup.QueryValues [ VInt 1L ]
    |> expectRows
        "prepared lookup returns the first parameter's typed row"
        [ "id"; "value"; "note" ]
        [ [ Some(VInt 1L); Some(VInt 10L); Some(VString "one") ] ]

    lookup.QueryValues [ VInt 2L ]
    |> expectRows
        "prepared lookup changes values and preserves SQL NULL"
        [ "id"; "value"; "note" ]
        [ [ Some(VInt 2L); None; Some(VString "two") ] ]

    lookup.QueryValues [ VInt 3L ]
    |> expectRows
        "prepared lookup changes values again and preserves trailing SQL NULL"
        [ "id"; "value"; "note" ]
        [ [ Some(VInt 3L); Some(VInt 30L); None ] ]

    lookup.QueryValues [ VInt 99L ]
    |> expectRows "prepared lookup returns an empty typed result for a missing row" [ "id"; "value"; "note" ] []

let private validatePreparedDatabaseBinding (connection: Db.Connection) =
    check "create database prepared_context_a" (connection.Query "CREATE DATABASE prepared_context_a")
    check "create database prepared_context_b" (connection.Query "CREATE DATABASE prepared_context_b")
    check "use prepared_context_a" (connection.Query "USE prepared_context_a")
    check "create context table a" (connection.Query "CREATE TABLE context_items (id INT PRIMARY KEY, value INT)")
    check "insert context row a" (connection.Query "INSERT INTO context_items VALUES (1, 7)")

    use lookup = prepare connection "SELECT value FROM context_items WHERE id=?"

    check "use prepared_context_b" (connection.Query "USE prepared_context_b")
    check "create context table b" (connection.Query "CREATE TABLE context_items (id INT PRIMARY KEY, value INT)")
    check "insert context row b" (connection.Query "INSERT INTO context_items VALUES (1, 99)")

    lookup.QueryValues [ VInt 1L ]
    |> expectRows
        "prepared lookup stays bound to the database selected at preparation"
        [ "value" ]
        [ [ Some(VInt 7L) ] ]

    connection.QueryValues("SELECT value FROM context_items WHERE id=?", [ VInt 1L ])
    |> expectRows
        "prepared execution restores the connection's current database"
        [ "value" ]
        [ [ Some(VInt 99L) ] ]

let private measure iterations action =
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()
    let allocatedBefore = GC.GetAllocatedBytesForCurrentThread()
    let timer = Stopwatch.StartNew()

    for _ in 1 .. iterations do
        action ()

    timer.Stop()
    { MicrosecondsPerOperation = timer.Elapsed.TotalMilliseconds * 1000.0 / float iterations
      BytesPerOperation = float (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / float iterations }

let private median values =
    let ordered = values |> Seq.sort |> Seq.toArray
    ordered.[ordered.Length / 2]

[<EntryPoint>]
let main _ =
    let trials = positiveEnvironment "FSDB_EMBEDDED_API_TRIALS" 5
    let iterations = positiveEnvironment "FSDB_EMBEDDED_API_ITERATIONS" 500

    use connection = Db.create () |> Db.connect
    validatePreparedLookup connection
    validatePreparedDatabaseBinding connection
    printfn "correctness=passed (varying values, NULL/missing rows, and prepared database binding)"

    check "create performance table" (connection.Query "CREATE TABLE perf (id INT PRIMARY KEY, value INT)")

    for batch in [ 1 .. 1000 ] |> List.chunkBySize 100 do
        let values = batch |> List.map (fun n -> sprintf "(%d,%d)" n n) |> String.concat ","
        check "seed performance table" (connection.Query("INSERT INTO perf VALUES " + values))

    use prepared = prepare connection "SELECT value FROM perf WHERE id=?"

    let cases =
        [ "Query literal", (fun () -> connection.Query "SELECT value FROM perf WHERE id=42" |> check "Query literal")
          "Execute parameter", (fun () -> connection.Execute("SELECT value FROM perf WHERE id=?", [ VInt 42L ]) |> check "Execute parameter")
          "QueryValues parameter", (fun () ->
              match connection.QueryValues("SELECT value FROM perf WHERE id=?", [ VInt 42L ]) with
              | Ok _ -> ()
              | Error error -> failwithf "QueryValues parameter failed: %A" error)
          "Prepared Execute", (fun () -> prepared.Execute [ VInt 42L ] |> check "Prepared Execute")
          "Prepared QueryValues", (fun () ->
              match prepared.QueryValues [ VInt 42L ] with
              | Ok _ -> ()
              | Error error -> failwithf "Prepared QueryValues failed: %A" error) ]

    for _, action in cases do
        for _ in 1 .. 50 do
            action ()

    let samples = Dictionary<string, ResizeArray<Measurement>>()
    for name, _ in cases do
        samples.Add(name, ResizeArray())

    for trial in 1 .. trials do
        let ordered = if trial % 2 = 0 then List.rev cases else cases
        for name, action in ordered do
            samples.[name].Add(measure iterations action)

    printfn "case,median_us_per_op,median_bytes_per_op"
    for name, _ in cases do
        let measurements = samples.[name]
        printfn
            "%s,%.1f,%.0f"
            name
            (measurements |> Seq.map _.MicrosecondsPerOperation |> median)
            (measurements |> Seq.map _.BytesPerOperation |> median)

    0
